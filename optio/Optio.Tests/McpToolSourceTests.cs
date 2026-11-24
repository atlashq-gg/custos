using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Optio.Harness;
using Optio.Tests.Infrastructure;
using static Optio.Tests.Infrastructure.FakeMembers;

namespace Optio.Tests;

/// <summary>The read tools of an MCP service, against a small MCP server in the test host. Needs
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

        await using var session = await source.OpenAsync("custos-token", Guild, default);
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
            Assert.That(_server.Authorizations, Has.All.EqualTo("Bearer exchanged-1"));
            Assert.That(
                _factory.AuthorizationServer.Requests.Single().Form["scope"],
                Is.EqualTo("custos:read")
            );
        }
    }
}
