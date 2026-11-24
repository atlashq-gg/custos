using Custos.Server.Catalog;
using Custos.Server.Events;

namespace Custos.Server.Tests.Events;

public sealed class ChangeTextTests
{
    private static readonly GameCatalog Catalog = GameCatalog.Load(
        Path.Combine(AppContext.BaseDirectory, "TestData", "test-catalog.json")
    );

    [Test]
    public void New_stockpile_gives_the_number_of_items() =>
        Assert.That(
            Make(null, ("item", "RifleC", 5), ("vehicle", "TruckC", 1)),
            Is.EqualTo("Depot, Main: new, 2 items")
        );

    [Test]
    public void Change_gives_the_place_and_each_change_with_before_and_after() =>
        Assert.That(
            Make(
                [("item_crate", "RifleC", 20), ("item", "RifleC", 3), ("vehicle", "TruckC", 2)],
                ("item_crate", "RifleC", 40),
                ("vehicle", "TruckC", 2)
            ),
            Is.EqualTo("Depot, Main\nTest Rifle -3 (0)\nTest Rifle crates +20 (40)")
        );

    [Test]
    public void Entry_that_goes_to_0_comes_before_a_larger_change() =>
        Assert.That(
            Lines(
                Make(
                    [("item_crate", "RifleC", 1), ("vehicle", "TruckC", 1)],
                    ("item_crate", "RifleC", 100)
                )
            ),
            Is.EqualTo(new[] { "Depot, Main", "Test Truck -1 (0)", "Test Rifle crates +99 (100)" })
        );

    [Test]
    public void Loose_entry_counts_in_crates_of_its_crate_size() =>
        // 60 loose rifles are 3 crates of 20. 5 rifle crates are more.
        Assert.That(
            Lines(
                Make(
                    [("item", "RifleC", 100), ("item_crate", "RifleC", 10)],
                    ("item", "RifleC", 160),
                    ("item_crate", "RifleC", 15)
                )
            ),
            Is.EqualTo(new[] { "Depot, Main", "Test Rifle crates +5 (15)", "Test Rifle +60 (160)" })
        );

    [Test]
    public void Decrease_comes_before_an_increase_of_the_same_size() =>
        Assert.That(
            Lines(
                Make(
                    [("item_crate", "A", 10), ("item_crate", "B", 10)],
                    ("item_crate", "A", 15),
                    ("item_crate", "B", 5)
                )
            ),
            Is.EqualTo(new[] { "Depot, Main", "B crates -5 (5)", "A crates +5 (15)" })
        );

    [Test]
    public void More_than_5_changes_give_the_number_of_the_others()
    {
        var before = Enumerable.Range(1, 8).Select(i => ("item_crate", $"I{i}", i)).ToArray();
        var after = Enumerable.Range(1, 8).Select(i => ("item_crate", $"I{i}", i * 2)).ToArray();

        Assert.That(
            Lines(Make(before, after)),
            Is.EqualTo(
                new[]
                {
                    "Depot, Main",
                    "I8 crates +8 (16)",
                    "I7 crates +7 (14)",
                    "I6 crates +6 (12)",
                    "I5 crates +5 (10)",
                    "I4 crates +4 (8)",
                    "+3 more",
                }
            )
        );
    }

    [Test]
    public void Long_text_has_fewer_change_lines_and_stays_in_the_limit()
    {
        var label = new string('x', 400);
        var before = Enumerable.Range(1, 8).Select(i => ("item_crate", $"{label}{i}", 1)).ToArray();

        var text = ChangeText.Make("Depot", "Main", Entries(before), Entries([]), Catalog);

        Assert.That(text, Has.Length.LessThanOrEqualTo(ChangeText.MaxLength));
        Assert.That(text, Does.EndWith("\n+7 more"));
    }

    [Test]
    public void Long_label_is_cut_at_the_limit_of_a_message() =>
        Assert.That(
            ChangeText.Make(new string('x', 600), "Main", null, Entries([]), Catalog),
            Has.Length.EqualTo(ChangeText.MaxLength)
        );

    [Test]
    public void Change_of_an_entry_with_the_quantity_0_gives_no_text() =>
        Assert.That(Make([("item", "RifleC", 0)]), Is.Null);

    private static string? Make(
        (string Category, string CodeName, int Quantity)[]? before,
        params (string Category, string CodeName, int Quantity)[] after
    ) =>
        ChangeText.Make(
            "Depot",
            "Main",
            before is null ? null : Entries(before),
            Entries(after),
            Catalog
        );

    private static string[] Lines(string? text) => text?.Split('\n') ?? [];

    private static Dictionary<EntryKey, int> Entries(
        (string Category, string CodeName, int Quantity)[] entries
    ) => entries.ToDictionary(e => new EntryKey(e.Category, e.CodeName), e => e.Quantity);
}
