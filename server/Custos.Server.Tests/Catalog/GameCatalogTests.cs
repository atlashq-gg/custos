using Custos.Server.Catalog;
using Custos.Server.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Custos.Server.Tests.Catalog;

public sealed class GameCatalogTests
{
    private static readonly GameCatalog Catalog = GameCatalog.Load(
        Path.Combine(AppContext.BaseDirectory, "Catalog", "test-catalog.json")
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
                        "colonial"
                    )
                )
            );
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
