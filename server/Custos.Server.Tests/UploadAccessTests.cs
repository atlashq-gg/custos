using System.Net;
using Custos.Server.Discord;
using Custos.Server.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static Custos.Server.Tests.Infrastructure.UploadRequests;

namespace Custos.Server.Tests;

/// <summary>The access rule for uploads, with the Discord connection on.</summary>
public sealed class UploadAccessTests
{
    private const ulong Guild = (ulong)CustosFactory.GuildId;
    private const ulong User = 400000000000000001;
    private const ulong ReportRole = 100000000000000001;

    private FakeDiscordDirectory _discord = null!;
    private CustosFactory _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _discord = new FakeDiscordDirectory();
        _factory = new CustosFactory(discord: _discord);
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Member_with_report_uploads_and_the_row_keeps_the_discord_user()
    {
        _discord.Add(Guild, User);
        using var client = _factory.CreateAuthorizedClient(discordId: User.ToString());

        using var response = await client.PostAsync(UploadPath, Body(SaveFile));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var db = _factory.CreateDb();
        Assert.That(db.Uploads.Single().DiscordUserId, Is.EqualTo((long)User));
    }

    [Test]
    public async Task Member_without_a_report_role_is_403()
    {
        _discord.Add(Guild, User, roleIds: 100000000000000009);
        await SaveReportRoleAsync();
        using var client = _factory.CreateAuthorizedClient(discordId: User.ToString());

        await AssertStatusAsync(client, HttpStatusCode.Forbidden, "No access");
    }

    [Test]
    public async Task Member_with_a_report_role_uploads()
    {
        _discord.Add(Guild, User, roleIds: ReportRole);
        await SaveReportRoleAsync();
        using var client = _factory.CreateAuthorizedClient(discordId: User.ToString());

        using var response = await client.PostAsync(UploadPath, Body(SaveFile));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Token_without_the_claim_is_403()
    {
        _discord.Add(Guild, User);
        using var client = _factory.CreateAuthorizedClient(discordId: null);

        await AssertStatusAsync(client, HttpStatusCode.Forbidden, "No Discord user");
    }

    [Test]
    public async Task Non_member_and_guild_without_the_bot_are_403()
    {
        using var noBot = _factory.CreateAuthorizedClient(discordId: User.ToString());
        await AssertStatusAsync(noBot, HttpStatusCode.Forbidden, "Guild without the bot");

        _discord.Guilds.Add(Guild);
        using var notMember = _factory.CreateAuthorizedClient(discordId: User.ToString());
        await AssertStatusAsync(notMember, HttpStatusCode.Forbidden, "Not a member");
    }

    [Test]
    public async Task Discord_outage_is_503()
    {
        _discord.Add(Guild, User);
        _discord.Unavailable = true;
        using var client = _factory.CreateAuthorizedClient(discordId: User.ToString());

        await AssertStatusAsync(client, HttpStatusCode.ServiceUnavailable, "Discord unavailable");
    }

    [Test]
    public async Task Development_discord_user_applies_in_development_only()
    {
        _discord.Add(Guild, (ulong)CustosFactory.DevelopmentDiscordUserId);
        using var development = new CustosFactory(environment: "Development", discord: _discord);
        using var developmentClient = development.CreateAuthorizedClient(discordId: null);

        using var accepted = await developmentClient.PostAsync(UploadPath, Body(SaveFile));

        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Without_discord_the_claim_is_kept_and_no_role_is_examined()
    {
        using var factory = new CustosFactory();
        using var client = factory.CreateAuthorizedClient(discordId: User.ToString());

        using var response = await client.PostAsync(UploadPath, Body(SaveFile));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var db = factory.CreateDb();
        Assert.That(db.Uploads.Single().DiscordUserId, Is.EqualTo((long)User));
    }

    private async Task SaveReportRoleAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<GuildSettingsStore>()
            .SaveAsync(
                Guild,
                GuildSettings.Default with
                {
                    Access = new AccessSettings { ReportRoleIds = [ReportRole] },
                },
                User,
                "test",
                CancellationToken.None
            );
    }

    private static async Task AssertStatusAsync(
        HttpClient client,
        HttpStatusCode status,
        string title
    )
    {
        using var response = await client.PostAsync(UploadPath, Body(SaveFile));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(status));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain(title));
        }
    }
}
