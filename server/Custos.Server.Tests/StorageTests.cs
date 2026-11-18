using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Custos.Server.Storage;
using Custos.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Custos.Server.Tests;

/// <summary>What the upload stores: the upload, the current state, the history, and the
/// wars.</summary>
public sealed class StorageTests
{
    private static readonly DateTimeOffset T1 = FakeWarApi.War142.StartedAt!.Value.AddDays(1);
    private static readonly DateTimeOffset T2 = T1.AddHours(1);

    private CustosFactory _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new CustosFactory();

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private static byte[] Depot(DateTimeOffset lastUpdated, short rifles = 20) =>
        MapDataWriter.File(
            new Pinned(
                "OriginHex",
                0.25f,
                0.75f,
                "StorageFacility",
                lastUpdated,
                [("ItemCrates", "RifleC", rifles)],
                [("ATLAS REC", [("Vehicles", "TruckC", 2)])]
            )
        );

    [Test]
    public async Task Upload_stores_the_upload_and_the_current_state()
    {
        await UploadAsync(Depot(T1));

        await using var db = _factory.CreateDb();
        var upload = await db.Uploads.SingleAsync();
        var structure = await db.Structures.SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(upload.GuildId, Is.EqualTo(CustosFactory.GuildId));
            Assert.That(upload.UserId, Is.EqualTo("dev"));
            Assert.That(upload.WarId, Is.EqualTo(FakeWarApi.War142.WarId));
            Assert.That(
                structure.StructureId,
                Is.EqualTo(
                    EntityIds.Structure("live", FakeWarApi.War142.WarId, "OriginHex", 0.25f, 0.75f)
                )
            );
            Assert.That(structure.CodeName, Is.EqualTo("StorageFacility"));
            Assert.That(structure.ObservedAt, Is.EqualTo(T1));
            Assert.That(
                await db.Stockpiles.Select(s => s.Name).ToListAsync(),
                Is.EquivalentTo(new[] { null, "ATLAS REC" })
            );
            Assert.That(await db.StockpileEntries.CountAsync(), Is.EqualTo(2));
            Assert.That(await db.StockpileChanges.CountAsync(), Is.EqualTo(2));
            Assert.That(await db.StructureChanges.CountAsync(), Is.EqualTo(1));
            Assert.That(
                await db.StockpileChanges.Select(c => c.Entries).ToListAsync(),
                Does.Contain("""{"item_crate": {"RifleC": 20}}""")
            );
        }
    }

    [Test]
    public async Task Same_content_moves_only_last_seen_at()
    {
        await UploadAsync(Depot(T1));
        await UploadAsync(Depot(T2));

        await using var db = _factory.CreateDb();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await db.Uploads.CountAsync(), Is.EqualTo(2));
            Assert.That(await db.StockpileChanges.CountAsync(), Is.EqualTo(2));
            Assert.That(
                await db.StockpileChanges.Select(c => c.LastSeenAt).Distinct().SingleAsync(),
                Is.EqualTo(T2)
            );
            Assert.That(
                await db.Stockpiles.Select(s => s.ObservedAt).Distinct().SingleAsync(),
                Is.EqualTo(T1)
            );
            Assert.That(
                await db.Structures.Select(s => s.LastSeenAt).SingleAsync(),
                Is.EqualTo(T2)
            );
            Assert.That(
                await db.StructureChanges.Select(c => c.LastSeenAt).SingleAsync(),
                Is.EqualTo(T2)
            );
        }
    }

    [Test]
    public async Task Changed_content_adds_a_change_and_replaces_the_entries()
    {
        await UploadAsync(Depot(T1, rifles: 20));
        await UploadAsync(Depot(T2, rifles: 5));

        await using var db = _factory.CreateDb();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await db.StockpileChanges.CountAsync(), Is.EqualTo(3));
            Assert.That(
                await db
                    .StockpileEntries.Where(e => e.CodeName == "RifleC")
                    .Select(e => e.Quantity)
                    .SingleAsync(),
                Is.EqualTo(5)
            );
            Assert.That(await db.StructureChanges.CountAsync(), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Older_data_does_not_replace_newer_data()
    {
        await UploadAsync(Depot(T2, rifles: 5));
        await UploadAsync(Depot(T1, rifles: 20));

        await using var db = _factory.CreateDb();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                await db
                    .StockpileEntries.Where(e => e.CodeName == "RifleC")
                    .Select(e => e.Quantity)
                    .SingleAsync(),
                Is.EqualTo(5)
            );
            Assert.That(await db.StockpileChanges.CountAsync(), Is.EqualTo(2));
        }
    }

    [Test]
    public async Task Item_from_before_the_war_is_ignored()
    {
        var result = await UploadAsync(Depot(FakeWarApi.War142.StartedAt!.Value.AddMinutes(-1)));

        await using var db = _factory.CreateDb();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("stockpiles").GetArrayLength(), Is.Zero);
            Assert.That(await db.Uploads.CountAsync(), Is.EqualTo(1));
            Assert.That(await db.Structures.CountAsync(), Is.Zero);
        }
    }

    [Test]
    public async Task Empty_public_stockpile_is_not_kept()
    {
        await UploadAsync(
            MapDataWriter.File(new Pinned("OriginHex", 0.3f, 0.6f, "MaintenanceTunnel", T1, []))
        );

        await using var db = _factory.CreateDb();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await db.Structures.CountAsync(), Is.EqualTo(1));
            Assert.That(await db.Stockpiles.CountAsync(), Is.Zero);
        }
    }

    [Test]
    public async Task Guilds_calculate_the_same_ids_but_keep_separate_rows()
    {
        await UploadAsync(Depot(T1), guild: "1");
        await UploadAsync(Depot(T1), guild: "2");

        await using var db = _factory.CreateDb();
        var structures = await db.Structures.ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(structures.Select(s => s.GuildId), Is.EquivalentTo(new[] { 1L, 2L }));
            Assert.That(structures.Select(s => s.StructureId).Distinct().Count(), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Next_war_ends_the_earlier_war_and_gets_its_own_partitions()
    {
        await UploadAsync(Depot(T1));
        var war143 = new CurrentWar(Guid.NewGuid(), 143, T2);
        _factory.WarApi.War = war143;
        _factory.Services.GetRequiredService<IMemoryCache>().Remove("war:current");
        await UploadAsync(Depot(T2.AddMinutes(1)));

        await using var db = _factory.CreateDb();
        var wars = await db.Wars.OrderBy(w => w.WarNumber).ToListAsync();
        var partitions = await db
            .Database.SqlQuery<string>(
                $"""
                SELECT c.relname AS "Value"
                FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
                WHERE c.relkind = 'r'
                """
            )
            .ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wars.Select(w => w.WarNumber), Is.EqualTo(new[] { 142, 143 }));
            Assert.That(wars[0].EndedAt, Is.Not.Null);
            Assert.That(wars[1].EndedAt, Is.Null);
            Assert.That(wars[1].StartedAt, Is.EqualTo(T2));
            Assert.That(
                partitions,
                Is.EquivalentTo(
                    new[]
                    {
                        "stockpile_changes_w142",
                        "structure_changes_w142",
                        "stockpile_changes_w143",
                        "structure_changes_w143",
                    }
                )
            );
            Assert.That(
                await db.Structures.Select(s => s.WarId).Distinct().CountAsync(),
                Is.EqualTo(2)
            );
        }
    }

    [Test]
    public async Task War_monitor_records_the_current_war_without_an_upload()
    {
        using var factory = _factory.WithWebHostBuilder(b =>
            b.UseSetting("Custos:WarPollIntervalSeconds", "1")
        );
        _ = factory.Server;

        await using var db = _factory.CreateDb();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await db.Wars.AnyAsync() && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        Assert.That(await db.Wars.Select(w => w.WarNumber).SingleAsync(), Is.EqualTo(142));
    }

    private async Task<JsonElement> UploadAsync(byte[] file, string guild = "1234")
    {
        using var client = _factory.CreateAuthorizedClient(guild: guild);
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await client.PostAsync("/api/v1/uploads", content);
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
