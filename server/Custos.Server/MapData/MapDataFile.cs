using System.Text.Json.Nodes;

namespace Custos.Server.MapData;

/// <summary>A map item that the player pinned. The file holds no ID for it.</summary>
/// <param name="Region">The map name, for example <c>OriginHex</c>. The war API uses the same
/// names.</param>
/// <param name="X">The normalized map coordinate. For a world structure it is equal to <c>x</c> of
/// the war API.</param>
/// <param name="Y">The normalized map coordinate. For a world structure it is equal to <c>y</c> of
/// the war API.</param>
/// <param name="CodeName">The structure type, for example <c>StorageFacility</c>.</param>
/// <param name="LastUpdated">The time of the data in this item.</param>
/// <param name="Details">The members of the item details that have no table, for example
/// <c>RefineryStorages</c>.</param>
/// <param name="Stockpiles">The public stockpile first, then the reserve stockpiles.</param>
public sealed record PinnedItem(
    string Region,
    float X,
    float Y,
    string CodeName,
    DateTimeOffset LastUpdated,
    JsonObject Details,
    IReadOnlyList<StockpileContent> Stockpiles
);

/// <summary>A stockpile. <see cref="Name"/> is <c>null</c> for the public stockpile.</summary>
public sealed record StockpileContent(string? Name, IReadOnlyList<StockpileEntryContent> Entries);

/// <summary>One entry of a stockpile. <see cref="Category"/> is for example
/// <c>item_crate</c>.</summary>
public sealed record StockpileEntryContent(string Category, string CodeName, int Quantity);

/// <summary>The map data file of Foxhole (<c>&lt;steamid&gt;_MapData.sav</c>).</summary>
internal static class MapDataFile
{
    public const string SaveGameClass = "/Script/War.PlayerMapDataSaveGame";

    /// <summary>The members of <c>MapDetailStockpileInfo</c> and their categories.</summary>
    private static readonly (string Member, string Category)[] Categories =
    [
        ("Items", "item"),
        ("ItemCrates", "item_crate"),
        ("Vehicles", "vehicle"),
        ("VehicleCrates", "vehicle_crate"),
        ("Structures", "structure"),
        ("StructureCrates", "structure_crate"),
    ];

    /// <summary>The members of the item details that the stockpile tables hold.</summary>
    private const string StockpileInfo = "StockpileInfo";
    private const string ReserveStockpiles = "ReserveStockpileInfoList";

    /// <exception cref="GvasFormatException">The data is not a map data file.</exception>
    public static IReadOnlyList<PinnedItem> Parse(ReadOnlyMemory<byte> data)
    {
        var file = GvasReader.Read(data);
        if (file.SaveGameClass != SaveGameClass)
            throw new GvasFormatException($"The save game is not {SaveGameClass}.");

        return file.Properties["PinnedMapToolTipsC"] is JsonArray items
            ? [.. items.Select(item => PinnedItem(Required<JsonObject>(item, "the pinned item")))]
            : [];
    }

    private static PinnedItem PinnedItem(JsonObject item)
    {
        var mapId = Value<string>(item, "MapId");
        var coordinates = Required<JsonObject>(item["NormalizedMapCoords"], "NormalizedMapCoords");
        var details = Required<JsonObject>(
            item["RecentMapItemDetails"] ?? item["InitalMapItemDetails"],
            "the item details"
        );

        List<StockpileContent> stockpiles = [];
        if (details[StockpileInfo] is JsonObject publicStockpile)
            stockpiles.Add(new StockpileContent(null, Entries(publicStockpile)));
        foreach (var reserve in details[ReserveStockpiles] as JsonArray ?? new JsonArray())
        {
            var info = Required<JsonObject>(reserve, "the reserve stockpile");
            stockpiles.Add(
                new StockpileContent(
                    Value<string>(info, "StockpileName"),
                    Entries(info[StockpileInfo] as JsonObject ?? new JsonObject())
                )
            );
        }

        var rest = (JsonObject)details.DeepClone();
        rest.Remove(StockpileInfo);
        rest.Remove(ReserveStockpiles);

        return new PinnedItem(
            Region: Region(mapId),
            X: Value<float>(coordinates, "x"),
            Y: Value<float>(coordinates, "y"),
            CodeName: Value<string>(item, "CodeName"),
            LastUpdated: Value<DateTimeOffset>(item, "LastUpdated"),
            Details: rest,
            Stockpiles: stockpiles
        );
    }

    /// <summary>The map name from the enum value, for example <c>OriginHex</c> from
    /// <c>EWorldConquestMapId::OriginHex</c>.</summary>
    private static string Region(string mapId)
    {
        var separator = mapId.LastIndexOf("::", StringComparison.Ordinal);
        return separator < 0 ? mapId : mapId[(separator + 2)..];
    }

    /// <summary>The entries of a stockpile. The server sums two entries with the same code
    /// name.</summary>
    private static List<StockpileEntryContent> Entries(JsonObject info) =>
        [
            .. Categories.SelectMany(c =>
                (info[c.Member] as JsonArray ?? new JsonArray())
                    .Select(entry => Required<JsonObject>(entry, "the stockpile entry"))
                    .GroupBy(entry => Value<string>(entry, "CodeName"), StringComparer.Ordinal)
                    .Select(g => new StockpileEntryContent(
                        c.Category,
                        g.Key,
                        g.Sum(entry => Value<int>(entry, "Quantity"))
                    ))
            ),
        ];

    private static T Required<T>(JsonNode? node, string what)
        where T : JsonNode =>
        node as T ?? throw new GvasFormatException($"The file has no valid {what}.");

    private static T Value<T>(JsonObject parent, string name) =>
        parent[name] is JsonValue value
        && value.TryGetValue<T>(out var result)
        && result is not null
            ? result
            : throw new GvasFormatException($"The file has no valid {name}.");
}
