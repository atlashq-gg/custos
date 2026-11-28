using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Optio.Storage;

/// <summary>Reads and writes the settings of the guilds.</summary>
public sealed partial class GuildSettingsStore(
    OptioDbContext db,
    TimeProvider time,
    ILogger<GuildSettingsStore> logger
)
{
    /// <summary>The tries of a change when another change of the guild is first.</summary>
    private const int Attempts = 5;

    /// <summary>The settings of a guild, or the default for a guild without a row.</summary>
    public async Task<GuildSettings> GetAsync(ulong guildId, CancellationToken cancellationToken)
    {
        var row = await db
            .GuildSettings.AsNoTracking()
            .SingleOrDefaultAsync(s => s.GuildId == (long)guildId, cancellationToken);
        return Read(row);
    }

    /// <summary>
    /// Changes the settings of a guild with <paramref name="change"/>, examines the result, saves
    /// it, and writes the audit line. When another change of the guild is first, it applies the
    /// change again to the new settings. A refused result changes nothing.
    /// </summary>
    public async Task<GuildSettingsResult> UpdateAsync(
        ulong guildId,
        Func<GuildSettings, GuildSettings> change,
        ulong userId,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var row = await db.GuildSettings.SingleOrDefaultAsync(
                s => s.GuildId == (long)guildId,
                cancellationToken
            );
            var result = change(Read(row)).Check();
            if (result.Settings is not { } settings)
                return result;

            var now = time.GetUtcNow();
            if (row is null)
            {
                row = new GuildSettingsRow
                {
                    GuildId = (long)guildId,
                    Document = settings.ToJson(),
                    UpdatedAt = now,
                    UpdatedBy = (long)userId,
                };
                db.GuildSettings.Add(row);
            }
            else
            {
                row.Document = settings.ToJson();
                row.UpdatedAt = now;
                row.UpdatedBy = (long)userId;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                LogSettingsChanged(logger, guildId, userId);
                return result;
            }
            catch (DbUpdateException) when (attempt < Attempts)
            {
                // Another change was first: a new version of the row, or a new row.
            }
        }
    }

    private static GuildSettings Read(GuildSettingsRow? row) =>
        row is null
            ? GuildSettings.Default
            : JsonSerializer.Deserialize<GuildSettings>(row.Document, GuildSettings.Json)!;

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Audit: the Optio settings of guild {GuildId} changed by user {UserId}"
    )]
    private static partial void LogSettingsChanged(ILogger logger, ulong guildId, ulong userId);
}

/// <summary>
/// Applies the migrations when Optio starts, before the other hosted services start. EF Core
/// locks the migration history, so two instances can start at the same time.
/// </summary>
internal sealed class DatabaseMigrator(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<OptioDbContext>()
            .Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
