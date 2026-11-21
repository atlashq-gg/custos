using Custos.Server.Discord;

namespace Custos.Server.Tests.Discord;

public sealed class AccessRuleTests
{
    private const ulong ReportRole = 100000000000000001;
    private const ulong ReadRole = 100000000000000002;
    private const ulong LabelRole = 100000000000000003;
    private const ulong OtherRole = 100000000000000009;

    private static readonly GuildSettings WithRoles = new()
    {
        Access = new AccessSettings
        {
            ReportRoleIds = [ReportRole],
            ReadRoleIds = [ReadRole],
            LabelRoleIds = [LabelRole],
        },
    };

    private const GuildAccess All =
        GuildAccess.Admin | GuildAccess.Read | GuildAccess.Report | GuildAccess.Label;

    // Default settings: empty lists.
    [TestCase(false, new ulong[0], false, ExpectedResult = GuildAccess.Report)]
    [TestCase(false, new[] { OtherRole }, false, ExpectedResult = GuildAccess.Report)]
    [TestCase(true, new ulong[0], false, ExpectedResult = All)]
    // Settings with one role in each list.
    [TestCase(false, new ulong[0], true, ExpectedResult = GuildAccess.None)]
    [TestCase(false, new[] { OtherRole }, true, ExpectedResult = GuildAccess.None)]
    [TestCase(false, new[] { ReportRole }, true, ExpectedResult = GuildAccess.Report)]
    [TestCase(false, new[] { ReadRole }, true, ExpectedResult = GuildAccess.Read)]
    [TestCase(false, new[] { LabelRole }, true, ExpectedResult = GuildAccess.Label)]
    [TestCase(
        false,
        new[] { ReadRole, LabelRole, OtherRole },
        true,
        ExpectedResult = GuildAccess.Read | GuildAccess.Label
    )]
    [TestCase(true, new ulong[0], true, ExpectedResult = All)]
    public GuildAccess Access_follows_the_roles_and_the_settings(
        bool administrator,
        ulong[] roles,
        bool withRoles
    ) =>
        AccessRule.Evaluate(
            new MemberFacts(roles, administrator),
            withRoles ? WithRoles : GuildSettings.Default
        );
}
