using Microsoft.EntityFrameworkCore;

namespace Custos.Server.Storage;

/// <summary>
/// Generates the labels of the structures of 0.1.0 when the server starts. It starts after <see
/// cref="DatabaseMigrator"/>, and takes the lock of each guild, as an upload does.
/// </summary>
internal sealed partial class LabelBackfill(
    IServiceScopeFactory scopes,
    ILogger<LabelBackfill> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await RunAsync(scope.ServiceProvider, stoppingToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogBackfillFailed(logger, e);
        }
    }

    internal static async Task<int> RunAsync(IServiceProvider services, CancellationToken token)
    {
        var db = services.GetRequiredService<CustosDbContext>();
        var labels = services.GetRequiredService<StructureLabels>();
        var groups = await db
            .Structures.Where(s => s.GeneratedLabel == null)
            .Select(s => new { s.GuildId, s.WarId })
            .Distinct()
            .ToListAsync(token);

        var count = 0;
        foreach (var group in groups)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await db.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock({group.GuildId})",
                token
            );
            var structures = await db
                .Structures.Where(s =>
                    s.GuildId == group.GuildId && s.WarId == group.WarId && s.GeneratedLabel == null
                )
                .ToListAsync(token);
            await labels.GenerateAsync(group.GuildId, group.WarId, structures, token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            db.ChangeTracker.Clear();
            count += structures.Count;
        }
        return count;
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "The server could not generate the labels of the earlier structures."
    )]
    private static partial void LogBackfillFailed(ILogger logger, Exception exception);
}
