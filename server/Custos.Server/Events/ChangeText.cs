using System.Globalization;
using Custos.Server.Catalog;

namespace Custos.Server.Events;

/// <summary>
/// The text of a stockpile change for a person. The first line names the place. Each next line
/// gives one change with the quantity before and after, the most important change first. The last
/// line gives the number of changes that the text leaves out:
/// <code>
/// Fort Barley Seaport, Main
/// Basic Materials 400 → 0
/// Rifle crates 20 → 40
/// +5 more
/// </code>
/// A new stockpile gets one line: <c>Fort Barley Seaport, Main: new, 12 items</c>.
/// </summary>
internal static class ChangeText
{
    public const int MaxChanges = 5;

    /// <summary>The limit of the text of a stream message (spec/05-stream.md).</summary>
    public const int MaxLength = 500;

    /// <summary>The text for the change from <paramref name="before"/> to
    /// <paramref name="after"/>. A new stockpile has no <paramref name="before"/>. Null when no
    /// quantity changed, for example when only an entry with the quantity 0 came or went.</summary>
    public static string? Make(
        string structureLabel,
        string stockpileName,
        IReadOnlyDictionary<EntryKey, int>? before,
        IReadOnlyDictionary<EntryKey, int> after,
        GameCatalog catalog
    )
    {
        var place = $"{structureLabel}, {stockpileName}";
        if (before is null)
        {
            var count = after.Count(e => e.Value != 0);
            return Cut($"{place}: new, {count} {(count == 1 ? "item" : "items")}");
        }

        var changes = Rank(before, after, catalog)
            .Select(c => $"{Name(c.Key, catalog)} {Number(c.Before)} → {Number(c.After)}")
            .ToList();
        if (changes.Count == 0)
            return null;

        // Fewer change lines when the text is too long, for example with a long label.
        for (var shown = Math.Min(changes.Count, MaxChanges); ; shown--)
        {
            List<string> lines = [place, .. changes.Take(shown)];
            if (shown < changes.Count)
                lines.Add($"+{Number(changes.Count - shown)} more");
            var text = string.Join('\n', lines);
            if (text.Length <= MaxLength || shown == 0)
                return Cut(text);
        }
    }

    /// <summary>
    /// The changes, the most important first (ranking option A of issue #51): an entry that goes
    /// to 0, then the largest change in crates, then a decrease before an increase of the same
    /// size. A crated entry counts as crates. A loose entry is divided by its crate size from the
    /// catalog, or counts as is without one.
    /// </summary>
    private static IEnumerable<(EntryKey Key, int Before, int After)> Rank(
        IReadOnlyDictionary<EntryKey, int> before,
        IReadOnlyDictionary<EntryKey, int> after,
        GameCatalog catalog
    ) =>
        before
            .Keys.Union(after.Keys)
            .Select(k =>
                (Key: k, Before: before.GetValueOrDefault(k), After: after.GetValueOrDefault(k))
            )
            .Where(c => c.Before != c.After)
            .OrderByDescending(c => c.After == 0)
            .ThenByDescending(c => Math.Abs(c.After - c.Before) / (double)CrateSize(c.Key, catalog))
            .ThenBy(c => c.After > c.Before)
            .ThenBy(c => Name(c.Key, catalog), StringComparer.OrdinalIgnoreCase);

    private static int CrateSize(EntryKey key, GameCatalog catalog) =>
        key.Crated ? 1
        : catalog.Find(key.CodeName)?.CrateSize is > 0 and var size ? size
        : 1;

    private static string Name(EntryKey key, GameCatalog catalog)
    {
        var name = catalog.DisplayName(key.CodeName);
        return key.Crated ? $"{name} crates" : name;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Cut(string text) =>
        text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
}

/// <summary>The key of a stockpile entry: its category and its code name.</summary>
internal readonly record struct EntryKey(string Category, string CodeName)
{
    /// <summary>True for an entry in crates, for example the category <c>item_crate</c>.</summary>
    public bool Crated => Category.EndsWith("_crate", StringComparison.Ordinal);
}
