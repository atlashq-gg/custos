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
    public async Task Write_call_waits_for_the_confirmation_and_the_model_answers_after_the_call()
    {
        const string question = "Set Label? \"Main Depot\"";
        _factory.Model.Turns.Enqueue(LabelCall("Main Depot", question));
        _factory.Model.Turns.Enqueue(FakeModel.Answer("The seaport is now Main Depot."));
        using var stream = await OpenAsync();
        var reference = await QueryAsync("Call the seaport Main Depot.");

        var confirm = await stream.NextAsync();
        Assert.That(_factory.Model.Requests, Has.Count.EqualTo(1));
        var accepted = await AnswerAsync(reference, accept: true);
        var progress = await stream.NextAsync();
        var answer = await stream.NextAsync();

        var (token, write) = await _factory.Tools.Writes.Reader.ReadAsync();
        var tool = _factory.Model.Requests[0].Tools.Single(t => t.Name == FakeTools.WriteTool);
        var result = _factory.Model.Requests[1].Messages.OfType<ToolResultsMessage>().Single();
        using (Assert.EnterMultipleScope())
        {
            SpecSchemas.AssertMessage(confirm!.Data!.Value);
            Assert.That(Kind(confirm), Is.EqualTo("confirm"));
            Assert.That(Text(confirm), Is.EqualTo(question));
            Assert.That(Reference(confirm), Is.EqualTo(reference));
            Assert.That(accepted, Is.EqualTo(reference));
            Assert.That(Text(progress), Is.EqualTo($"{FakeTools.WriteTitle}…"));
            Assert.That(Kind(answer), Is.EqualTo("answer"));
            Assert.That(Text(answer), Is.EqualTo("The seaport is now Main Depot."));
            Assert.That(Reference(answer), Is.EqualTo(reference));
            Assert.That(
                write.Call.Arguments.Keys,
                Is.EquivalentTo(new[] { "structure_id", "label" })
            );
            Assert.That(token, Is.Not.Empty);
            Assert.That(
                tool.Schema.GetProperty("properties").TryGetProperty("confirm_text", out _),
                Is.True
            );
            Assert.That(result.Results.Single().CallId, Is.EqualTo(write.Call.Id));
        }
    }

    [Test]
    public async Task Calls_of_a_write_turn_run_only_after_the_accept()
    {
        _factory.Model.Turns.Enqueue(
            FakeModel.Call(
                (FakeTools.Tool, []),
                (FakeTools.WriteTool, new() { ["label"] = "Main Depot" })
            )
        );
        _factory.Model.Turns.Enqueue(FakeModel.Answer("Done."));
        using var stream = await OpenAsync();
        var reference = await QueryAsync("Find rifles and call the seaport Main Depot.");

        var confirm = await stream.NextAsync();
        var readBefore = _factory.Tools.Calls.Reader.TryRead(out _);
        await AnswerAsync(reference, accept: true);
        await stream.NextAsync();
        await stream.NextAsync();
        var answer = await stream.NextAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Kind(confirm), Is.EqualTo("confirm"));
            Assert.That(Text(confirm), Is.EqualTo($"{FakeTools.WriteTitle}? \"Main Depot\""));
            Assert.That(readBefore, Is.False);
            Assert.That(Text(answer), Is.EqualTo("Done."));
            Assert.That(_factory.Tools.Calls.Reader.TryRead(out _), Is.True);
            Assert.That(_factory.Tools.Writes.Reader.TryRead(out _), Is.True);
        }
    }

    [Test]
    public async Task Write_call_after_the_accept_asks_again()
    {
        _factory.Model.Turns.Enqueue(LabelCall("Main Depot"));
        _factory.Model.Turns.Enqueue(LabelCall("North Bunker"));
        using var stream = await OpenAsync();
        var reference = await QueryAsync("Call the seaport Main Depot, then North Bunker.");
        await stream.NextAsync();

        await AnswerAsync(reference, accept: true);
        await stream.NextAsync();
        var second = await stream.NextAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Kind(second), Is.EqualTo("confirm"));
            Assert.That(Text(second), Is.EqualTo($"{FakeTools.WriteTitle}? \"North Bunker\""));
            Assert.That(Reference(second), Is.EqualTo(reference));
        }
    }

    [Test]
    public async Task Accept_while_another_query_of_the_user_runs_gets_429()
    {
        _factory.Model.Turns.Enqueue(LabelCall("Main Depot"));
        using var stream = await OpenAsync();
        using var other = await OpenAsync("session-2");
        var reference = await QueryAsync("Call the seaport Main Depot.");
        await stream.NextAsync();
        // No turn left: the query of the other client runs until it times out. The first query
        // frees its slot just after the confirm message.
        Assert.That(
            () => PostAsync(new { text = "Slow?" }, "session-2"),
            Is.EqualTo(HttpStatusCode.Accepted).After(5000, 50)
        );

        using var client = _factory.CreateMemberClient();
        using var response = await client.PostAsJsonAsync(
            Path,
            new { confirm = reference, accept = true }
        );

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(response.Headers.RetryAfter?.Delta, Is.Not.Null);
        }
    }

    [Test]
    public async Task Refused_confirmation_removes_the_line_and_makes_no_call()
    {
        _factory.Model.Turns.Enqueue(LabelCall("Main Depot"));
        using var stream = await OpenAsync();
        var reference = await QueryAsync("Call the seaport Main Depot.");
        await stream.NextAsync();

        await AnswerAsync(reference, accept: false);
        var removal = await stream.NextAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Reference(removal), Is.EqualTo(reference));
            Assert.That(removal!.Data!.Value.TryGetProperty("text", out _), Is.False);
            Assert.That(_factory.Tools.Writes.Reader.TryRead(out _), Is.False);
            Assert.That(
                await PostAsync(new { confirm = reference, accept = true }),
                Is.EqualTo(HttpStatusCode.Conflict)
            );
        }
    }

    [Test]
    public async Task Failed_write_call_goes_to_the_model()
    {
        _factory.Tools.WriteError = "The label is in use.";
        _factory.Model.Turns.Enqueue(LabelCall("Main Depot"));
        _factory.Model.Turns.Enqueue(FakeModel.Answer("Main Depot is in use."));
        using var stream = await OpenAsync();
        var reference = await QueryAsync("Call the seaport Main Depot.");
        await stream.NextAsync();

        await AnswerAsync(reference, accept: true);
        await stream.NextAsync();
        var answer = await stream.NextAsync();

        var result = _factory
            .Model.Requests[1]
            .Messages.OfType<ToolResultsMessage>()
            .Single()
            .Results.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Text(answer), Is.EqualTo("Main Depot is in use."));
            Assert.That(result.IsError, Is.True);
            Assert.That(result.Content, Is.EqualTo("The label is in use."));
        }
    }

    [Test]
    public async Task Expired_confirmation_gets_409()
    {
        _factory.Model.Turns.Enqueue(LabelCall("Main Depot"));
        using var stream = await OpenAsync();
        var reference = await QueryAsync("Call the seaport Main Depot.");
        await stream.NextAsync();

        _factory.Time.Advance(Confirmations.Lifetime);

        Assert.That(
            await PostAsync(new { confirm = reference, accept = true }),
            Is.EqualTo(HttpStatusCode.Conflict)
        );
    }

    [Test]
    public async Task Query_belongs_to_the_client_that_sent_it()
    {
        _factory.Model.Turns.Enqueue(LabelCall("Main Depot"));
        using var stream = await OpenAsync();
        using var other = await OpenAsync("session-2");
        var reference = await QueryAsync("Call the seaport Main Depot.");
        await stream.NextAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                await PostAsync(new { confirm = reference, accept = true }, "session-2"),
                Is.EqualTo(HttpStatusCode.Conflict)
            );
            Assert.That(await other.IsQuietAsync(TimeSpan.FromMilliseconds(200)), Is.True);
        }
    }

    [Test]
    public async Task New_query_of_the_client_ends_its_open_confirmation()
    {
        _factory.Model.Turns.Enqueue(LabelCall("Main Depot"));
        using var stream = await OpenAsync();
        var reference = await QueryAsync("Call the seaport Main Depot.");
        await stream.NextAsync();

        _factory.Model.Turns.Enqueue(FakeModel.Answer("Yes."));
        await QueryAsync("Any rifles?");
        var removal = await stream.NextAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Reference(removal), Is.EqualTo(reference));
            Assert.That(removal!.Data!.Value.TryGetProperty("text", out _), Is.False);
            Assert.That(
                await PostAsync(new { confirm = reference, accept = true }),
                Is.EqualTo(HttpStatusCode.Conflict)
            );
        }
    }

    [Test]
    public async Task Answer_without_accept_gets_400()
    {
        using var stream = await OpenAsync();

        Assert.That(
            await PostAsync(new { confirm = "query:1" }),
            Is.EqualTo(HttpStatusCode.BadRequest)
        );
    }

    [TestCase("")]
    [TestCase(null)]
    public async Task Query_without_valid_text_gets_400(string? text)
    {
        using var stream = await OpenAsync();

        Assert.That(await PostAsync(new { text }), Is.EqualTo(HttpStatusCode.BadRequest));
    }

    private async Task<SseReader> OpenAsync(string session = OptioFactory.DefaultSession)
    {
        var client = _factory.CreateMemberClient(session: session);
        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/v1/stream"),
            HttpCompletionOption.ResponseHeadersRead
        );
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return new SseReader(response);
    }

    private async Task<string> QueryAsync(string text, string session = OptioFactory.DefaultSession)
    {
        using var client = _factory.CreateMemberClient(session: session);
        using var response = await client.PostAsJsonAsync(Path, new { text });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        SpecSchemas.AssertQueryAccepted(body);
        return body.GetProperty("reference").GetString()!;
    }

    private async Task<HttpStatusCode> PostAsync(
        object body,
        string session = OptioFactory.DefaultSession
    )
    {
        using var client = _factory.CreateMemberClient(session: session);
        using var response = await client.PostAsJsonAsync(Path, body);
        return response.StatusCode;
    }

    private async Task<string> AnswerAsync(string reference, bool accept)
    {
        using var response = await _factory.AnswerAsync(reference, accept);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        SpecSchemas.AssertQueryAccepted(body);
        return body.GetProperty("reference").GetString()!;
    }

    /// <summary>A turn with a call of the write tool for a new label.</summary>
    private static ModelTurn LabelCall(string label, string? question = null)
    {
        Dictionary<string, object?> arguments = new()
        {
            ["structure_id"] = "s-1",
            ["label"] = label,
        };
        if (question is not null)
            arguments[Confirmations.TextArgument] = question;
        return FakeModel.Call((FakeTools.WriteTool, arguments));
    }

    private static string? Kind(SseItem? item) => item?.Data?.GetProperty("kind").GetString();

    private static string? Text(SseItem? item) => item?.Data?.GetProperty("text").GetString();

    private static string? Reference(SseItem? item) =>
        item?.Data?.GetProperty("reference").GetString();
}
