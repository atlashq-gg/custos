using NetCord;
using Optio.Streams;

namespace Optio.Tests;

/// <summary>The policy rules of optio/docs/producers.md.</summary>
public sealed class AccessPolicyTests
{
    private const ulong Guild = 1;
    private const ulong Owner = 10;
    private const ulong User = 11;
    private const ulong ReadRole = 100;
    private const ulong AdminRole = 101;
    private const ulong UnknownRole = 199;

    private static readonly GuildFacts Facts = new(
        Guild,
        Owner,
        new Dictionary<ulong, Permissions>
        {
            [Guild] = Permissions.ViewChannel,
            [ReadRole] = Permissions.ViewChannel,
            [AdminRole] = Permissions.Administrator,
        }
    );

    [TestCase(new ulong[] { ReadRole }, new ulong[] { ReadRole }, true)]
    [TestCase(new ulong[] { ReadRole }, new ulong[0], false)]
    [TestCase(new ulong[0], new ulong[] { ReadRole }, false)]
    [TestCase(new ulong[] { Guild }, new ulong[0], true)]
    [TestCase(new ulong[] { UnknownRole }, new ulong[] { UnknownRole }, false)]
    [TestCase(new ulong[0], new ulong[] { AdminRole }, true)]
    public void Member_reads_through_a_role_of_the_policy(
        ulong[] policyRoles,
        ulong[] memberRoles,
        bool reads
    ) =>
        Assert.That(
            AccessPolicy.CanRead(
                new EventPolicy(Guild, policyRoles, null),
                Facts,
                new(User, memberRoles)
            ),
            Is.EqualTo(reads)
        );

    [Test]
    public void Owner_reads_an_event_with_an_empty_role_list() =>
        Assert.That(AccessPolicy.CanRead(new EventPolicy(Guild, [], null), Facts, new(Owner, [])));

    [Test]
    public void Everyone_role_with_the_administrator_permission_gives_each_member_access()
    {
        var facts = Facts with
        {
            Roles = new Dictionary<ulong, Permissions> { [Guild] = Permissions.Administrator },
        };

        Assert.That(AccessPolicy.CanRead(new EventPolicy(Guild, [], null), facts, new(User, [])));
    }

    [Test]
    public void Event_of_another_guild_gives_no_access() =>
        Assert.That(
            AccessPolicy.CanRead(new EventPolicy(2, [2], null), Facts, new(Owner, [])),
            Is.False
        );

    [Test]
    public void Event_for_some_users_gives_access_to_those_users_and_not_to_the_owner()
    {
        var policy = new EventPolicy(Guild, [], [User]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AccessPolicy.CanRead(policy, Facts, new(User, [])), Is.True);
            Assert.That(AccessPolicy.CanRead(policy, Facts, new(Owner, [])), Is.False);
            Assert.That(AccessPolicy.CanRead(policy, Facts, new(12, [AdminRole])), Is.False);
        }
    }

    [Test]
    public void Event_for_some_users_also_gives_access_through_a_role() =>
        Assert.That(
            AccessPolicy.CanRead(
                new EventPolicy(Guild, [ReadRole], [User]),
                Facts,
                new(12, [ReadRole])
            )
        );
}
