using Custos.Server.Discord;

namespace Custos.Server.Tests.Infrastructure;

/// <summary>
/// Discord for the tests. A member is in <see cref="Members"/>. A guild in <see cref="Guilds"/>
/// has the bot. <see cref="Unavailable"/> makes each request fail as a Discord outage.
/// </summary>
internal sealed class FakeDiscordDirectory : IDiscordDirectory
{
    public HashSet<ulong> Guilds { get; } = [];
    public Dictionary<(ulong Guild, ulong User), MemberFacts> Members { get; } = [];
    public bool Unavailable { get; set; }
    public int Calls { get; private set; }

    /// <summary>Adds a member, and the bot to the guild of the member.</summary>
    public FakeDiscordDirectory Add(
        ulong guildId,
        ulong userId,
        bool administrator = false,
        params ulong[] roleIds
    )
    {
        Guilds.Add(guildId);
        Members[(guildId, userId)] = new MemberFacts(roleIds, administrator);
        return this;
    }

    public Task<MemberLookup> GetMemberAsync(
        ulong guildId,
        ulong userId,
        CancellationToken cancellationToken
    )
    {
        Calls++;
        if (Unavailable)
            throw new DiscordUnavailableException("The fake Discord is unavailable.");
        if (!Guilds.Contains(guildId))
            return Task.FromResult(MemberLookup.BotNotInGuild);
        return Task.FromResult(
            Members.TryGetValue((guildId, userId), out var facts)
                ? new MemberLookup(MemberStatus.Member, facts)
                : MemberLookup.NotMember
        );
    }
}
