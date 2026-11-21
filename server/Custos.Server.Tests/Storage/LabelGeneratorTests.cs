using Custos.Server.Storage;

namespace Custos.Server.Tests.Storage;

public sealed class LabelGeneratorTests
{
    private static readonly Guid Id = Guid.Parse("f0e1d2c3-b4a5-5968-8776-655443322110");
    private static readonly Guid War = Guid.NewGuid();

    [Test]
    public void Code_is_crockford_base32_of_the_start_of_the_id()
    {
        // f0 e1 d2 = 11110 00011 10000 11101 0010 -> Y 3 G X
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LabelGenerator.Code(Id, 4), Is.EqualTo("Y3GX"));
            Assert.That(LabelGenerator.Code(Id, 6), Does.StartWith("Y3GX"));
            Assert.That(LabelGenerator.Code(Id, 26), Has.Length.EqualTo(26));
        }
    }

    [Test]
    public void Place_is_the_nearest_place_name()
    {
        MapPlace[] places = [Place("Far", 0.9f, 0.9f, major: true), Place("Near", 0.3f, 0.3f)];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(LabelGenerator.Place(places, 0.25f, 0.3f), Is.EqualTo("Near"));
            Assert.That(LabelGenerator.Place([], 0.25f, 0.3f), Is.Null);
        }
    }

    [Test]
    public void World_structure_has_the_coordinates_of_a_map_item()
    {
        MapItem[] items = [Item(0.43503433f, 0.83201146f)];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(LabelGenerator.IsWorldStructure(items, 0.43503433f, 0.83201146f), Is.True);
            Assert.That(LabelGenerator.IsWorldStructure(items, 0.4351f, 0.83201146f), Is.False);
        }
    }

    [TestCase("TheFingersHex", "The Fingers")]
    [TestCase("MarbanHollow", "Marban Hollow")]
    [TestCase("OriginHex", "Origin")]
    public void Region_name_is_readable(string region, string name) =>
        Assert.That(LabelGenerator.RegionName(region), Is.EqualTo(name));

    [Test]
    public void World_structure_gets_place_and_type()
    {
        Assert.That(
            LabelGenerator.Generate("Fort Barley", "Seaport", true, Id, _ => false),
            Is.EqualTo("Fort Barley Seaport")
        );
    }

    [Test]
    public void Player_built_structure_gets_a_code()
    {
        Assert.That(
            LabelGenerator.Generate("Fort Barley", "Bunker Base", false, Id, _ => false),
            Is.EqualTo("Fort Barley Bunker Base Y3GX")
        );
    }

    [Test]
    public void Equal_label_gets_the_code_or_more_of_the_code()
    {
        HashSet<string> taken = ["Fort Barley Seaport", "Fort Barley Seaport Y3GX"];

        Assert.That(
            LabelGenerator.Generate("Fort Barley", "Seaport", true, Id, taken.Contains),
            Is.EqualTo("Fort Barley Seaport Y3GX" + LabelGenerator.Code(Id, 5)[4])
        );
    }

    private static MapPlace Place(string name, float x, float y, bool major = false) =>
        new()
        {
            WarId = War,
            Region = "TheFingersHex",
            PlaceIndex = 0,
            Name = name,
            X = x,
            Y = y,
            Major = major,
        };

    private static MapItem Item(float x, float y) =>
        new()
        {
            WarId = War,
            Region = "TheFingersHex",
            ItemIndex = 0,
            IconType = 52,
            X = x,
            Y = y,
        };
}
