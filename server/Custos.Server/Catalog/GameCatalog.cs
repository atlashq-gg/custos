using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Custos.Server.Catalog;

/// <summary>
/// One code name of the game, with the names that a person reads. <paramref name="CrateSize"/> is
/// the number in one crate: for an item, in an item crate, and for a vehicle or a structure, in a
/// shippable crate. It is null when the catalog does not have the number.
/// </summary>
public sealed record CatalogEntry(
    string CodeName,
    string DisplayName,
    CatalogKind Kind,
    string Category,
    string Faction,
    int? CrateSize = null
);

[JsonConverter(typeof(JsonStringEnumConverter<CatalogKind>))]
public enum CatalogKind
{
    Item,
    Vehicle,
    Structure,
}

/// <summary>The catalog file: its source, and its entries.</summary>
public sealed record CatalogFile(string Source, string RetrievedAt, CatalogEntry[] Entries)
{
    /// <summary>The JSON of the file: snake_case members, and the kind in lower case.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<CatalogKind>(JsonNamingPolicy.SnakeCaseLower) },
    };
}

/// <summary>
/// The display name and the category of each code name, from the catalog file. A code name
/// without an entry still works: its display name and its category are the code name.
/// </summary>
public sealed partial class GameCatalog
{
    private readonly Dictionary<string, CatalogEntry> _entries;

    public GameCatalog(IEnumerable<CatalogEntry> entries)
    {
        _entries = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            _entries.TryAdd(entry.CodeName, entry);
    }

    public static readonly GameCatalog Empty = new([]);

    public int Count => _entries.Count;

    public CatalogEntry? Find(string codeName) => _entries.GetValueOrDefault(codeName);

    public string DisplayName(string codeName) => Find(codeName)?.DisplayName ?? codeName;

    public string Category(string codeName) => Find(codeName)?.Category ?? codeName;

    /// <summary>
    /// The type of a structure in a generated label: the display name without the tier, for
    /// example "Bunker Base" for "Bunker Base (Tier 2)". Without an entry, the code name.
    /// </summary>
    public string StructureType(string codeName) =>
        Find(codeName) is { } entry ? Tier().Replace(entry.DisplayName, "").Trim() : codeName;

    /// <summary>
    /// The code names whose code name or display name contain <paramref name="filter"/>, without
    /// regard to case. A code name is a match too when the catalog does not have it.
    /// </summary>
    public IReadOnlyList<string> Match(string filter) =>
        [
            .. _entries
                .Values.Where(e =>
                    e.CodeName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || e.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                )
                .Select(e => e.CodeName),
        ];

    /// <summary>Reads a catalog file. A missing file gives the empty catalog.</summary>
    public static GameCatalog Load(string path)
    {
        if (!File.Exists(path))
            return Empty;
        using var stream = File.OpenRead(path);
        var file = JsonSerializer.Deserialize<CatalogFile>(stream, CatalogFile.Json)
            ?? throw new JsonException($"The catalog file {path} is empty.");
        return new GameCatalog(file.Entries);
    }

    [GeneratedRegex(@"\s*\(Tier \d+\)$")]
    private static partial Regex Tier();
}

/// <summary>Registers the catalog as a singleton.</summary>
internal static partial class CatalogSetup
{
    public static IHostApplicationBuilder AddCustosCatalog(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton(services =>
        {
            var path = services.GetRequiredService<IOptions<CustosOptions>>().Value.CatalogPath;
            var resolved = Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppContext.BaseDirectory, path);
            var catalog = GameCatalog.Load(resolved);
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Catalog");
            if (catalog.Count == 0)
                LogNoCatalog(logger, resolved);
            else
                LogCatalog(logger, catalog.Count, resolved);
            return catalog;
        });
        return builder;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No catalog at {Path}. Each name and each category is the code name."
    )]
    private static partial void LogNoCatalog(ILogger logger, string path);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "The catalog has {Count} entries ({Path})"
    )]
    private static partial void LogCatalog(ILogger logger, int count, string path);
}
