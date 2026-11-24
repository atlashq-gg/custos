using Microsoft.EntityFrameworkCore;

namespace Optio.Storage;

/// <summary>The settings of optio for a guild, as <c>/optioadmin settings</c> shows them.</summary>
public sealed record GuildSettings(string? Prompt)
{
    /// <summary>The longest prompt of a guild.</summary>
    public const int MaxPromptLength = 1000;

    public static readonly GuildSettings Default = new((string?)null);
}

/// <summary>Reads and writes the settings of the guilds.</summary>
public sealed partial class GuildSettingsStore(
    OptioDbContext db,
    TimeProvider time,
    ILogger<GuildSettingsStore> logger
)
{
    /// <summary>The settings of a guild, or the default for a guild without a row.</summary>
    public async Task<GuildSettings> GetAsync(ulong guildId, CancellationToken cancellationToken)
    {
        var row = await db
            .GuildSettings.AsNoTracking()
            .SingleOrDefaultAsync(s => s.GuildId == (long)guildId, cancellationToken);
        return row is null ? GuildSettings.Default : new GuildSettings(row.Prompt);
    }

    /// <summary>Replaces the settings of a guild, and writes the audit line. A prompt of only
    /// white space is no prompt.</summary>
    public async Task SaveAsync(
        ulong guildId,
        GuildSettings settings,
        ulong userId,
        CancellationToken cancellationToken
    )
    {
        var prompt = string.IsNullOrWhiteSpace(settings.Prompt) ? null : settings.Prompt.Trim();
        if (prompt?.Length > GuildSettings.MaxPromptLength)
        {
            throw new ArgumentException(
                $"The prompt has more than {GuildSettings.MaxPromptLength} characters.",
                nameof(settings)
            );
        }

        var now = time.GetUtcNow();
        var row = await db.GuildSettings.SingleOrDefaultAsync(
            s => s.GuildId == (long)guildId,
            cancellationToken
        );
        if (row is null)
        {
            db.GuildSettings.Add(
                new GuildSettingsRow
                {
                    GuildId = (long)guildId,
                    Prompt = prompt,
                    UpdatedAt = now,
                    UpdatedBy = (long)userId,
                }
            );
        }
        else
        {
            row.Prompt = prompt;
            row.UpdatedAt = now;
            row.UpdatedBy = (long)userId;
        }

        await db.SaveChangesAsync(cancellationToken);
        LogSettingsChanged(logger, guildId, userId);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Audit: the optio settings of guild {GuildId} changed by user {UserId}"
    )]
    private static partial void LogSettingsChanged(ILogger logger, ulong guildId, ulong userId);
}

/// <summary>
/// Applies the migrations when optio starts, before the other hosted services start. EF Core
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
