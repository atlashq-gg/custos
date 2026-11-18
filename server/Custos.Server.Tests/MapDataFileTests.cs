using Custos.Server.MapData;
using Custos.Server.Tests.Infrastructure;

namespace Custos.Server.Tests;

public sealed class MapDataFileTests
{
    private static readonly DateTimeOffset LastUpdated = new(
        2026,
        11,
        17,
        14,
        3,
        15,
        243,
        TimeSpan.Zero
    );

    [Test]
    public void Parses_a_pinned_item_with_its_stockpiles()
    {
        var file = MapDataWriter.File(
            new Pinned(
                "OriginHex",
                0.30871195f,
                0.7751747f,
                "StorageFacility",
                LastUpdated,
                [("ItemCrates", "RifleC", 20), ("Vehicles", "TruckC", 3)],
                [("ÅSGARD", [("Structures", "MaterialPlatform", 1)])]
            )
        );

        var item = MapDataFile.Parse(file).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.Region, Is.EqualTo("OriginHex"));
            Assert.That(item.X, Is.EqualTo(0.30871195f));
            Assert.That(item.Y, Is.EqualTo(0.7751747f));
            Assert.That(item.CodeName, Is.EqualTo("StorageFacility"));
            Assert.That(item.LastUpdated, Is.EqualTo(LastUpdated));
            Assert.That(
                item.Details.ToJsonString(),
                Is.EqualTo("""{"Flags":128,"RefineryStorages":[]}""")
            );
            Assert.That(item.Stockpiles.Select(s => s.Name), Is.EqualTo(new[] { null, "ÅSGARD" }));
            Assert.That(
                item.Stockpiles[0].Entries,
                Is.EqualTo(
                    new[]
                    {
                        new StockpileEntryContent("item_crate", "RifleC", 20),
                        new StockpileEntryContent("vehicle", "TruckC", 3),
                    }
                )
            );
            Assert.That(
                item.Stockpiles[1].Entries.Single(),
                Is.EqualTo(new StockpileEntryContent("structure", "MaterialPlatform", 1))
            );
        }
    }

    [Test]
    public void Sums_two_entries_with_the_same_code_name()
    {
        var file = MapDataWriter.File(
            new Pinned(
                "OriginHex",
                0,
                0,
                "Seaport",
                LastUpdated,
                [("Items", "Water", 5), ("Items", "Water", 7)]
            )
        );

        Assert.That(
            MapDataFile.Parse(file).Single().Stockpiles[0].Entries.Single().Quantity,
            Is.EqualTo(12)
        );
    }

    [Test]
    public void Skips_a_property_of_an_unknown_type()
    {
        var file = MapDataWriter.File(
            "/Script/War.PlayerMapDataSaveGame",
            MapDataWriter.Properties(
                MapDataWriter.Property("Future", "TextProperty", [], [1, 2, 3, 4, 5]),
                MapDataWriter.StructArray("PinnedMapToolTipsC", "PinnedMapToolTipSaveData", [])
            )
        );

        Assert.That(MapDataFile.Parse(file), Is.Empty);
    }

    [Test]
    public void File_that_ends_too_early_is_a_format_error()
    {
        var file = MapDataWriter.File(
            new Pinned("OriginHex", 0, 0, "Seaport", LastUpdated, [("Items", "Water", 5)])
        );

        Assert.Throws<GvasFormatException>(() =>
            MapDataFile.Parse(file.AsMemory(0, file.Length / 2))
        );
    }

    [Test]
    public void Save_file_of_another_class_is_a_format_error()
    {
        var file = MapDataWriter.File("/Script/War.PlayerSaveGame", MapDataWriter.Properties());

        Assert.Throws<GvasFormatException>(() => MapDataFile.Parse(file));
    }
}
