using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Custos.Server.Storage;

/// <summary>
/// Records each war that the server sees. For a new war, it adds the row, creates the partitions of the
/// history tables, and ends the earlier wars of the shard: the war API reports a new war only when the
/// earlier war ended.
/// </summary>
internal sealed class WarRegistry(CustosDbContext db, IMemoryCache cache, IOptions<CustosOptions> options, TimeProvider time)
{
    /// <summary>The advisory lock of this registry. The locks of guilds use the guild ID, which is positive.</summary>
    public const long LockKey = -1;

    /// <summary>Records the war, and gives the start of the war as the database has it.</summary>
    public async Task<DateTimeOffset> RecordAsync(CurrentWar war, CancellationToken cancellationToken)
    {
        var cacheKey = (nameof(WarRegistry), war.WarId);
        if (cache.TryGetValue(cacheKey, out DateTimeOffset startedAt)) return startedAt;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockKey})", cancellationToken);

        var row = await db.Wars.FindAsync([war.WarId], cancellationToken);
        if (row is null)
        {
            var now = time.GetUtcNow();
            var shard = options.Value.Shard;
            await db.Wars
                .Where(w => w.Shard == shard && w.EndedAt == null)
                .ExecuteUpdateAsync(w => w.SetProperty(x => x.EndedAt, now), cancellationToken);

            row = new War { WarId = war.WarId, Shard = shard, WarNumber = war.WarNumber, StartedAt = war.StartedAt ?? now };
            db.Wars.Add(row);
            await db.SaveChangesAsync(cancellationToken);

            foreach (var table in HistoryTables())
            {
                // Identifiers cannot be parameters. The values are an integer and a Guid, not input text.
#pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync(
                    $"CREATE TABLE IF NOT EXISTS {PartitionName(table, war.WarNumber)} PARTITION OF {table} FOR VALUES IN ('{war.WarId:D}')",
                    cancellationToken);
#pragma warning restore EF1002
            }
        }

        await transaction.CommitAsync(cancellationToken);
        cache.Set(cacheKey, row.StartedAt);
        return row.StartedAt;
    }

    /// <summary>
    /// The partition of one war, for example <c>stockpile_changes_w142</c>. War numbers repeat across shards, but
    /// the server supports one shard.
    /// </summary>
    private static string PartitionName(string table, int warNumber) =>
        string.Create(CultureInfo.InvariantCulture, $"{table}_w{warNumber}");

    private IEnumerable<string> HistoryTables() =>
        new[] { typeof(StockpileChange), typeof(StructureChange) }
            .Select(type => db.Model.FindEntityType(type)!.GetTableName()!);
}
