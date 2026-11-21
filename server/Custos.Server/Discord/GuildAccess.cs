namespace Custos.Server.Discord;

/// <summary>
/// The kinds of access of a member in a guild. <see cref="Admin"/> includes the three other kinds.
/// </summary>
[Flags]
public enum GuildAccess
{
    None = 0,
    Read = 1,
    Report = 2,
    Label = 4,
    Admin = 8,
}

/// <summary>
/// The facts about a member that the access rule needs: the role IDs, and the Administrator
/// permission. The guild owner has the Administrator permission.
/// </summary>
internal sealed record MemberFacts(IReadOnlyCollection<ulong> RoleIds, bool IsAdministrator);

/// <summary>The access rule. It has no I/O.</summary>
internal static class AccessRule
{
    /// <summary>
    /// The access of a member. A member has <see cref="GuildAccess.Read"/>,
    /// <see cref="GuildAccess.Report"/>, or <see cref="GuildAccess.Label"/> through one role of the
    /// related list. With an empty report list, each member can report. With an empty read or
    /// label list, only an administrator has that access.
    /// </summary>
    public static GuildAccess Evaluate(MemberFacts member, GuildSettings settings)
    {
        if (member.IsAdministrator)
            return GuildAccess.Admin | GuildAccess.Read | GuildAccess.Report | GuildAccess.Label;

        var access = GuildAccess.None;
        var roles = settings.Access;
        if (Holds(member, roles.ReadRoleIds))
            access |= GuildAccess.Read;
        if (roles.ReportRoleIds.Count == 0 || Holds(member, roles.ReportRoleIds))
            access |= GuildAccess.Report;
        if (Holds(member, roles.LabelRoleIds))
            access |= GuildAccess.Label;
        return access;
    }

    private static bool Holds(MemberFacts member, IReadOnlyList<ulong> roleIds) =>
        roleIds.Any(member.RoleIds.Contains);
}
