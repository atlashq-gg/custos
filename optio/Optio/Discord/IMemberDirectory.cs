using Optio.Streams;

namespace Optio.Discord;

/// <summary>The result of <see cref="IMemberDirectory.ConnectAsync"/>.</summary>
internal enum MemberState
{
    /// <summary>The user is a member of the guild.</summary>
    Member,

    /// <summary>The user is not a member of the guild.</summary>
    NotMember,

    /// <summary>The bot of Optio is not in the guild.</summary>
    NoBot,

    /// <summary>Discord did not answer in time.</summary>
    Unavailable,
}

/// <summary>The facts of a user in a guild for a query. Role IDs and administrator only for a
/// <see cref="MemberState.Member"/>.</summary>
internal sealed record MemberAccess(
    MemberState State,
    IReadOnlyCollection<ulong> RoleIds,
    bool IsAdministrator
)
{
    public static MemberAccess Not(MemberState state) => new(state, [], false);
}

/// <summary>The Discord facts of the users that have an open stream, and of a user that
/// asks.</summary>
internal interface IMemberDirectory
{
    /// <summary>A guild became ready, so that <see cref="CanRead"/> knows its facts.</summary>
    event Action<ulong>? GuildReady;

    /// <summary>Starts to follow a user for one more open stream. Each call needs one call of
    /// <see cref="Disconnect"/>, also when the result is not <see cref="MemberState.Member"/>.
    /// </summary>
    Task<MemberState> ConnectAsync(ulong guild, ulong user, CancellationToken cancellationToken);

    void Disconnect(ulong guild, ulong user);

    /// <summary>False when the bot knows that it is not in the guild.</summary>
    bool HasGuild(ulong guild);

    /// <summary>The policy rules for a followed user, with the roles of now. Null when the guild
    /// is not ready.</summary>
    bool? CanRead(EventPolicy policy, ulong guild, ulong user);

    /// <summary>The roles of a user now, and whether the user is an administrator. The roles of a
    /// followed user come from memory, the roles of another user from Discord.</summary>
    Task<MemberAccess> AccessAsync(ulong guild, ulong user, CancellationToken cancellationToken);
}
