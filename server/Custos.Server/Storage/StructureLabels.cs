using System.Text.RegularExpressions;
using Custos.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Custos.Server.Storage;

/// <summary>The result of a label change.</summary>
public sealed record LabelChange(string? OldLabel, string? NewLabel, string? Refusal)
{
    public bool Changed => Refusal is null;

    public static LabelChange Refused(string reason) => new(null, null, reason);
}

/// <summary>A structure and the label that shows.</summary>
public sealed record LabeledStructure(Guid StructureId, string Label);

/// <summary>
/// The labels of the structures. The label that shows is the label of a user, or the generated
/// label. It is unique in one guild and one war, without regard to case. The Discord command and
/// the MCP tool use <see cref="SetAsync"/>, which holds the rules for a user label.
/// </summary>
public sealed partial class StructureLabels(
    CustosDbContext db,
    GameCatalog catalog,
    ILogger<StructureLabels> logger
)
{
    public const int MaxLength = 64;

    /// <summary>
    /// Generates the label of each structure that has none. The caller holds the advisory lock of
    /// the guild, and saves the changes.
    /// </summary>
    internal async Task GenerateAsync(
        long guildId,
        Guid warId,
        IReadOnlyCollection<Structure> structures,
        CancellationToken cancellationToken
    )
    {
        var pending = structures.Where(s => s.GeneratedLabel is null).ToList();
        if (pending.Count == 0)
            return;

        var taken = new HashSet<string>(
            await db
                .Structures.Where(s => s.GuildId == guildId && s.WarId == warId)
                .Select(s => s.Label ?? s.GeneratedLabel)
                .Where(l => l != null)
                .Select(l => l!)
                .ToListAsync(cancellationToken),
            StringComparer.OrdinalIgnoreCase
        );
        var regions = await GetMapsAsync(warId, pending.Select(s => s.Region), cancellationToken);

        // The order of the IDs makes the result the same for each guild.
        foreach (var structure in pending.OrderBy(s => s.StructureId))
        {
            var map = regions.GetValueOrDefault(structure.Region);
            var place = (
                map is null ? null : LabelGenerator.Place(map.Places, structure.X, structure.Y)
            ) ?? LabelGenerator.RegionName(structure.Region);
            var world =
                map is not null
                && LabelGenerator.IsWorldStructure(map.Items, structure.X, structure.Y);
            var label = LabelGenerator.Generate(
                place,
                catalog.StructureType(structure.CodeName),
                world,
                structure.StructureId,
                taken.Contains
            );
            structure.GeneratedLabel = label;
            taken.Add(label);
        }
    }

    /// <summary>The map data of the regions, for the labels. A region without data is
    /// absent.</summary>
    private async Task<Dictionary<string, RegionMap>> GetMapsAsync(
        Guid warId,
        IEnumerable<string> regions,
        CancellationToken cancellationToken
    )
    {
        var wanted = regions.Distinct().ToList();
        var stored = await db
            .MapRegions.AsNoTracking()
            .Where(r => r.WarId == warId && wanted.Contains(r.Region))
            .Select(r => r.Region)
            .ToListAsync(cancellationToken);
        var places = await db
            .MapPlaces.AsNoTracking()
            .Where(p => p.WarId == warId && stored.Contains(p.Region))
            .ToListAsync(cancellationToken);
        var items = await db
            .MapItems.AsNoTracking()
            .Where(i => i.WarId == warId && stored.Contains(i.Region))
            .ToListAsync(cancellationToken);
        return stored.ToDictionary(
            region => region,
            region => new RegionMap(
                [.. places.Where(p => p.Region == region)],
                [.. items.Where(i => i.Region == region)]
            )
        );
    }

    /// <summary>
    /// Sets the label of a user, or removes it when <paramref name="label"/> is empty. The change
    /// is not a change of the structure: the history and the content hash stay.
    /// </summary>
    public async Task<LabelChange> SetAsync(
        long guildId,
        Guid warId,
        Guid structureId,
        string? label,
        ulong userId,
        string origin,
        CancellationToken cancellationToken
    )
    {
        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (label is not null && Refusal(label) is { } refusal)
            return LabelChange.Refused(refusal);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // The upload of the guild holds the same lock while it generates labels.
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({guildId})",
            cancellationToken
        );

        var structure = await db.Structures.SingleOrDefaultAsync(
            s => s.GuildId == guildId && s.WarId == warId && s.StructureId == structureId,
            cancellationToken
        );
        if (structure is null)
            return LabelChange.Refused("The guild has no record of this structure in this war.");

        var oldLabel = structure.Label ?? structure.GeneratedLabel;
        var shown = label ?? structure.GeneratedLabel;
        if (shown is not null)
        {
            var lower = shown.ToLowerInvariant();
            var taken = await db.Structures.AnyAsync(
                s =>
                    s.GuildId == guildId
                    && s.WarId == warId
                    && s.StructureId != structureId
                    && (s.Label ?? s.GeneratedLabel)!.ToLower() == lower,
                cancellationToken
            );
            if (taken)
                return LabelChange.Refused($"Another structure has the label \"{shown}\".");
        }

        structure.Label = label;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogLabelChanged(logger, guildId, structureId, userId, origin);
        return new LabelChange(oldLabel, shown, null);
    }

    /// <summary>The structures of a guild in a war whose label contains <paramref
    /// name="text"/>.</summary>
    public async Task<IReadOnlyList<LabeledStructure>> SearchAsync(
        long guildId,
        Guid warId,
        string text,
        int limit,
        CancellationToken cancellationToken
    )
    {
        var pattern = $"%{EscapeLike(text.Trim())}%";
        return await db
            .Structures.AsNoTracking()
            .Where(s => s.GuildId == guildId && s.WarId == warId)
            .Select(s => new { s.StructureId, Shown = s.Label ?? s.GeneratedLabel ?? s.CodeName })
            .Where(s => EF.Functions.ILike(s.Shown, pattern, "\\"))
            .OrderBy(s => s.Shown)
            .Take(limit)
            .Select(s => new LabeledStructure(s.StructureId, s.Shown))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The reason to refuse a user label, or <c>null</c>. A label goes into Discord messages and
    /// into the context of an LLM, so it permits letters, digits, spaces, and few signs.
    /// </summary>
    public static string? Refusal(string label)
    {
        if (label.Length > MaxLength)
            return $"A label has {MaxLength} characters or less.";
        if (!Permitted().IsMatch(label))
        {
            return "A label permits letters, digits, spaces, and the signs - ' . , ( ) / & +.";
        }
        return null;
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    [GeneratedRegex(@"^[\p{L}\p{M}\p{N} \-'.,()/&+]+$")]
    private static partial Regex Permitted();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Audit: the label of structure {StructureId} in guild {GuildId} changed by user "
            + "{UserId} through {Origin}"
    )]
    private static partial void LogLabelChanged(
        ILogger logger,
        long guildId,
        Guid structureId,
        ulong userId,
        string origin
    );
}

/// <summary>The place names and the map items of one region.</summary>
internal sealed record RegionMap(IReadOnlyList<MapPlace> Places, IReadOnlyList<MapItem> Items);
