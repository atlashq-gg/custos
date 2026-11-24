using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Optio.Harness;
using Optio.Tests.Infrastructure;
using static Optio.Tests.Infrastructure.FakeMembers;

namespace Optio.Tests;

/// <summary>The tools of an MCP service, against a small MCP server in the test host. Needs
/// Docker for PostgreSQL.</summary>
public sealed class McpToolSourceTests
{
    private TestMcpServer _server = null!;
    private OptioFactory _factory = null!;

    [SetUp]
    public async Task StartAsync()
    {
        _server = await TestMcpServer.StartAsync();
        _factory = new OptioFactory(query: true) { McpServer = _server.Handler };
    }

    [TearDown]
    public async Task StopAsync()
    {
        _factory.Dispose();
        await _server.DisposeAsync();
    }

    [Test]
    public async Task Tools_hide_the_guild_and_a_call_gets_the_guild_of_the_stream()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var source = ActivatorUtilities.CreateInstance<McpToolSource>(scope.ServiceProvider);

        await using var session = await source.OpenAsync(
            new ToolUser("custos-token", Guild, Reader),
            default
        );
        var findItems = session.Tools.Single(t => t.Name == "find_items");
        var result = await session.CallAsync(
            new ToolCall(
                "call-1",
                "find_items",
                new Dictionary<string, JsonElement>
                {
                    ["item"] = JsonSerializer.SerializeToElement("rifle"),
                    ["guild_id"] = JsonSerializer.SerializeToElement("1"),
                }
            ),
            default
        );
        var guilds = await session.CallAsync(
            new ToolCall("call-2", "list_guilds", new Dictionary<string, JsonElement>()),
            default
        );

        using (Assert.EnterMultipleScope())
        {
            Assert.That(findItems.Title, Is.EqualTo("Find items"));
            var properties = findItems.Schema.GetProperty("properties");
            Assert.That(properties.TryGetProperty("guild_id", out _), Is.False);
            Assert.That(properties.TryGetProperty("item", out _), Is.True);
            Assert.That(TestMcpServer.FindItemsCalls, Is.EqualTo(new[] { ($"{Guild}", "rifle") }));
            Assert.That(result.Content, Does.Contain("Depot Seaport"));
            Assert.That(result.IsError, Is.False);
            Assert.That(guilds.Content, Is.EqualTo("no guilds"));
            Assert.That(
                session.Tools.Select(t => t.Name),
                Is.EquivalentTo(new[] { "find_items", "find_structures", "list_guilds" })
            );
            Assert.That(
                _server.Authorizations.Distinct(),
                Is.EqualTo(new[] { "Bearer exchanged-1", "Bearer exchanged-2" })
            );
            Assert.That(
                _factory.AuthorizationServer.Requests.Select(r => r.Form["scope"]),
                Is.EqualTo(new[] { "custos:read", "custos:read custos:write" })
            );
        }
    }

    [Test]
    public async Task Write_tool_is_only_in_the_write_list_and_its_call_uses_a_write_token()
    {
        TestMcpServer.CanWrite = token =>
            _factory.AuthorizationServer.ScopeOf(token)?.Contains("custos:write") == true;
        await using var scope = _factory.Services.CreateAsyncScope();
        var source = ActivatorUtilities.CreateInstance<McpToolSource>(scope.ServiceProvider);
        await using var session = await source.OpenAsync(
            new ToolUser("custos-token", Guild, Reader),
            default
        );
        var call = new ToolCall(
            "call-1",
            TestMcpServer.LabelTool,
            new Dictionary<string, JsonElement>
            {
                ["structure_id"] = JsonSerializer.SerializeToElement("s-1"),
                ["label"] = JsonSerializer.SerializeToElement("Main Depot"),
            }
        );

        var refused = await session.CallAsync(call, default);
        var write = session.AsWrite(call)!;
        var result = await source.CallAsync(
            new ToolUser("answer-token", Guild, Reader),
            write,
            default
        );

        var tool = session.Tools.Single(t => t.Name == TestMcpServer.LabelTool);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tool.Write, Is.True);
            Assert.That(session.Tools.Single(t => t.Name == "find_items").Write, Is.False);
            Assert.That(
                tool.Schema.GetProperty("properties").TryGetProperty("guild_id", out _),
                Is.False
            );
            Assert.That(session.AsWrite(call with { Name = "find_items" }), Is.Null);
            Assert.That(refused.IsError, Is.True);
            Assert.That(write.Title, Is.EqualTo("Set the label of a structure"));
            Assert.That(write.Shown, Is.EqualTo(new[] { "label" }));
            Assert.That(result.IsError, Is.False);
            Assert.That(
                TestMcpServer.LabelCalls,
                Is.EqualTo(new[] { ("Bearer exchanged-3", $"{Guild}", "s-1", "Main Depot") })
            );
            var exchange = _factory.AuthorizationServer.Requests[2].Form;
            Assert.That(exchange["scope"], Is.EqualTo("custos:write"));
            Assert.That(exchange["subject_token"], Is.EqualTo("answer-token"));
        }
    }
}
