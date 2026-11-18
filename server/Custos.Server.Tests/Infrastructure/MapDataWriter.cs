using System.Buffers.Binary;
using System.Text;

namespace Custos.Server.Tests.Infrastructure;

/// <summary>A pinned item for <see cref="MapDataWriter"/>.</summary>
/// <param name="Public">The entries of the public stockpile, by member, for example
/// <c>("ItemCrates", "RifleC", 20)</c>.</param>
/// <param name="Reserves">The reserve stockpiles and their entries.</param>
internal sealed record Pinned(
    string Region,
    float X,
    float Y,
    string CodeName,
    DateTimeOffset LastUpdated,
    (string Member, string CodeName, short Quantity)[] Public,
    (string Name, (string Member, string CodeName, short Quantity)[] Entries)[]? Reserves = null
);

/// <summary>
/// Writes a map data file in the GVAS format of UE 4.24, with the members that the server reads.
/// The tests do not use a real file of a player: it holds the names of reserve stockpiles and squad
/// IDs.
/// </summary>
internal static class MapDataWriter
{
    private static readonly string[] Members =
    [
        "Items",
        "ItemCrates",
        "Vehicles",
        "VehicleCrates",
        "Structures",
        "StructureCrates",
    ];

    public static byte[] File(params Pinned[] items) =>
        File(
            "/Script/War.PlayerMapDataSaveGame",
            Properties(
                StructArray(
                    "PinnedMapToolTipsC",
                    "PinnedMapToolTipSaveData",
                    [.. items.Select(Item)]
                )
            )
        );

    public static byte[] File(string saveGameClass, byte[] properties) =>
        [
            .. "GVAS"u8,
            .. Int32(2),
            .. Int32(518),
            .. UInt16(4),
            .. UInt16(24),
            .. UInt16(3),
            .. Int32(0),
            .. String("++UE4+Release-4.24"),
            .. Int32(3),
            .. Int32(1),
            .. new byte[16],
            .. Int32(7),
            .. String(saveGameClass),
            .. properties,
            .. Int32(0),
        ];

    private static byte[] Item(Pinned item) =>
        Properties(
            Struct("NormalizedMapCoords", "Vector2D", [.. Single(item.X), .. Single(item.Y)]),
            Enum("MapId", "EWorldConquestMapId", $"EWorldConquestMapId::{item.Region}"),
            Struct(
                "RecentMapItemDetails",
                "MapItemDetails",
                Properties(
                    Int("Flags", 128),
                    Struct("StockpileInfo", "MapDetailStockpileInfo", StockpileInfo(item.Public)),
                    StructArray(
                        "ReserveStockpileInfoList",
                        "ReserveStockpileMapDetailInfo",
                        [
                            .. (item.Reserves ?? []).Select(r =>
                                Properties(
                                    Str("StockpileName", r.Name),
                                    Struct(
                                        "StockpileInfo",
                                        "MapDetailStockpileInfo",
                                        StockpileInfo(r.Entries)
                                    )
                                )
                            ),
                        ]
                    ),
                    StructArray("RefineryStorages", "MapDetailRefineryStorage", [])
                )
            ),
            Name("CodeName", item.CodeName),
            Struct("LastUpdated", "DateTime", Int64(item.LastUpdated.UtcTicks))
        );

    private static byte[] StockpileInfo(
        (string Member, string CodeName, short Quantity)[] entries
    ) =>
        Properties([
            .. Members.Select(member =>
                StructArray(
                    member,
                    "StockpileEntry",
                    [
                        .. entries
                            .Where(e => e.Member == member)
                            .Select(e =>
                                Properties(
                                    Name("CodeName", e.CodeName),
                                    Int16("Quantity", e.Quantity)
                                )
                            ),
                    ]
                )
            ),
        ]);

    public static byte[] Properties(params byte[][] properties) =>
        [.. properties.SelectMany(p => p), .. String("None")];

    public static byte[] Struct(string name, string structName, byte[] body) =>
        Property(name, "StructProperty", [.. String(structName), .. new byte[16]], body);

    public static byte[] StructArray(string name, string structName, byte[][] elements)
    {
        var body = elements.SelectMany(e => e).ToArray();
        return Property(
            name,
            "ArrayProperty",
            String("StructProperty"),
            [
                .. Int32(elements.Length),
                .. String(name),
                .. String("StructProperty"),
                .. Int64(body.Length),
                .. String(structName),
                .. new byte[16],
                0,
                .. body,
            ]
        );
    }

    public static byte[] Int(string name, int value) =>
        Property(name, "IntProperty", [], Int32(value));

    public static byte[] Int16(string name, short value) =>
        Property(name, "Int16Property", [], BitConverter.GetBytes(value));

    public static byte[] Name(string name, string value) =>
        Property(name, "NameProperty", [], String(value));

    public static byte[] Str(string name, string value) =>
        Property(name, "StrProperty", [], String(value));

    public static byte[] Enum(string name, string enumName, string value) =>
        Property(name, "EnumProperty", String(enumName), String(value));

    /// <summary>A property tag and its body. The size counts the body only.</summary>
    public static byte[] Property(string name, string type, byte[] tag, byte[] body) =>
        [.. String(name), .. String(type), .. Int64(body.Length), .. tag, 0, .. body];

    /// <summary>An FString: Latin-1 when possible, else UTF-16 with a negative length.</summary>
    public static byte[] String(string value)
    {
        if (value.All(c => c < 128))
            return [.. Int32(value.Length + 1), .. Encoding.Latin1.GetBytes(value), 0];
        return [.. Int32(-(value.Length + 1)), .. Encoding.Unicode.GetBytes(value), 0, 0];
    }

    private static byte[] Int32(int value) => BitConverter.GetBytes(value);

    private static byte[] Int64(long value) => BitConverter.GetBytes(value);

    private static byte[] UInt16(ushort value) => BitConverter.GetBytes(value);

    private static byte[] Single(float value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        return bytes;
    }
}
