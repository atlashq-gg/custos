using System.Globalization;
using Custos.Server.Catalog;
using Custos.Server.Discord;
using Custos.Server.Storage;
using Custos.Server.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Custos.Server.Events;

/// <summary>A stockpile that an upload changed. A new stockpile has no
/// <paramref name="Before"/>.</summary>
internal sealed record StockpileDiff(
    Guid StockpileId,
    Guid StructureId,
    string? Name,
    IReadOnlyDictionary<EntryKey, int>? Before,
    IReadOnlyDictionary<EntryKey, int> After,
    DateTimeOffset ObservedAt
);

/// <summary>The data of an event of the type <c>stockpile.changed</c>.</summary>
internal sealed record StockpileChangedData(
    EventPolicy Policy,
    EventMessage Message,
    string GuildId,
    string OriginGuild,
    string StockpileId,
    string StockpileName,
    string StructureId,
    string StructureLabel,
    int War,
    DateTimeOffset ObservedAt
) : EventData(Policy, Message);

/// <summary>
/// Makes the events of the stockpile changes of an upload, after its commit. Each guild that can
/// read the records of the upload gets its own event: the guild of the upload, and each guild in
/// its <c>Sharing.ReadGuildIds</c>. The policy of an event holds the read roles of that guild, and
/// the text holds the labels that the guild sees.
/// </summary>
internal sealed partial class StockpileEvents(
    CustosDbContext db,
    GuildSettingsStore settings,
    GameCatalog catalog,
    EventPublisher publisher,
    ServerMetrics metrics,
    ILogger<StockpileEvents> logger
)
{
    public const string Type = "stockpile.changed";

    /// <summary>Publishes the events. A failure does not fail the upload, which is already
    /// committed. The publisher counts it.</summary>
    public async Task PublishAsync(
        Upload upload,
        IReadOnlyList<StockpileDiff> diffs,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await PublishCoreAsync(upload, diffs, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            metrics.Event(EventOutcome.Failed);
            LogFailed(logger, e, upload.UploadId);
        }
    }

    private async Task PublishCoreAsync(
        Upload upload,
        IReadOnlyList<StockpileDiff> diffs,
        CancellationToken cancellationToken
    )
    {
        var origin = upload.GuildId;
        var originSettings = await settings.GetAsync((ulong)origin, cancellationToken);
        var readers = new Dictionary<long, GuildSettings> { [origin] = originSettings };
        foreach (var guild in originSettings.Sharing.ReadGuildIds)
            readers[(long)guild] = await settings.GetAsync(guild, cancellationToken);
        var guilds = readers.Keys.ToList();
        var structureIds = diffs.Select(d => d.StructureId).Distinct().ToList();

        // The label of the reader guild when it has its own record, else the label of the guild of
        // the upload, as in GuildRecords.
        var labels = await db
            .Structures.AsNoTracking()
            .Where(s =>
                guilds.Contains(s.GuildId)
                && s.WarId == upload.WarId
                && structureIds.Contains(s.StructureId)
            )
            .ToDictionaryAsync(
                s => (s.GuildId, s.StructureId),
                s => s.Label ?? s.GeneratedLabel ?? s.CodeName,
                cancellationToken
            );
        var war = await db
            .Wars.Where(w => w.WarId == upload.WarId)
            .Select(w => w.WarNumber)
            .SingleAsync(cancellationToken);

        foreach (var (reader, readerSettings) in readers)
        {
            var policy = new EventPolicy(
                Id(reader),
                [.. readerSettings.Access.ReadRoleIds.Select(r => Id(r))]
            );
            foreach (var diff in diffs)
            {
                var label = labels.GetValueOrDefault((reader, diff.StructureId))
                    ?? labels[(origin, diff.StructureId)];
                var name = diff.Name ?? "Public";
                if (ChangeText.Make(label, name, diff.Before, diff.After, catalog) is not { } text)
                    continue;

                publisher.Publish(
                    Type,
                    upload.ReceivedAt,
                    new StockpileChangedData(
                        policy,
                        new EventMessage(text, $"stockpile:{diff.StockpileId}"),
                        Id(reader),
                        Id(origin),
                        diff.StockpileId.ToString(),
                        name,
                        diff.StructureId.ToString(),
                        label,
                        war,
                        diff.ObservedAt
                    )
                );
            }
        }
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static string Id(ulong id) => id.ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The events of the upload {UploadId} were not published"
    )]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid uploadId);
}
