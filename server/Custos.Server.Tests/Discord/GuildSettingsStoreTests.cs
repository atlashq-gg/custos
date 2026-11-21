using Custos.Server.Discord;
using Custos.Server.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Custos.Server.Tests.Discord;

public sealed class GuildSettingsStoreTests
{
    private const ulong GuildA = 300000000000000001;
    private const ulong GuildB = 300000000000000002;
    private const ulong GuildC = 300000000000000003;
    private const ulong Admin = 400000000000000001;

    private CustosFactory _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new CustosFactory();

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Guild_without_a_row_has_the_default_document()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<GuildSettingsStore>();

        Assert.That(
            (await store.GetAsync(GuildA, CancellationToken.None)).ToJson(),
            Is.EqualTo(GuildSettings.Default.ToJson())
        );
    }

    [Test]
    public async Task Save_replaces_the_document_and_records_the_user()
    {
        await SaveAsync(GuildA, new SharingSettings { ReadGuildIds = [GuildB] });
        var settings = await SaveAsync(GuildA, new SharingSettings { ReadGuildIds = [GuildC] });

        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<GuildSettingsStore>();
        using var db = _factory.CreateDb();
        var row = db.GuildSettings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                (await store.GetAsync(GuildA, CancellationToken.None)).ToJson(),
                Is.EqualTo(settings.ToJson())
            );
            Assert.That(row.GuildId, Is.EqualTo((long)GuildA));
            Assert.That(row.UpdatedBy, Is.EqualTo((long)Admin));
        }
    }

    [Test]
    public async Task Sharing_guilds_are_found_for_a_reader()
    {
        await SaveAsync(GuildA, new SharingSettings { ReadGuildIds = [GuildC] });
        await SaveAsync(GuildB, new SharingSettings { ReadGuildIds = [GuildA, GuildC] });

        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<GuildSettingsStore>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                await store.GetSharingGuildsAsync(GuildC, CancellationToken.None),
                Is.EqualTo(new[] { GuildA, GuildB })
            );
            Assert.That(
                await store.GetSharingGuildsAsync(GuildA, CancellationToken.None),
                Is.EqualTo(new[] { GuildB })
            );
            Assert.That(
                await store.GetSharingGuildsAsync(GuildB, CancellationToken.None),
                Is.Empty
            );
        }
    }

    private async Task<GuildSettings> SaveAsync(ulong guildId, SharingSettings sharing)
    {
        var settings = GuildSettings.Default with { Sharing = sharing };
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<GuildSettingsStore>()
            .SaveAsync(guildId, settings, Admin, "test", CancellationToken.None);
        return settings;
    }
}
