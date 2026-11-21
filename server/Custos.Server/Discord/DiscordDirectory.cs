using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

namespace Custos.Server.Discord;

/// <summary>What Discord knows about a user in a guild.</summary>
public enum MemberStatus
{
    Member,
    NotMember,

    /// <summary>The bot is not in the guild, so the server cannot read its members.</summary>
    BotNotInGuild,
}

/// <summary>A guild of the bot.</summary>
public sealed record DiscordGuild(ulong Id, string Name);

public sealed record MemberLookup(MemberStatus Status, MemberFacts? Facts = null)
{
    public static readonly MemberLookup NotMember = new(MemberStatus.NotMember);
    public static readonly MemberLookup BotNotInGuild = new(MemberStatus.BotNotInGuild);
}

/// <summary>Discord did not answer, or answered with <c>429</c> or a <c>5xx</c> status.</summary>
public sealed class DiscordUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// The port to Discord for access checks. The tests replace it with a fake, so that they run
/// without Discord.
/// </summary>
public interface IDiscordDirectory
{
    /// <summary>The guilds of the bot, from the gateway cache.</summary>
    IReadOnlyList<DiscordGuild> ListGuilds();

    /// <summary>
    /// The member <paramref name="userId"/> of the guild <paramref name="guildId"/>, from a request
    /// to Discord. Throws <see cref="DiscordUnavailableException"/>.
    /// </summary>
    Task<MemberLookup> GetMemberAsync(
        ulong guildId,
        ulong userId,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Reads the member with Get Guild Member, which needs no privileged intent. The roles and the
/// owner of the guild come from the gateway cache. The intent <c>Guilds</c> keeps them current.
/// Before the gateway delivers a guild, the server gets the guild through REST.
/// </summary>
internal sealed class GatewayDirectory(GatewayClient client) : IDiscordDirectory
{
    public IReadOnlyList<DiscordGuild> ListGuilds() =>
        [.. client.Cache.Guilds.Values.Select(g => new DiscordGuild(g.Id, g.Name))];

    public async Task<MemberLookup> GetMemberAsync(
        ulong guildId,
        ulong userId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            RestGuild? guild = client.Cache.Guilds.TryGetValue(guildId, out var cached)
                ? cached
                : await TryGetGuildAsync(guildId, cancellationToken);
            if (guild is null)
                return MemberLookup.BotNotInGuild;

            GuildUser user;
            try
            {
                user = await client.Rest.GetGuildUserAsync(
                    guildId,
                    userId,
                    cancellationToken: cancellationToken
                );
            }
            catch (RestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return MemberLookup.NotMember;
            }

            return new MemberLookup(
                MemberStatus.Member,
                new MemberFacts(
                    [.. user.RoleIds],
                    guild.OwnerId == userId || IsAdministrator(guild, user.RoleIds)
                )
            );
        }
        catch (RestException e) when ((int)e.StatusCode >= 500)
        {
            throw new DiscordUnavailableException($"Discord answered {(int)e.StatusCode}.", e);
        }
        catch (Exception e) when (e is RestRateLimitedException or HttpRequestException)
        {
            throw new DiscordUnavailableException("Discord did not answer.", e);
        }
    }

    /// <summary>The guild, or <c>null</c> when the bot is not in it.</summary>
    private async Task<RestGuild?> TryGetGuildAsync(ulong guildId, CancellationToken token)
    {
        try
        {
            return await client.Rest.GetGuildAsync(guildId, cancellationToken: token);
        }
        catch (RestException e) when ((int)e.StatusCode is 403 or 404)
        {
            return null;
        }
    }

    /// <summary>The permissions of <c>@everyone</c> and of each role of the member.</summary>
    private static bool IsAdministrator(RestGuild guild, IEnumerable<ulong> roleIds)
    {
        var permissions = guild.Roles.TryGetValue(guild.Id, out var everyone)
            ? everyone.Permissions
            : default;
        foreach (var roleId in roleIds)
        {
            if (guild.Roles.TryGetValue(roleId, out var role))
                permissions |= role.Permissions;
        }
        return permissions.HasFlag(Permissions.Administrator);
    }
}
