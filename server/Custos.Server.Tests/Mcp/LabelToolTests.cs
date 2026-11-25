using System.Net;
using System.Text.Json;
using Custos.Server.Discord;
using Custos.Server.Mcp;
using Custos.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Custos.Server.Tests.Infrastructure.UploadRequests;
using static Custos.Server.Tests.Mcp.McpClients;

namespace Custos.Server.Tests.Mcp;

public sealed class LabelToolTests
{
    private const ulong Guild = 300000000000000001;
    private const ulong Sharer = 300000000000000002;
    private const ulong Labeler = 400000000000000001;
    private const ulong Reader = 400000000000000002;
    private const ulong LabelRole = 100000000000000003;

    private CustosFactory _factory = null!;

    [SetUp]
    public async Task SetUp()
    {
        var discord = new FakeDiscordDirectory()
            .Add(Guild, Labeler, roleIds: LabelRole)
            .Add(Guild, Reader)
            .Add(Sharer, Labeler, administrator: true);
        _factory = new CustosFactory(discord: discord, mcp: true);
        await using var scope = _factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<GuildSettingsStore>();
        await settings.SaveAsync(
            Guild,
            GuildSettings.Default with
            {
                Access = new AccessSettings { LabelRoleIds = [LabelRole] },
            },
            Labeler,
            "test",
            default
        );
        await settings.SaveAsync(
            Sharer,
            GuildSettings.Default with
            {
                Sharing = new SharingSettings { ReadGuildIds = [Guild] },
            },
            Labeler,
            "test",
            default
        );
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Member_with_label_changes_and_removes_a_label()
    {
        var id = await UploadAsync(Guild);

        var set = await CallAsync(Labeler, id, "Main Depot");
        var reset = await CallAsync(Labeler, id, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(set.IsError, Is.Not.True, Text(set));
            var changed = JsonDocument.Parse(Text(set)).RootElement;
            Assert.That(changed.GetProperty("new_label").GetString(), Is.EqualTo("Main Depot"));
            Assert.That(
                changed.GetProperty("old_label").GetString(),
                Does.StartWith("Origin Test Depot")
            );
            Assert.That(
                JsonDocument.Parse(Text(reset)).RootElement.GetProperty("new_label").GetString(),
                Does.StartWith("Origin Test Depot")
            );
        }
    }

    [Test]
    public async Task Member_without_label_is_refused()
    {
        var id = await UploadAsync(Guild);

        var result = await CallAsync(Reader, id, "Main Depot");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(Text(result), Does.Contain(McpProblem.ForbiddenType));
        }
    }

    [Test]
    public async Task Label_against_the_rules_is_an_error_result()
    {
        var id = await UploadAsync(Guild);

        var result = await CallAsync(Labeler, id, "<script>");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(Text(result), Does.Contain("permits letters, digits"));
        }
    }

    [Test]
    public async Task Structure_of_a_guild_that_shares_is_not_visible()
    {
        var id = await UploadAsync(Sharer);

        var result = await CallAsync(Labeler, id, "Not Mine");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(Text(result), Does.Contain(McpProblem.NotVisibleType));
        }
    }

    [Test]
    public async Task Read_token_lists_the_tool_but_its_call_needs_the_write_scope()
    {
        var structureId = await UploadAsync(Guild);
        using var http = _factory.CreateMcpClient(Labeler.ToString(), "custos:read");
        await using var client = await ConnectAsync(http);

        var tools = await client.ListToolsAsync();
        var call = await OutcomeAsync(() =>
            client.CallToolAsync(
                "set_structure_label",
                new Dictionary<string, object?>
                {
                    ["guild_id"] = Guild.ToString(),
                    ["structure_id"] = structureId,
                    ["label"] = "Main Depot",
                }
            )
        );

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                tools.Select(t => t.Name),
                Is.EquivalentTo(
                    new[] { "find_items", "find_structures", "list_guilds", "set_structure_label" }
                )
            );
            Assert.That(call, Does.Contain("insufficient_scope").Or.Contain("403"));
        }
    }

    /// <summary>The result text of a call, or the message of its exception: a call without the
    /// scope is an HTTP 403, which the client gives as an exception.</summary>
    private static async Task<string> OutcomeAsync(
        Func<ValueTask<ModelContextProtocol.Protocol.CallToolResult>> call
    )
    {
        try
        {
            return Text(await call());
        }
        catch (Exception e)
        {
            return e.ToString();
        }
    }

    [Test]
    public async Task Tool_has_the_annotations_of_a_write_that_is_not_destructive()
    {
        using var http = _factory.CreateMcpClient(Labeler.ToString());
        await using var client = await ConnectAsync(http);

        var tool = (await client.ListToolsAsync()).Single(t => t.Name == "set_structure_label");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tool.ProtocolTool.Annotations?.ReadOnlyHint, Is.False);
            Assert.That(tool.ProtocolTool.Annotations?.DestructiveHint, Is.False);
            Assert.That(tool.ProtocolTool.Annotations?.IdempotentHint, Is.True);
        }
    }

    private async Task<string> UploadAsync(ulong guild)
    {
        using var client = _factory.CreateAuthorizedClient(
            guild: guild.ToString(),
            discordId: Labeler.ToString()
        );
        using var response = await client.PostAsync(UploadPath, Body(SaveFile));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var db = _factory.CreateDb();
        return (
            await db
                .Structures.Where(s => s.GuildId == (long)guild)
                .Select(s => s.StructureId)
                .SingleAsync()
        ).ToString();
    }

    private async Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(
        ulong user,
        string structureId,
        string? label
    )
    {
        using var http = _factory.CreateMcpClient(user.ToString());
        await using var client = await ConnectAsync(http);
        return await client.CallToolAsync(
            "set_structure_label",
            new Dictionary<string, object?>
            {
                ["guild_id"] = Guild.ToString(),
                ["structure_id"] = structureId,
                ["label"] = label,
            }
        );
    }
}
