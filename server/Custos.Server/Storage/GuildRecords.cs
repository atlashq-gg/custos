using Custos.Server.Discord;
using Microsoft.EntityFrameworkCore;

namespace Custos.Server.Storage;

/// <summary>A structure in the records that a guild can read.</summary>
public sealed record StructureRecord(
    Guid StructureId,
    long OriginGuild,
    string CodeName,
    string Region,
    float X,
    float Y,
    string? Label,
    string? GeneratedLabel
)
{
    /// <summary>The label that shows.</summary>
    public string ShownLabel => Label ?? GeneratedLabel ?? CodeName;
}

/// <summary>A stockpile in the records that a guild can read, with its entries.</summary>
public sealed record StockpileRecord(
    Guid StockpileId,
    Guid StructureId,
    long OriginGuild,
    string? Name,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<EntryRecord> Entries
);

public sealed record EntryRecord(string Category, string CodeName, int Quantity);

/// <summary>The records that a guild can read in one war: its own, and the records of each guild
/// that shares with it.</summary>
public sealed record ReadableRecords(
    IReadOnlyDictionary<Guid, StructureRecord> Structures,
    IReadOnlyList<StockpileRecord> Stockpiles,
    IReadOnlyDictionary<string, IReadOnlyList<MapPlace>> Places
);

/// <summary>
/// Reads the records of a guild and of each guild that shares with it, as one query for each
/// table. For a stockpile that two guilds know, the record with the latest sighting wins. The
/// label of a structure is the label of the reader guild when it has its own record, else the label
/// of the guild of the record.
/// </summary>
public sealed class GuildRecords(CustosDbContext db, GuildSettingsStore settings)
{
    public async Task<ReadableRecords> ReadAsync(
        ulong readerGuildId,
        Guid warId,
        CancellationToken cancellationToken
    )
    {
        var reader = (long)readerGuildId;
        List<long> guilds =
        [
            reader,
            .. (await settings.GetSharingGuildsAsync(readerGuildId, cancellationToken)).Select(g =>
                (long)g
            ),
        ];

        var stockpiles = (
            await db
                .Stockpiles.AsNoTracking()
                .Where(s => s.WarId == warId && guilds.Contains(s.GuildId))
                .ToListAsync(cancellationToken)
        )
            .GroupBy(s => s.StockpileId)
            .Select(g =>
                g.OrderByDescending(s => s.LastSeenAt).ThenBy(s => s.GuildId != reader).First()
            )
            .ToList();
        var chosen = stockpiles.Select(s => (s.GuildId, s.StockpileId)).ToHashSet();
        var stockpileIds = stockpiles.Select(s => s.StockpileId).ToList();
        var entries = (
            await db
                .StockpileEntries.AsNoTracking()
                .Where(e =>
                    e.WarId == warId
                    && guilds.Contains(e.GuildId)
                    && stockpileIds.Contains(e.StockpileId)
                )
                .ToListAsync(cancellationToken)
        )
            .Where(e => chosen.Contains((e.GuildId, e.StockpileId)))
            .ToLookup(e => e.StockpileId);

        var structures = (
            await db
                .Structures.AsNoTracking()
                .Where(s => s.WarId == warId && guilds.Contains(s.GuildId))
                .ToListAsync(cancellationToken)
        )
            .GroupBy(s => s.StructureId)
            .Select(g =>
                g.OrderBy(s => s.GuildId != reader).ThenByDescending(s => s.LastSeenAt).First()
            )
            .ToDictionary(
                s => s.StructureId,
                s => new StructureRecord(
                    s.StructureId,
                    s.GuildId,
                    s.CodeName,
                    s.Region,
                    s.X,
                    s.Y,
                    s.Label,
                    s.GeneratedLabel
                )
            );

        var regions = structures.Values.Select(s => s.Region).Distinct().ToList();
        var places = (
            await db
                .MapPlaces.AsNoTracking()
                .Where(p => p.WarId == warId && regions.Contains(p.Region))
                .ToListAsync(cancellationToken)
        )
            .GroupBy(p => p.Region)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<MapPlace>)[.. g]);

        return new ReadableRecords(
            structures,
            [
                .. stockpiles.Select(s => new StockpileRecord(
                    s.StockpileId,
                    s.StructureId,
                    s.GuildId,
                    s.Name,
                    s.LastSeenAt,
                    [
                        .. entries[s.StockpileId]
                            .Select(e => new EntryRecord(e.Category, e.CodeName, e.Quantity)),
                    ]
                )),
            ],
            places
        );
    }
}
