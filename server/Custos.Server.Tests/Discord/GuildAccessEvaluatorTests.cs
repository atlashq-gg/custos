using Custos.Server.Discord;
using Custos.Server.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Custos.Server.Tests.Discord;

public sealed class GuildAccessEvaluatorTests
{
    private const ulong Guild = 300000000000000001;
    private const ulong OtherGuild = 300000000000000002;
    private const ulong User = 400000000000000001;
    private const ulong ReadRole = 100000000000000002;

    private CustosFactory _factory = null!;
    private AsyncServiceScope _scope;
    private FakeDiscordDirectory _discord = null!;
    private GuildAccessEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new CustosFactory();
        _scope = _factory.Services.CreateAsyncScope();
        _discord = new FakeDiscordDirectory();
        _evaluator = new GuildAccessEvaluator(
            _discord,
            _scope.ServiceProvider.GetRequiredService<GuildSettingsStore>()
        );
    }

    [TearDown]
    public async Task TearDown()
    {
        await _scope.DisposeAsync();
        await _factory.DisposeAsync();
    }

    [Test]
    public async Task Member_gets_the_access_of_the_settings()
    {
        _discord.Add(Guild, User, roleIds: ReadRole);
        await _scope
            .ServiceProvider.GetRequiredService<GuildSettingsStore>()
            .SaveAsync(
                Guild,
                GuildSettings.Default with
                {
                    Access = new AccessSettings { ReadRoleIds = [ReadRole] },
                },
                User,
                "test",
                CancellationToken.None
            );

        var check = await _evaluator.EvaluateAsync(Guild, User, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(check.Status, Is.EqualTo(MemberStatus.Member));
            Assert.That(check.Access, Is.EqualTo(GuildAccess.Read | GuildAccess.Report));
            Assert.That(check.Has(GuildAccess.Read), Is.True);
            Assert.That(check.Has(GuildAccess.Label), Is.False);
        }
    }

    [Test]
    public async Task Non_member_and_guild_without_the_bot_have_no_access()
    {
        _discord.Guilds.Add(Guild);

        var notMember = await _evaluator.EvaluateAsync(Guild, User, CancellationToken.None);
        var noBot = await _evaluator.EvaluateAsync(OtherGuild, User, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(notMember, Is.EqualTo(new AccessCheck(MemberStatus.NotMember, default)));
            Assert.That(noBot, Is.EqualTo(new AccessCheck(MemberStatus.BotNotInGuild, default)));
        }
    }

    [Test]
    public async Task One_request_asks_discord_one_time_for_each_guild()
    {
        _discord.Add(Guild, User).Add(OtherGuild, User);

        await _evaluator.EvaluateAsync(Guild, User, CancellationToken.None);
        await _evaluator.EvaluateAsync(Guild, User, CancellationToken.None);
        await _evaluator.EvaluateAsync(OtherGuild, User, CancellationToken.None);

        Assert.That(_discord.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task Discord_outage_is_not_a_refusal()
    {
        _discord.Unavailable = true;

        await Assert.ThatAsync(
            () => _evaluator.EvaluateAsync(Guild, User, CancellationToken.None),
            Throws.TypeOf<DiscordUnavailableException>()
        );
    }
}
