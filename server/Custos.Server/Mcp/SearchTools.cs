using System.ComponentModel;
using System.Globalization;
using Custos.Server.Catalog;
using Custos.Server.Discord;
using Custos.Server.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Custos.Server.Mcp;

/// <summary>What the search applied: the war, the access of the caller, and the code names that
/// each item filter matched.</summary>
public sealed record SearchApplied(
    int? War,
    IReadOnlyList<string> Access,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Items
);

/// <summary>An item, vehicle, or structure type, with the number in a crate and in a pallet when
/// the catalog has them.</summary>
public sealed record ItemName(string CodeName, string DisplayName, int? CrateSize, int? PalletSize);

public sealed record StructureRef(
    string StructureId,
    string Label,
    string Type,
    string Region,
    string? OriginGuild
);

public sealed record ItemRow(
    ItemName Item,
    bool Crated,
    int Quantity,
    string StockpileId,
    string Stockpile,
    StructureRef Structure,
    DateTimeOffset LastSeenAt,
    string? OriginGuild
);

public sealed record ItemTotal(ItemName Item, bool Crated, int Quantity);

public sealed record ItemSearch(
    SearchApplied Applied,
    IReadOnlyList<ItemRow> Items,
    int Total,
    IReadOnlyList<ItemTotal> Totals,
    string? NextCursor
);

public sealed record EntryRow(ItemName Item, bool Crated, int Quantity);

public sealed record StockpileRow(
    string StockpileId,
    string Name,
    DateTimeOffset LastSeenAt,
    string? OriginGuild,
    IReadOnlyList<EntryRow> Entries
);

public sealed record StructureRow(
    string StructureId,
    string Label,
    string? GeneratedLabel,
    string Type,
    string CodeName,
    string Category,
    string Region,
    string? Place,
    string? OriginGuild,
    IReadOnlyList<StockpileRow> Stockpiles
);

public sealed record StructureSearch(
    SearchApplied Applied,
    IReadOnlyList<StructureRow> Items,
    int Total,
    string? NextCursor
);

