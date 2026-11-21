using System.Text.Json;
using Custos.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Custos.Server.Discord;

/// <summary>Reads and writes the settings documents of the guilds.</summary>
public sealed partial class GuildSettingsStore(
    CustosDbContext db,
    TimeProvider time,
    ILogger<GuildSettingsStore> logger
)
{
    /// <summary>The settings of a guild, or the default document for a guild without a
    /// row.</summary>
    public async Task<GuildSettings> GetAsync(ulong guildId, CancellationToken cancellationToken)
    {
        var row = await db
            .GuildSettings.AsNoTracking()
            .SingleOrDefaultAsync(s => s.GuildId == (long)guildId, cancellationToken);
        return row is null
            ? GuildSettings.Default
            : JsonSerializer.Deserialize<GuildSettings>(row.Document, GuildSettings.Json)
                ?? GuildSettings.Default;
    }

    /// <summary>Replaces the settings of a guild, and writes the audit line.</summary>
    public async Task SaveAsync(
        ulong guildId,
        GuildSettings settings,
        ulong userId,
        string origin,
        CancellationToken cancellationToken
    )
    {
        var document = settings.ToJson();
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
                    Document = document,
                    UpdatedAt = now,
                    UpdatedBy = (long)userId,
                }
            );
        }
        else
        {
            row.Document = document;
            row.UpdatedAt = now;
            row.UpdatedBy = (long)userId;
        }

        await db.SaveChangesAsync(cancellationToken);
        LogSettingsChanged(logger, guildId, userId, origin);
    }

    /// <summary>The guilds that list <paramref name="readerGuildId"/> in
    /// <c>Sharing.ReadGuildIds</c>.</summary>
    public async Task<IReadOnlyList<ulong>> GetSharingGuildsAsync(
        ulong readerGuildId,
        CancellationToken cancellationToken
    )
    {
        var filter = JsonSerializer.Serialize(
            new { Sharing = new { ReadGuildIds = new[] { readerGuildId.ToString() } } }
        );
        var guilds = await db
            .GuildSettings.FromSql(
                $"SELECT * FROM guild_settings WHERE document @> {filter}::jsonb"
            )
            .Select(s => s.GuildId)
            .ToListAsync(cancellationToken);
        return [.. guilds.Select(g => (ulong)g).Order()];
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Audit: the settings of guild {GuildId} changed by user {UserId} through {Origin}"
    )]
    private static partial void LogSettingsChanged(
        ILogger logger,
        ulong guildId,
        ulong userId,
        string origin
    );
}
