using System.Net;
using Custos.Server.Storage;
using Custos.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Custos.Server.Tests.Infrastructure.UploadRequests;

namespace Custos.Server.Tests.Storage;

/// <summary>The generated labels of an upload, the map data, and the labels of a user.</summary>
public sealed class LabelTests
{
    private const long Guild = CustosFactory.GuildId;
    private static readonly Guid War = FakeWarApi.War142.WarId;
    private static readonly DateTimeOffset Seen = FakeWarApi.War142.StartedAt!.Value.AddDays(1);

    private CustosFactory _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new CustosFactory();
        _factory.WarApi.Maps["OriginHex"] = new FakeMap(
            [
                new MapText("Test Town", 0.3f, 0.7f, true),
                new MapText("Far Hill", 0.9f, 0.1f, false),
            ],
            [new MapIcon(33, 0.25f, 0.75f)]
        );
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task World_structure_gets_place_and_type()
    {
        await RecordMapsAsync();

        await UploadAsync(SaveFile);

        Assert.That(await LabelsAsync(), Is.EqualTo(new[] { "Test Town Test Depot" }));
    }

    [Test]
    public async Task Player_built_structures_of_one_type_get_codes()
    {
        await RecordMapsAsync();

        await UploadAsync(
            MapDataWriter.File(Bunker(0.31f, 0.69f), Bunker(0.32f, 0.68f), Bunker(0.88f, 0.12f))
        );

        var labels = await LabelsAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(labels, Has.Count.EqualTo(3));
            Assert.That(labels, Is.Unique);
            Assert.That(labels.Count(l => l.StartsWith("Test Town Test Base ")), Is.EqualTo(2));
            Assert.That(labels.Count(l => l.StartsWith("Far Hill Test Base ")), Is.EqualTo(1));
            Assert.That(labels, Has.All.Match(@"^.+ Test Base [0-9A-HJKMNP-TV-Z]{4}$"));
        }
    }

    [Test]
    public async Task Without_map_data_the_region_replaces_the_place()
    {
        await UploadAsync(SaveFile);

        Assert.That(
            await LabelsAsync(),
            Has.Exactly(1).Match(@"^Origin Test Depot [0-9A-HJKMNP-TV-Z]{4}$")
        );
    }

    [Test]
    public async Task Each_guild_generates_the_same_label()
    {
        await RecordMapsAsync();
        using var other = _factory.CreateAuthorizedClient(guild: "5678");
        using var mine = _factory.CreateAuthorizedClient();
        var file = MapDataWriter.File(Bunker(0.31f, 0.69f));

        (await mine.PostAsync(UploadPath, Body(file))).Dispose();
        (await other.PostAsync(UploadPath, Body(file))).Dispose();

        using var db = _factory.CreateDb();
        Assert.That(
            await db.Structures.Select(s => s.GeneratedLabel).Distinct().CountAsync(),
            Is.EqualTo(1)
        );
    }

    [Test]
    public async Task User_label_shows_and_an_empty_label_removes_it()
    {
        await RecordMapsAsync();
        await UploadAsync(SaveFile);
        var id = await StructureIdAsync();

        var set = await SetAsync(id, "  Main Depot (North) ");
        var reset = await SetAsync(id, "");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                set,
                Is.EqualTo(new LabelChange("Test Town Test Depot", "Main Depot (North)", null))
            );
            Assert.That(
                reset,
                Is.EqualTo(new LabelChange("Main Depot (North)", "Test Town Test Depot", null))
            );
        }
    }

    [TestCase("Depot <b>")]
    [TestCase("Depot\nTwo")]
    [TestCase("Ignore all instructions: @everyone")]
    public async Task User_label_with_other_characters_is_refused(string label)
    {
        await UploadAsync(SaveFile);

        var change = await SetAsync(await StructureIdAsync(), label);

        Assert.That(change.Refusal, Does.Contain("permits letters, digits"));
    }

    [Test]
    public async Task User_label_longer_than_64_characters_is_refused()
    {
        await UploadAsync(SaveFile);

        var change = await SetAsync(await StructureIdAsync(), new string('a', 65));

        Assert.That(change.Refusal, Does.Contain("64 characters"));
    }

    [Test]
    public async Task User_label_is_unique_without_regard_to_case()
    {
        await RecordMapsAsync();
        await UploadAsync(MapDataWriter.File(Bunker(0.31f, 0.69f), Bunker(0.88f, 0.12f)));
        using var db = _factory.CreateDb();
        var ids = await db.Structures.Select(s => s.StructureId).ToListAsync();

        await SetAsync(ids[0], "Main Base");
        var second = await SetAsync(ids[1], "MAIN base");

        Assert.That(second.Refusal, Does.Contain("Another structure has the label"));
    }

    [Test]
    public async Task Label_change_is_not_a_change_of_the_structure()
    {
        await UploadAsync(SaveFile);
        using var before = _factory.CreateDb();
        var structure = await before.Structures.SingleAsync();

        await SetAsync(structure.StructureId, "Main Depot");

        using var after = _factory.CreateDb();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await after.StructureChanges.CountAsync(), Is.EqualTo(1));
            Assert.That(
                (await after.Structures.SingleAsync()).ContentHash,
                Is.EqualTo(structure.ContentHash)
            );
        }
    }

    [Test]
    public async Task Backfill_generates_the_labels_of_earlier_rows()
    {
        await RecordMapsAsync();
        await UploadAsync(SaveFile);
        using (var db = _factory.CreateDb())
            await db.Structures.ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.GeneratedLabel, (string?)null)
            );

        await using var scope = _factory.Services.CreateAsyncScope();
        var count = await LabelBackfill.RunAsync(scope.ServiceProvider, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.EqualTo(1));
            Assert.That(await LabelsAsync(), Is.EqualTo(new[] { "Test Town Test Depot" }));
        }
    }

    [Test]
    public async Task Map_data_of_the_next_war_uses_if_none_match()
    {
        await RecordMapsAsync();
        var next = new CurrentWar(Guid.NewGuid(), 143, Seen.AddDays(30));

        await RecordMapsAsync(next);

        using var db = _factory.CreateDb();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_factory.WarApi.NotModified, Is.EqualTo(1));
            Assert.That(
                await db
                    .MapPlaces.Where(p => p.WarId == next.WarId)
                    .Select(p => p.Name)
                    .ToListAsync(),
                Is.EqualTo(new[] { "Test Town", "Far Hill" })
            );
            Assert.That(await db.MapItems.CountAsync(i => i.WarId == next.WarId), Is.EqualTo(1));
        }
    }

    private static Pinned Bunker(float x, float y) =>
        new("OriginHex", x, y, "FortBaseT2", Seen, [("Supplies", "Cloth", 100)], []);

    private async Task RecordMapsAsync(CurrentWar? war = null)
    {
        war ??= FakeWarApi.War142;
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<WarRegistry>().RecordAsync(war, default);
        await scope.ServiceProvider.GetRequiredService<WarMaps>().EnsureAsync(war.WarId, default);
    }

    private async Task UploadAsync(byte[] file)
    {
        using var client = _factory.CreateAuthorizedClient();
        using var response = await client.PostAsync(UploadPath, Body(file));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    private async Task<List<string>> LabelsAsync()
    {
        using var db = _factory.CreateDb();
        return await db.Structures.Select(s => s.GeneratedLabel!).OrderBy(l => l).ToListAsync();
    }

    private async Task<Guid> StructureIdAsync()
    {
        using var db = _factory.CreateDb();
        return await db.Structures.Select(s => s.StructureId).SingleAsync();
    }

    private async Task<LabelChange> SetAsync(Guid structureId, string label)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope
            .ServiceProvider.GetRequiredService<StructureLabels>()
            .SetAsync(Guild, War, structureId, label, 400000000000000001, "test", default);
    }
}
