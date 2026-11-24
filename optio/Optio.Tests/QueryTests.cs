using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Optio.Harness;
using Optio.Storage;
using Optio.Tests.Infrastructure;
using static Optio.Tests.Infrastructure.FakeMembers;

namespace Optio.Tests;

/// <summary>The query of spec/05-stream.md, with a fake model, fake tools, and a fake Discord.
/// Needs Docker for PostgreSQL.</summary>
public sealed class QueryTests
{
    private const string Path = "/api/v1/query";

    private OptioFactory _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new OptioFactory(query: true);

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Discovery_document_has_the_query_endpoint()
    {
        var document = await _factory
            .CreateClient()
            .GetFromJsonAsync<JsonElement>("/.well-known/custos-configuration");

        SpecSchemas.AssertDiscoveryDocument(document);
        Assert.That(
            document.GetProperty("query_endpoint").GetString(),
            Is.EqualTo(OptioFactory.PublicBaseUrl + Path)
        );
    }

    [Test]
    public async Task Query_sends_a_progress_message_for_each_tool_call_and_then_the_answer()
    {
        _factory.Model.Turns.Enqueue(FakeModel.Call(FakeTools.Tool));
        _factory.Model.Turns.Enqueue(FakeModel.Answer("40 rifle crates in Depot, Main."));
        using var stream = await OpenAsync();

        var reference = await QueryAsync("How many rifles?");

        var progress = await stream.NextAsync();
        var answer = await stream.NextAsync();
        using (Assert.EnterMultipleScope())
        {
            SpecSchemas.AssertMessage(progress!.Data!.Value);
            SpecSchemas.AssertMessage(answer!.Data!.Value);
            Assert.That(Kind(progress), Is.EqualTo("progress"));
            Assert.That(Text(progress), Is.EqualTo("Find items…"));
            Assert.That(Kind(answer), Is.EqualTo("answer"));
            Assert.That(Text(answer), Is.EqualTo("40 rifle crates in Depot, Main."));
            Assert.That(Reference(progress), Is.EqualTo(reference));
            Assert.That(Reference(answer), Is.EqualTo(reference));
            Assert.That(
                await _factory.Tools.Calls.Reader.ReadAsync(),
                Is.EqualTo((Guild, FakeTools.Tool))
            );
            Assert.That(
                _factory
                    .Model.Requests[1]
                    .Messages.OfType<ToolResultsMessage>()
                    .Single()
                    .Results[0]
                    .Content,
                Is.EqualTo($"{FakeTools.Tool} in {Guild}")
            );
        }
    }

    [Test]
    public async Task System_prompt_holds_the_guild_and_the_prompt_of_the_guild()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope
                .ServiceProvider.GetRequiredService<GuildSettingsStore>()
                .SaveAsync(
                    Guild,
                    new GuildSettings("Answer like a quartermaster."),
                    Owner,
                    default
                );
        }
        _factory.Model.Turns.Enqueue(FakeModel.Answer("Yes."));
        using var stream = await OpenAsync();

        await QueryAsync("Any rifles?");
        await stream.NextAsync();

        var system = _factory.Model.Requests.Single().System;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(system, Does.StartWith(OptioFactory.SystemPrompt));
            Assert.That(system, Does.Contain(Guild.ToString()));
            Assert.That(system, Does.Contain("Answer like a quartermaster."));
        }
    }

    [Test]
    public async Task Query_with_too_many_tool_calls_ends_with_an_error()
    {
        _factory.Model.Turns.Enqueue(
            FakeModel.Call([.. Enumerable.Repeat(FakeTools.Tool, QueryHarness.MaxToolCalls + 1)])
        );
        using var stream = await OpenAsync();

        await QueryAsync("Everything?");

        Assert.That(Kind(await stream.NextAsync()), Is.EqualTo("error"));
    }

    [Test]
    public async Task Query_that_the_model_refuses_ends_with_an_error()
    {
        _factory.Model.Turns.Enqueue(new ModelTurn("", [], ModelStop.Refusal, new object()));
        using var stream = await OpenAsync();

        await QueryAsync("Something bad?");

        Assert.That(Kind(await stream.NextAsync()), Is.EqualTo("error"));
    }

    [Test]
    public async Task Query_that_takes_too_long_ends_with_an_error_and_the_user_can_ask_again()
    {
        using var stream = await OpenAsync();
        await QueryAsync("Slow?");
        Assert.That(() => _factory.Model.Requests, Has.Count.EqualTo(1).After(5000, 20));

        _factory.Time.Advance(QueryHarness.MaxDuration);

        // The advance also gives keep-alive comments.
        SseItem? item;
        do item = await stream.NextAsync();
        while (item?.Comment is not null);
        Assert.That(Kind(item), Is.EqualTo("error"));
        _factory.Model.Turns.Enqueue(FakeModel.Answer("Fast."));
        Assert.That(
            () => PostAsync(new { text = "Again?" }),
            Is.EqualTo(HttpStatusCode.Accepted).After(5000, 50)
        );
    }

    [Test]
    public async Task Second_query_while_one_runs_gets_429()
    {
        using var stream = await OpenAsync();
        await QueryAsync("Slow?");

        using var client = _factory.CreateMemberClient();
        using var response = await client.PostAsJsonAsync(Path, new { text = "Another?" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(response.Headers.RetryAfter?.Delta, Is.Not.Null);
        }
    }

    [Test]
    public async Task Query_without_an_open_stream_gets_409() =>
        Assert.That(await PostAsync(new { text = "Hello?" }), Is.EqualTo(HttpStatusCode.Conflict));

    [Test]
    public async Task Answer_to_a_confirmation_gets_409_until_confirmations_exist()
    {
        using var stream = await OpenAsync();

        Assert.That(
            await PostAsync(new { confirm = "confirm:1", accept = true }),
            Is.EqualTo(HttpStatusCode.Conflict)
        );
    }

    [TestCase("")]
    [TestCase(null)]
    public async Task Query_without_valid_text_gets_400(string? text)
    {
        using var stream = await OpenAsync();

        Assert.That(await PostAsync(new { text }), Is.EqualTo(HttpStatusCode.BadRequest));
    }

    private async Task<SseReader> OpenAsync()
    {
        var client = _factory.CreateMemberClient();
        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/v1/stream"),
            HttpCompletionOption.ResponseHeadersRead
        );
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return new SseReader(response);
    }

    private async Task<string> QueryAsync(string text)
    {
        using var client = _factory.CreateMemberClient();
        using var response = await client.PostAsJsonAsync(Path, new { text });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        SpecSchemas.AssertQueryAccepted(body);
        return body.GetProperty("reference").GetString()!;
    }

    private async Task<HttpStatusCode> PostAsync(object body)
    {
        using var client = _factory.CreateMemberClient();
        using var response = await client.PostAsJsonAsync(Path, body);
        return response.StatusCode;
    }

    private static string? Kind(SseItem? item) => item?.Data?.GetProperty("kind").GetString();

    private static string? Text(SseItem? item) => item?.Data?.GetProperty("text").GetString();

    private static string? Reference(SseItem? item) =>
        item?.Data?.GetProperty("reference").GetString();
}
