using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Rest;
using Optio.Discord;
using Optio.Streams;

namespace Optio.Tests;

/// <summary>
/// The member directory against the real Discord gateway. The tests need the bot token in
/// <c>Discord__Token</c> and a member of a guild of the bot in <c>Discord__TestUserId</c>, who is
/// not the owner and has no role with the permission Administrator. Without both, they skip. CI
/// does not run them. They change nothing in Discord.
/// </summary>
[Category("Discord")]
public sealed class DiscordMembersTests
{
    private DiscordGateway _gateway = null!;
    private DiscordMembers _members = null!;
    private ulong _guild;
    private ulong _user;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var token = Environment.GetEnvironmentVariable("Discord__Token");
        if (
            string.IsNullOrEmpty(token)
            || !ulong.TryParse(Environment.GetEnvironmentVariable("Discord__TestUserId"), out _user)
        )
        {
            Assert.Ignore("Discord__Token or Discord__TestUserId is not set.");
        }

        using var rest = new RestClient(new BotToken(token));
        await foreach (var guild in rest.GetCurrentUserGuildsAsync())
        {
            try
            {
                await rest.GetGuildUserAsync(guild.Id, _user);
                _guild = guild.Id;
                break;
            }
            catch (RestException)
            {
                // The test user is not a member of this guild.
            }
        }
        if (_guild == 0)
            Assert.Ignore("The test user is not a member of a guild of the bot.");

        _gateway = new DiscordGateway(
            Options.Create(new OptioOptions { DiscordToken = token }),
            new ServiceCollection()
                .BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DiscordGateway>.Instance,
            commands: false
        );
        _members = new DiscordMembers(_gateway, NullLogger<DiscordMembers>.Instance);
        await _gateway.StartAsync(default);
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (_gateway is null)
            return;
        await _gateway.StopAsync(default);
        _gateway.Dispose();
    }

    [Test]
    public async Task Member_of_the_guild_reads_by_the_policy_with_its_roles()
    {
        Assert.That(
            await _members.ConnectAsync(_guild, _user, default),
            Is.EqualTo(MemberState.Member)
        );
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_members.CanRead(Policy([_guild]), _guild, _user), Is.True);
                Assert.That(_members.CanRead(Policy([]), _guild, _user), Is.False);
                Assert.That(_members.CanRead(Policy([], [_user]), _guild, _user), Is.True);
            }
        }
        finally
        {
            _members.Disconnect(_guild, _user);
        }
    }

    [Test]
    public async Task User_that_is_not_a_member_is_not_a_member()
    {
        // A snowflake of 2015 that is not a member of a small test guild.
        const ulong stranger = 100000000000000000;

        Assert.That(
            await _members.ConnectAsync(_guild, stranger, default),
            Is.EqualTo(MemberState.NotMember)
        );
        _members.Disconnect(_guild, stranger);
    }

    [Test]
    public async Task Guild_without_the_bot_has_no_bot()
    {
        Assert.That(await _members.ConnectAsync(1, _user, default), Is.EqualTo(MemberState.NoBot));
        _members.Disconnect(1, _user);
    }

    [Test]
    public async Task Access_of_a_user_without_a_stream_comes_from_Discord()
    {
        var expected = await new RestClient(
            new BotToken(Environment.GetEnvironmentVariable("Discord__Token")!)
        ).GetGuildUserAsync(_guild, _user);

        var access = await _members.AccessAsync(_guild, _user, default);
        var stranger = await _members.AccessAsync(_guild, 100000000000000000, default);
        var noBot = await _members.AccessAsync(1, _user, default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(access.State, Is.EqualTo(MemberState.Member));
            Assert.That(access.RoleIds, Is.EquivalentTo(expected.RoleIds));
            Assert.That(access.IsAdministrator, Is.False);
            Assert.That(stranger.State, Is.EqualTo(MemberState.NotMember));
            Assert.That(noBot.State, Is.EqualTo(MemberState.NoBot));
        }
    }

    private EventPolicy Policy(ulong[] roles, ulong[]? users = null) => new(_guild, roles, users);
}
