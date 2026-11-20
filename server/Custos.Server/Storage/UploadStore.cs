using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Custos.Server.Endpoints;
using Custos.Server.MapData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Custos.Server.Storage;

/// <summary>
/// Writes an upload and the observations of its pinned items. The history keeps a change, not a
/// sighting: if the content of an entity is equal to its current state, the store only moves
/// <c>last_seen_at</c>.
/// </summary>
internal sealed class UploadStore(
    CustosDbContext db,
    StructureLabels labels,
    IOptions<CustosOptions> options
)
{
    private enum Observation
    {
        Stale,
        Seen,
        Changed,
    }

    /// <summary>
    /// Saves the upload and its observations, and gives the stockpiles for the upload result. An
    /// item from before <paramref name="warStartedAt"/> belongs to an earlier war, and the store
    /// ignores it.
    /// </summary>
    public async Task<StockpileSummary[]> SaveAsync(
        Upload upload,
        IReadOnlyList<PinnedItem> items,
        DateTimeOffset warStartedAt,
        CancellationToken cancellationToken
    )
    {
        var (guildId, warId) = (upload.GuildId, upload.WarId);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // One upload of a guild at a time, so that two uploads cannot race on the current state.
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({guildId})",
            cancellationToken
        );

        db.Uploads.Add(upload);

        // If two items have the same key, the latest item applies.
        var observed = items
            .Where(item => item.LastUpdated >= warStartedAt)
            .Select(item =>
                (
                    Id: EntityIds.Structure(
                        options.Value.Shard,
                        warId,
                        item.Region,
                        item.X,
                        item.Y
                    ),
                    Item: item
                )
            )
            .GroupBy(o => o.Id)
            .Select(g => g.MaxBy(o => o.Item.LastUpdated))
            .ToList();

        var structureIds = observed.Select(o => o.Id).ToList();
        var structures = await db
            .Structures.Where(s =>
                s.GuildId == guildId && s.WarId == warId && structureIds.Contains(s.StructureId)
            )
            .ToDictionaryAsync(s => s.StructureId, cancellationToken);
        var stockpiles = await db
            .Stockpiles.Where(s =>
                s.GuildId == guildId && s.WarId == warId && structureIds.Contains(s.StructureId)
            )
            .ToDictionaryAsync(s => s.StockpileId, cancellationToken);

        List<StockpileSummary> summaries = [];
        foreach (var (structureId, item) in observed)
        {
            var seenAt = Microseconds(item.LastUpdated);
            await ObserveStructureAsync(
                upload,
                structures.GetValueOrDefault(structureId),
                structureId,
                item,
                seenAt,
                cancellationToken
            );

            foreach (var content in item.Stockpiles)
            {
                var stockpileId = EntityIds.Stockpile(structureId, content.Name);
                var stockpile = stockpiles.GetValueOrDefault(stockpileId);

                // Each structure has a public stockpile, but most are empty. Keep one only when it
                // has entries.
                if (stockpile is null && content.Name is null && content.Entries.Count == 0)
                    continue;

                await ObserveStockpileAsync(
                    upload,
                    stockpile,
                    stockpileId,
                    structureId,
                    content,
                    seenAt,
                    cancellationToken
                );
                summaries.Add(
                    new StockpileSummary(
                        content.Name ?? "Public",
                        $"{item.CodeName}, {item.Region}",
                        content.Entries.Count
                    )
                );
            }
        }

        // Each new structure gets its generated label under the lock of the guild.
        var created = db
            .ChangeTracker.Entries<Structure>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .ToList();
        await labels.GenerateAsync(guildId, warId, created, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return [.. summaries];
    }

    private async Task ObserveStructureAsync(
        Upload upload,
        Structure? structure,
        Guid structureId,
        PinnedItem item,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken
    )
    {
        var details = item.Details.ToJsonString();
        var hash = Hash($"{item.CodeName}\n{details}");

        switch (Compare(structure?.LastSeenAt, structure?.ContentHash, seenAt, hash))
        {
            case Observation.Stale:
                return;
            case Observation.Seen:
                structure!.LastSeenAt = seenAt;
                await db
                    .StructureChanges.Where(c =>
                        c.GuildId == upload.GuildId
                        && c.WarId == upload.WarId
                        && c.StructureId == structureId
                        && c.ObservedAt == structure.ObservedAt
                    )
                    .ExecuteUpdateAsync(
                        c => c.SetProperty(x => x.LastSeenAt, seenAt),
                        cancellationToken
                    );
                return;
        }

        if (structure is null)
        {
            db.Structures.Add(
                new Structure
                {
                    GuildId = upload.GuildId,
                    WarId = upload.WarId,
                    StructureId = structureId,
                    Region = item.Region,
                    X = item.X,
                    Y = item.Y,
                    CodeName = item.CodeName,
                    ObservedAt = seenAt,
                    LastSeenAt = seenAt,
                    ContentHash = hash,
                    Details = details,
                }
            );
        }
        else
        {
            structure.CodeName = item.CodeName;
            structure.ObservedAt = structure.LastSeenAt = seenAt;
            structure.ContentHash = hash;
            structure.Details = details;
        }

        db.StructureChanges.Add(
            new StructureChange
            {
                GuildId = upload.GuildId,
                WarId = upload.WarId,
                StructureId = structureId,
                ObservedAt = seenAt,
                LastSeenAt = seenAt,
                UploadId = upload.UploadId,
                CodeName = item.CodeName,
                Details = details,
            }
        );
    }

    private async Task ObserveStockpileAsync(
        Upload upload,
        Stockpile? stockpile,
        Guid stockpileId,
        Guid structureId,
        StockpileContent content,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken
    )
    {
        var entries = EntriesJson(content.Entries);
        var hash = Hash(entries);

        switch (Compare(stockpile?.LastSeenAt, stockpile?.ContentHash, seenAt, hash))
        {
            case Observation.Stale:
                return;
            case Observation.Seen:
                stockpile!.LastSeenAt = seenAt;
                await db
                    .StockpileChanges.Where(c =>
                        c.GuildId == upload.GuildId
                        && c.WarId == upload.WarId
                        && c.StockpileId == stockpileId
                        && c.ObservedAt == stockpile.ObservedAt
                    )
                    .ExecuteUpdateAsync(
                        c => c.SetProperty(x => x.LastSeenAt, seenAt),
                        cancellationToken
                    );
                return;
        }

        if (stockpile is null)
        {
            db.Stockpiles.Add(
                new Stockpile
                {
                    GuildId = upload.GuildId,
                    WarId = upload.WarId,
                    StockpileId = stockpileId,
                    StructureId = structureId,
                    Name = content.Name,
                    ObservedAt = seenAt,
                    LastSeenAt = seenAt,
                    ContentHash = hash,
                }
            );
        }
        else
        {
            stockpile.ObservedAt = stockpile.LastSeenAt = seenAt;
            stockpile.ContentHash = hash;
            await db
                .StockpileEntries.Where(e =>
                    e.GuildId == upload.GuildId
                    && e.WarId == upload.WarId
                    && e.StockpileId == stockpileId
                )
                .ExecuteDeleteAsync(cancellationToken);
        }

        db.StockpileEntries.AddRange(
            content.Entries.Select(e => new StockpileEntry
            {
                GuildId = upload.GuildId,
                WarId = upload.WarId,
                StockpileId = stockpileId,
                Category = e.Category,
                CodeName = e.CodeName,
                Quantity = e.Quantity,
            })
        );
        db.StockpileChanges.Add(
            new StockpileChange
            {
                GuildId = upload.GuildId,
                WarId = upload.WarId,
                StockpileId = stockpileId,
                ObservedAt = seenAt,
                LastSeenAt = seenAt,
                UploadId = upload.UploadId,
                Entries = entries,
            }
        );
    }

    /// <summary>
    /// An item that is not newer than the last sighting is stale: another upload already gave the
    /// same or newer data. The history does not get older states that arrive late.
    /// </summary>
    private static Observation Compare(
        DateTimeOffset? lastSeenAt,
        byte[]? currentHash,
        DateTimeOffset seenAt,
        byte[] hash
    ) =>
        lastSeenAt is null ? Observation.Changed
        : seenAt <= lastSeenAt ? Observation.Stale
        : hash.AsSpan().SequenceEqual(currentHash) ? Observation.Seen
        : Observation.Changed;

    /// <summary>The entries by category and code name, in ordinal order, for example
    /// <c>{"item_crate":{"RifleC":20}}</c>.</summary>
    private static string EntriesJson(IEnumerable<StockpileEntryContent> entries)
    {
        var json = new JsonObject();
        foreach (
            var category in entries
                .GroupBy(e => e.Category)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
        )
        {
            var codes = new JsonObject();
            foreach (var entry in category.OrderBy(e => e.CodeName, StringComparer.Ordinal))
                codes[entry.CodeName] = entry.Quantity;
            json[category.Key] = codes;
        }

        return json.ToJsonString();
    }

    private static byte[] Hash(string content) => SHA256.HashData(Encoding.UTF8.GetBytes(content));

    /// <summary>PostgreSQL keeps microseconds. The file has ticks of 100 ns.</summary>
    private static DateTimeOffset Microseconds(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % 10));
}
