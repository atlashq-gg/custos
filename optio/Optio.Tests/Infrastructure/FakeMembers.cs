using NetCord;
using Optio.Discord;
using Optio.Streams;

namespace Optio.Tests.Infrastructure;

/// <summary>
/// Discord for the tests: the guild <see cref="Guild"/> with the owner <see cref="Owner"/>, the
/// role <see cref="ReadRole"/>, and the member <see cref="Reader"/> with that role.
/// </summary>
internal sealed class FakeMembers : IMemberDirectory
{
    public const ulong Guild = 300000000000000001;
    public const ulong OtherGuild = 300000000000000002;
    public const ulong Owner = 400000000000000001;
    public const ulong Reader = 400000000000000002;
    public const ulong ReadRole = 500000000000000001;
    public const ulong OtherRole = 500000000000000002;

    private readonly Dictionary<(ulong Guild, ulong User), int> _connected = [];

    public Dictionary<(ulong Guild, ulong User), ulong[]> Members { get; } =
        new() { [(Guild, Owner)] = [], [(Guild, Reader)] = [ReadRole] };

    public HashSet<ulong> BotGuilds { get; } = [Guild];

    public HashSet<ulong> NotReady { get; } = [];

    public bool Unavailable { get; set; }

    /// <summary>The open streams that the directory follows.</summary>
    public int Connected
    {
        get
        {
            lock (_connected)
                return _connected.Values.Sum();
        }
    }

    public event Action<ulong>? GuildReady;

    public void MakeReady(ulong guild)
    {
        NotReady.Remove(guild);
        GuildReady?.Invoke(guild);
    }

    public Task<MemberState> ConnectAsync(
        ulong guild,
        ulong user,
        CancellationToken cancellationToken
    )
    {
        lock (_connected)
            _connected[(guild, user)] = _connected.GetValueOrDefault((guild, user)) + 1;
        return Task.FromResult(
            Unavailable ? MemberState.Unavailable
            : !BotGuilds.Contains(guild) ? MemberState.NoBot
            : Members.ContainsKey((guild, user)) ? MemberState.Member
            : MemberState.NotMember
        );
    }

    public void Disconnect(ulong guild, ulong user)
    {
        lock (_connected)
            _connected[(guild, user)]--;
    }

    public bool HasGuild(ulong guild) => BotGuilds.Contains(guild);

    public bool? CanRead(EventPolicy policy, ulong guild, ulong user)
    {
        if (NotReady.Contains(guild))
            return null;
        if (!Members.TryGetValue((guild, user), out var roles))
            return false;
        return AccessPolicy.CanRead(
            policy,
            new GuildFacts(
                guild,
                Owner,
                new Dictionary<ulong, Permissions>
                {
                    [guild] = Permissions.ViewChannel,
                    [ReadRole] = Permissions.ViewChannel,
                    [OtherRole] = Permissions.ViewChannel,
                }
            ),
            new MemberFacts(user, roles)
        );
    }
}
