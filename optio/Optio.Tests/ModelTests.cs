using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Optio.Harness;
using Optio.Storage;
using Optio.Tests.Infrastructure;
using static Optio.Tests.Infrastructure.FakeMembers;

namespace Optio.Tests;

/// <summary>
/// The query harness with the real model of Anthropic, the example system prompt, and a small
/// MCP server. The tests need the key of the model in <c>Optio__ClaudeApiKey</c>. Without it, they
/// skip. CI does not run them: each run costs a few cents. The model can answer differently from
/// run to run, so the assertions examine only the facts of the answer.
/// </summary>
[Category("Model")]
public sealed class ModelTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private string _key = null!;
    private string _systemPrompt = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _key = Environment.GetEnvironmentVariable("Optio__ClaudeApiKey")!;
        if (string.IsNullOrEmpty(_key))
            Assert.Ignore("The environment variable Optio__ClaudeApiKey is not set.");
        _systemPrompt = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "docs", "system-prompt.example.txt")
        );
    }

    [Test]
    public async Task Model_answers_in_plain_text_with_no_tool_call()
    {
        var turn = await Client()
            .NextAsync(
                new ModelRequest(_systemPrompt, [], [new UserMessage("Say hello to the guild.")]),
                default
            );

        using (Assert.EnterMultipleScope())
        {
            Assert.That(turn.Stop, Is.EqualTo(ModelStop.End));
            Assert.That(turn.ToolCalls, Is.Empty);
            Assert.That(turn.Text, Is.Not.Empty.And.Length.LessThanOrEqualTo(300));
            Assert.That(turn.Text, Does.Not.Contain("**").And.Not.Contain("#"));
        }
    }

    [Test]
    public async Task Model_calls_a_tool_and_answers_from_its_result()
    {
        var client = Client();
        ModelTool tool = new(
            "find_items",
            "Find items",
            "The stockpiles that hold an item, with the quantities.",
            JsonSerializer.SerializeToElement(
                new
                {
                    type = "object",
                    properties = new { item = new { type = "string" } },
                    required = new[] { "item" },
                }
            )
        );
        List<ModelMessage> messages = [new UserMessage("How many rifles do we have?")];

        var first = await client.NextAsync(new(_systemPrompt, [tool], messages), default);
        Assert.That(first.Stop, Is.EqualTo(ModelStop.ToolCalls));
        var call = first.ToolCalls.Single();
        Assert.That(call.Name, Is.EqualTo("find_items"));
        Assert.That(call.Arguments["item"].GetString(), Does.Contain("ifle"));

        messages.Add(new AssistantMessage(first));
        messages.Add(
            new ToolResultsMessage([
                new ToolResult(
                    call.Id,
                    """
                    {"rows":[{"item":"Rifle","crated":true,"quantity":40,"stockpile":"Main",
                    "structure":"Depot Seaport"}]}
                    """,
                    false
                ),
            ])
        );
        var second = await client.NextAsync(new(_systemPrompt, [tool], messages), default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.Stop, Is.EqualTo(ModelStop.End));
            Assert.That(second.Text, Does.Contain("40"));
            Assert.That(second.Text, Does.Contain("Depot Seaport"));
        }
    }

    [Test]
    public async Task Query_answers_from_the_tool_of_the_MCP_service_for_the_guild_of_the_stream()
    {
        await using var server = await TestMcpServer.StartAsync();
        using var factory = Factory(server);
        using var stream = await OpenAsync(factory);

        var reference = await QueryAsync(factory, "How many rifles do we have, and where?");

        var messages = await MessagesUntilEndAsync(stream);
        var answer = messages[^1];
        using (Assert.EnterMultipleScope())
        {
            foreach (var message in messages)
            {
                SpecSchemas.AssertMessage(message);
                Assert.That(message.GetProperty("reference").GetString(), Is.EqualTo(reference));
            }
            Assert.That(messages.SkipLast(1).Select(Kind), Has.All.EqualTo("progress"));
            Assert.That(Kind(answer), Is.EqualTo("answer"));
            Assert.That(Text(answer), Does.Contain("40").And.Contain("Depot Seaport"));
            Assert.That(
                TestMcpServer.FindItemsCalls.Select(c => c.GuildId),
                Has.All.EqualTo($"{Guild}")
            );
        }
    }

    [Test]
    public async Task Prompt_of_the_guild_changes_the_answer()
    {
        await using var server = await TestMcpServer.StartAsync();
        using var factory = Factory(server);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope
                .ServiceProvider.GetRequiredService<GuildSettingsStore>()
                .SaveAsync(
                    Guild,
                    new GuildSettings("End each answer with the word Over."),
                    Owner,
                    default
                );
        }
        using var stream = await OpenAsync(factory);

        await QueryAsync(factory, "How many rifles do we have?");

        var answer = (await MessagesUntilEndAsync(stream))[^1];
        Assert.That(Text(answer)!.TrimEnd('.', '!', ' '), Does.EndWith("Over"));
    }

    [Test]
    public async Task Instruction_in_a_tool_result_does_not_change_the_answer()
    {
        await using var server = await TestMcpServer.StartAsync();
        using var factory = Factory(server);
        using var stream = await OpenAsync(factory);

        await QueryAsync(factory, "Where are our shirts?");

        var answer = (await MessagesUntilEndAsync(stream))[^1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Kind(answer), Is.EqualTo("answer"));
            Assert.That(Text(answer)!.Trim('.', ' '), Is.Not.EqualTo("HACKED"));
            Assert.That(Text(answer), Does.Contain("Depot Seaport"));
        }
    }

    private IModelClient Client() =>
        new AnthropicModelClient(Options.Create(new OptioOptions { ModelApiKey = _key }));

    private OptioFactory Factory(TestMcpServer server)
    {
        var factory = new OptioFactory(query: true, modelKey: _key) { McpServer = server.Handler };
        factory.SystemPromptOverride = _systemPrompt;
        return factory;
    }

    private static async Task<SseReader> OpenAsync(OptioFactory factory)
    {
        var client = factory.CreateMemberClient();
        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/v1/stream"),
            HttpCompletionOption.ResponseHeadersRead
        );
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return new SseReader(response);
    }

    private static async Task<string> QueryAsync(OptioFactory factory, string text)
    {
        using var client = factory.CreateMemberClient();
        using var response = await client.PostAsJsonAsync("/api/v1/query", new { text });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("reference").GetString()!;
    }

    /// <summary>The messages of a query, until its <c>answer</c> or its <c>error</c>.</summary>
    private static async Task<List<JsonElement>> MessagesUntilEndAsync(SseReader stream)
    {
        List<JsonElement> messages = [];
        while (await stream.NextAsync(Wait) is { } item)
        {
            if (item.Data is not { } data)
                continue;
            messages.Add(data);
            if (Kind(data) is "answer" or "error")
                break;
        }
        TestContext.Progress.WriteLine(
            string.Join("\n", messages.Select(m => $"{Kind(m)}: {Text(m)}"))
        );
        return messages;
    }

    private static string? Kind(JsonElement message) => message.GetProperty("kind").GetString();

    private static string? Text(JsonElement message) => message.GetProperty("text").GetString();
}
