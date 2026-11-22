using Custos.Server.Catalog;
using Custos.Server.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Custos.Server.Tests.Catalog;

public sealed class GameCatalogTests
{
    private static readonly GameCatalog Catalog = GameCatalog.Load(
        Path.Combine(AppContext.BaseDirectory, "TestData", "test-catalog.json")
    );

    [Test]
    public void File_is_read()
    {
        var rifle = Catalog.Find("riflec");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Catalog.Count, Is.EqualTo(5));
            Assert.That(
                rifle,
                Is.EqualTo(
                    new CatalogEntry(
                        "RifleC",
                        "Test Rifle",
                        CatalogKind.Item,
                        "Test Arms",
                        "colonial",
                        20,
                        120
                    )
                )
            );
        }
    }

    // The file that the build copies from server/catalog. A run of the script that goes wrong, for
    // example when the wiki changes its tables, fails here.
    [Test]
    public void Shipped_catalog_has_the_names_of_the_sample_of_0_1_0()
    {
        var shipped = GameCatalog.Load(
            Path.Combine(AppContext.BaseDirectory, "catalog", "catalog.json")
        );

        using (Assert.EnterMultipleScope())
        {
            Assert.That(shipped.Count, Is.GreaterThan(500));
            Assert.That(shipped.DisplayName("StorageFacility"), Is.EqualTo("Storage Depot"));
            Assert.That(shipped.DisplayName("Seaport"), Is.EqualTo("Seaport"));
            Assert.That(shipped.Find("RifleC")?.Kind, Is.EqualTo(CatalogKind.Item));
            Assert.That(shipped.Find("TruckC")?.Kind, Is.EqualTo(CatalogKind.Vehicle));
            Assert.That(shipped.StructureType("FortBaseT2"), Is.EqualTo("Bunker Base"));
            Assert.That(shipped.Find("Cloth")?.CrateSize, Is.EqualTo(100));
            Assert.That(shipped.Find("AAAmmo")?.PalletSize, Is.EqualTo(60));
            Assert.That(shipped.Find("TruckC")?.CrateSize, Is.EqualTo(3));
            Assert.That(shipped.Find("BusC")?.CrateSize, Is.EqualTo(3));
            Assert.That(shipped.Find("MediumTankC")?.CrateSize, Is.EqualTo(5));
            Assert.That(shipped.Find("AircraftBomberC")?.CrateSize, Is.Null);
            Assert.That(shipped.Find("EmplacedATW")?.CrateSize, Is.EqualTo(3));
            Assert.That(shipped.Find("Seaport")?.CrateSize, Is.Null);
            Assert.That(shipped.Find("StorageFacility")?.CrateSize, Is.Null);
        }
    }

    [Test]
    public void Code_name_without_an_entry_is_its_own_name_and_category()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Catalog.DisplayName("Unknown"), Is.EqualTo("Unknown"));
            Assert.That(Catalog.Category("Unknown"), Is.EqualTo("Unknown"));
            Assert.That(Catalog.StructureType("Unknown"), Is.EqualTo("Unknown"));
        }
    }

    [Test]
    public void Structure_type_has_no_tier() =>
        Assert.That(Catalog.StructureType("FortBaseT2"), Is.EqualTo("Test Base"));

    [TestCase("rifle", new[] { "RifleC" })]
    [TestCase("TRUCKc", new[] { "TruckC" })]
    [TestCase("test port", new[] { "Seaport" })]
    [TestCase("nothing", new string[0])]
    public void Filter_matches_the_code_name_or_the_display_name(string filter, string[] codes) =>
        Assert.That(Catalog.Match(filter), Is.EquivalentTo(codes));

    [Test]
    public void Missing_file_gives_the_empty_catalog() =>
        Assert.That(GameCatalog.Load("/nonexistent/catalog.json").Count, Is.Zero);

    [Test]
    public void Server_loads_the_file_of_the_configuration()
    {
        using var factory = new CustosFactory();

        Assert.That(factory.Services.GetRequiredService<GameCatalog>().Count, Is.EqualTo(5));
    }
}
