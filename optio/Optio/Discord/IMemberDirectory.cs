using Optio.Streams;

namespace Optio.Discord;

/// <summary>The result of <see cref="IMemberDirectory.ConnectAsync"/>.</summary>
internal enum MemberState
{
    /// <summary>The user is a member of the guild.</summary>
    Member,

    /// <summary>The user is not a member of the guild.</summary>
    NotMember,

    /// <summary>The bot of optio is not in the guild.</summary>
    NoBot,

    /// <summary>Discord did not answer in time.</summary>
    Unavailable,
}

/// <summary>The Discord facts of the users that have an open stream.</summary>
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
}
