using NetCord;

namespace Optio.Streams;

/// <summary>The access policy of an event in Discord terms (optio/docs/producers.md).</summary>
internal sealed record EventPolicy(
    ulong Guild,
    IReadOnlyList<ulong> Roles,
    IReadOnlyList<ulong>? Users
);

/// <summary>A member of a guild: the user and the role IDs, without <c>@everyone</c>.</summary>
internal sealed record MemberFacts(ulong UserId, IReadOnlyCollection<ulong> RoleIds);

/// <summary>The facts of a guild that the policy needs: the owner, and the permissions of each
/// role. The role <c>@everyone</c> has the ID of the guild.</summary>
internal sealed record GuildFacts(
    ulong GuildId,
    ulong OwnerId,
    IReadOnlyDictionary<ulong, Permissions> Roles
);

/// <summary>The policy rules of optio/docs/producers.md. They have no I/O.</summary>
internal static class AccessPolicy
{
    /// <summary>True when <paramref name="member"/> of <paramref name="guild"/> can read an
    /// event with <paramref name="policy"/> on a stream of that guild.</summary>
    public static bool CanRead(EventPolicy policy, GuildFacts guild, MemberFacts member)
    {
        if (policy.Guild != guild.GuildId)
            return false;

        // The owner and an administrator read each event, except an event for some users.
        var forUsers = policy.Users is { Count: > 0 };
        if (!forUsers && IsAdministrator(guild, member))
            return true;
        if (forUsers && policy.Users!.Contains(member.UserId))
            return true;

        // An unknown role ID gives no access. The ID of the guild is @everyone.
        return policy.Roles.Any(role =>
            guild.Roles.ContainsKey(role)
            && (role == guild.GuildId || member.RoleIds.Contains(role))
        );
    }

    /// <summary>True for the owner of the guild, and for a member with a role that has the
    /// permission Administrator.</summary>
    internal static bool IsAdministrator(GuildFacts guild, MemberFacts member) =>
        member.UserId == guild.OwnerId
        || member
            .RoleIds.Append(guild.GuildId)
            .Any(role =>
                guild.Roles.TryGetValue(role, out var permissions)
                && permissions.HasFlag(Permissions.Administrator)
            );
}