/// <summary>
/// The searches in the records of a guild and of each guild that shares with it. Each filter is a
/// list. A row matches when it matches one value of each list that the call gives. A filter that
/// matches nothing narrows the result. It is not a refusal.
/// </summary>
[McpServerToolType]
public sealed class SearchTools(
    McpCallContext context,
    IServiceProvider services,
    GuildRecords records,
    WarRegistry wars,
    GameCatalog catalog
) : McpTools(context, services)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    private const string PublicStockpile = "public";

    [McpServerTool(
        Name = "find_items",
        Title = "Find items",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false
    )]
    [McpGuard(McpScope.Read, GuildAccess.Read)]
    [Description(
        "The stockpiles that hold an item, with the quantities. One row for each item in each "
            + "stockpile, and one total for each item. A row with crated true counts crates: "
            + "crate_size is the number in one crate, and pallet_size is the number in one "
            + "pallet. Filters: items (code name or name), "
            + "stockpiles (ID, part of a name, or public), structures (ID or part of a label), "
            + "and structure_categories. war is a war number, and the default is the current war."
    )]
    public Task<CallToolResult> FindItems(
        [Description("The ID of the Discord guild.")] string guild_id,
        [Description("Item code names or names.")] string[]? items = null,
        [Description("Stockpile IDs, parts of stockpile names, or public.")]
            string[]? stockpiles = null,
        [Description("Structure IDs or parts of labels.")] string[]? structures = null,
        [Description("Structure categories.")] string[]? structure_categories = null,
        [Description("A war number.")] int? war = null,
        [Description("At most this many rows, 50 by default and 200 at most.")] int? limit = null,
        [Description("The next_cursor of the earlier page.")] string? cursor = null,
        CancellationToken cancellationToken = default
    ) => RunAsync("find_items", async () =>
        {
            var (applied, view, filter) = await PrepareAsync(
                items,
                stockpiles,
                structures,
                structure_categories,
                war,
                cancellationToken
            );
            var rows = view is null
                ? []
                : (
                    from stockpile in filter.Stockpiles(view)
                    from entry in stockpile.Entries
                    where filter.Item(entry.CodeName)
                    let structure = view.Structures[stockpile.StructureId]
                    select new ItemRow(
                        Name(entry.CodeName),
                        IsCrated(entry.Category),
                        entry.Quantity,
                        stockpile.StockpileId.ToString(),
                        stockpile.Name ?? "Public",
                        Ref(structure),
                        stockpile.LastSeenAt,
                        Origin(stockpile.OriginGuild)
                    )
                )
                    .OrderBy(r => r.Item.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Crated)
                    .ThenByDescending(r => r.Quantity)
                    .ThenBy(r => r.StockpileId, StringComparer.Ordinal)
                    .ToList();
            var totals = rows.GroupBy(r => (r.Item.CodeName, r.Crated))
                .Select(g => new ItemTotal(g.First().Item, g.Key.Crated, g.Sum(r => r.Quantity)))
                .OrderBy(t => t.Item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Crated)
                .ToList();
            var (page, next) = Page(rows, limit, cursor);
            return new ItemSearch(applied, page, rows.Count, totals, next);
        });

    [McpServerTool(
        Name = "find_structures",
        Title = "Find structures",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false
    )]
    [McpGuard(McpScope.Read, GuildAccess.Read)]
    [Description(
        "The structures, with their labels, places, stockpiles, and items. One row for each "
            + "structure. An item with crated true counts crates: crate_size is the number in one "
            + "crate, and pallet_size is the number in one pallet. Filters: structures (ID or part of a label), structure_categories, "
            + "items (code name or name), and stockpiles (ID, part of a name, or public). war is a "
            + "war number, and the default is the current war."
    )]
    public Task<CallToolResult> FindStructures(
        [Description("The ID of the Discord guild.")] string guild_id,
        [Description("Structure IDs or parts of labels.")] string[]? structures = null,
        [Description("Structure categories.")] string[]? structure_categories = null,
        [Description("Item code names or names.")] string[]? items = null,
        [Description("Stockpile IDs, parts of stockpile names, or public.")]
            string[]? stockpiles = null,
        [Description("A war number.")] int? war = null,
        [Description("At most this many rows, 50 by default and 200 at most.")] int? limit = null,
        [Description("The next_cursor of the earlier page.")] string? cursor = null,
        CancellationToken cancellationToken = default
    ) => RunAsync("find_structures", async () =>
        {
            var (applied, view, filter) = await PrepareAsync(
                items,
                stockpiles,
                structures,
                structure_categories,
                war,
                cancellationToken
            );
            if (view is null)
                return new StructureSearch(applied, [], 0, null);

            var byStructure = filter
                .Stockpiles(view)
                .Select(s => new
                {
                    Stockpile = s,
                    Entries = s.Entries.Where(e => filter.Item(e.CodeName)).ToList(),
                })
                .Where(s => !filter.HasItems || s.Entries.Count > 0)
                .ToLookup(s => s.Stockpile.StructureId);
            var rows = view
                .Structures.Values.Where(filter.Structure)
                .Where(s => !filter.NarrowsStockpiles || byStructure[s.StructureId].Any())
                .Select(s => new StructureRow(
                    s.StructureId.ToString(),
                    s.ShownLabel,
                    s.GeneratedLabel,
                    catalog.StructureType(s.CodeName),
                    s.CodeName,
                    catalog.Category(s.CodeName),
                    LabelGenerator.RegionName(s.Region),
                    view.Places.TryGetValue(s.Region, out var places)
                        ? LabelGenerator.Place(places, s.X, s.Y)
                        : null,
                    Origin(s.OriginGuild),
                    [
                        .. byStructure[s.StructureId]
                            .OrderBy(p => p.Stockpile.Name is not null)
                            .ThenBy(p => p.Stockpile.Name, StringComparer.OrdinalIgnoreCase)
                            .Select(p => new StockpileRow(
                                p.Stockpile.StockpileId.ToString(),
                                p.Stockpile.Name ?? "Public",
                                p.Stockpile.LastSeenAt,
                                Origin(p.Stockpile.OriginGuild),
                                [
                                    .. p
                                        .Entries.OrderBy(
                                            e => catalog.DisplayName(e.CodeName),
                                            StringComparer.OrdinalIgnoreCase
                                        )
                                        .Select(e => new EntryRow(
                                            Name(e.CodeName),
                                            IsCrated(e.Category),
                                            e.Quantity
                                        )),
                                ]
                            )),
                    ]
                ))
                .OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.StructureId, StringComparer.Ordinal)
                .ToList();
            var (page, next) = Page(rows, limit, cursor);
            return new StructureSearch(applied, page, rows.Count, next);
        });

    /// <summary>The war, the records, and the filter of a search. Without a war, the records are
    /// <c>null</c>.</summary>
    private async Task<(SearchApplied, ReadableRecords?, Filter)> PrepareAsync(
        string[]? items,
        string[]? stockpiles,
        string[]? structures,
        string[]? categories,
        int? warNumber,
        CancellationToken cancellationToken
    )
    {
        var war = warNumber is { } number
            ? await wars.FindAsync(number, cancellationToken)
                ?? throw new McpException($"The server has no record of war {number}.")
            : await wars.CurrentAsync(cancellationToken);
        var filter = new Filter(
            catalog,
            Clean(items),
            Clean(stockpiles),
            Clean(structures),
            Clean(categories)
        );
        var applied = new SearchApplied(
            war?.WarNumber,
            GuildTools.Kinds(Context.Check!.Access),
            filter.HasItems ? filter.MatchedItems : null
        );
        if (war is null)
            return (applied, null, filter);

        var view = await records.ReadAsync(Context.GuildId!.Value, war.WarId, cancellationToken);
        filter.Complete(view);
        return (
            applied with
            {
                Items = filter.HasItems ? filter.MatchedItems : null,
            },
            view,
            filter
        );
    }

    private ItemName Name(string codeName)
    {
        var entry = catalog.Find(codeName);
        return new(codeName, catalog.DisplayName(codeName), entry?.CrateSize, entry?.PalletSize);
    }

    private StructureRef Ref(StructureRecord structure) =>
        new(
            structure.StructureId.ToString(),
            structure.ShownLabel,
            catalog.StructureType(structure.CodeName),
            LabelGenerator.RegionName(structure.Region),
            Origin(structure.OriginGuild)
        );

    /// <summary>The guild of a record of another guild, or <c>null</c> for the guild of the
    /// call.</summary>
    private string? Origin(long guildId) =>
        (ulong)guildId == Context.GuildId ? null : guildId.ToString(CultureInfo.InvariantCulture);

    private static bool IsCrated(string category) =>
        category.EndsWith("_crate", StringComparison.Ordinal);

    private static IReadOnlyList<string> Clean(string[]? values) =>
        values is null ? [] : [.. values.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct()];

    private static (IReadOnlyList<T> Page, string? Next) Page<T>(
        IReadOnlyList<T> rows,
        int? limit,
        string? cursor
    )
    {
        var size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLimit)
            throw new McpException($"limit must be between 1 and {MaxLimit}.");
        var offset = 0;
        if (
            cursor is not null
            && (
                !int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset)
                || offset > rows.Count
            )
        )
        {
            throw new McpException("cursor is not a next_cursor of this search.");
        }
        var page = rows.Skip(offset).Take(size).ToList();
        var next =
            offset + page.Count < rows.Count
                ? (offset + page.Count).ToString(CultureInfo.InvariantCulture)
                : null;
        return (page, next);
    }

    /// <summary>The filters of a search. A value matches without regard to case.</summary>
    private sealed class Filter(
        GameCatalog catalog,
        IReadOnlyList<string> items,
        IReadOnlyList<string> stockpiles,
        IReadOnlyList<string> structures,
        IReadOnlyList<string> categories
    )
    {
        private HashSet<string> _codes = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyDictionary<Guid, StructureRecord> _structures =
            new Dictionary<Guid, StructureRecord>();

        public bool HasItems => items.Count > 0;
        public bool NarrowsStockpiles => HasItems || stockpiles.Count > 0;

        public Dictionary<string, IReadOnlyList<string>> MatchedItems { get; } = [];

        /// <summary>Matches the item filters with the catalog and with the code names of the
        /// records, so that a code name without an entry works too.</summary>
        public void Complete(ReadableRecords view)
        {
            _structures = view.Structures;
            var known = view
                .Stockpiles.SelectMany(s => s.Entries)
                .Select(e => e.CodeName)
                .Distinct()
                .ToList();
            foreach (var value in items)
            {
                var codes = catalog
                    .Match(value)
                    .Concat(known.Where(c => c.Contains(value, StringComparison.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.Ordinal)
                    .ToList();
                MatchedItems[value] = codes;
                _codes.UnionWith(codes);
            }
        }

        public bool Item(string codeName) => !HasItems || _codes.Contains(codeName);

        public IEnumerable<StockpileRecord> Stockpiles(ReadableRecords view) =>
            view.Stockpiles.Where(s =>
                Stockpile(s)
                && _structures.TryGetValue(s.StructureId, out var structure)
                && Structure(structure)
            );

        private bool Stockpile(StockpileRecord stockpile) =>
            stockpiles.Count == 0
            || stockpiles.Any(v =>
                v.Equals(stockpile.StockpileId.ToString(), StringComparison.OrdinalIgnoreCase)
                || (
                    stockpile.Name is null
                    && v.Equals(PublicStockpile, StringComparison.OrdinalIgnoreCase)
                )
                || (stockpile.Name?.Contains(v, StringComparison.OrdinalIgnoreCase) ?? false)
            );

        public bool Structure(StructureRecord structure) =>
            (
                structures.Count == 0
                || structures.Any(v =>
                    v.Equals(structure.StructureId.ToString(), StringComparison.OrdinalIgnoreCase)
                    || structure.ShownLabel.Contains(v, StringComparison.OrdinalIgnoreCase)
                )
            )
            && (
                categories.Count == 0
                || categories.Any(v =>
                    catalog
                        .Category(structure.CodeName)
                        .Contains(v, StringComparison.OrdinalIgnoreCase)
                    || structure.CodeName.Contains(v, StringComparison.OrdinalIgnoreCase)
                )
            );
    }
}
