using System.ComponentModel;
using Custos.Server.Discord;
using Custos.Server.Storage;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Custos.Server.Mcp;

/// <summary>A list in a tool result.</summary>
public sealed record McpList<T>(IReadOnlyList<T> Items, int Total, object? Applied = null);

public sealed record GuildRef(string GuildId, string? Name);

public sealed record GuildSummary(
    string GuildId,
    string Name,
    IReadOnlyList<string> Access,
    int? CurrentWar,
    IReadOnlyList<GuildRef> SharedBy
);

[McpServerToolType]
public sealed class GuildTools(
    McpCallContext context,
    IServiceProvider services,
    IDiscordDirectory discord,
    GuildAccessEvaluator access,
    GuildSettingsStore settings,
    WarRegistry wars
) : McpTools(context, services)
{
    [McpServerTool(
        Name = "list_guilds",
        Title = "List guilds",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false
    )]
    [McpGuard(McpScope.Read, GuildAccess.None)]
    [Description(
        "The Discord guilds of the Custos bot where you are a member: the guild ID, the name, "
            + "your access, the current war, and the guilds that share their records with it."
    )]
    public Task<CallToolResult> ListGuilds(CancellationToken cancellationToken) =>
        RunAsync("list_guilds", async () =>
        {
            var caller = Context.CallerId!.Value;
            var guilds = discord.ListGuilds();
            var names = guilds.ToDictionary(g => g.Id, g => g.Name);
            var war = await wars.CurrentAsync(cancellationToken);
            var items = new List<GuildSummary>();
            foreach (var guild in guilds)
            {
                var check = await access.EvaluateAsync(guild.Id, caller, cancellationToken);
                if (check.Status != MemberStatus.Member)
                    continue;
                var sharedBy = await settings.GetSharingGuildsAsync(guild.Id, cancellationToken);
                items.Add(
                    new GuildSummary(
                        guild.Id.ToString(),
                        guild.Name,
                        Kinds(check.Access),
                        war?.WarNumber,
                        [
                            .. sharedBy.Select(id => new GuildRef(
                                id.ToString(),
                                names.GetValueOrDefault(id)
                            )),
                        ]
                    )
                );
            }
            var sorted = items
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.GuildId, StringComparer.Ordinal)
                .ToList();
            return new McpList<GuildSummary>(sorted, sorted.Count);
        });

    /// <summary>The kinds of access as names, for example <c>["read", "report"]</c>.</summary>
    internal static IReadOnlyList<string> Kinds(GuildAccess access) =>
        [
            .. Enum.GetValues<GuildAccess>()
                .Where(a => a != GuildAccess.None && access.HasFlag(a))
                .Select(a => a.ToString().ToLowerInvariant())
                .Order(StringComparer.Ordinal),
        ];
}
