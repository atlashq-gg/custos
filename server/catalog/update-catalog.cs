#:property PublishAot=false

// Makes catalog.json from the Cargo tables of the Foxhole wiki. Run it from this folder after a
// game update:
//
//   dotnet run update-catalog.cs -- catalog.json
//
// The queries are in this file, so that each run gives the same shape. The wiki content is under
// CC BY-NC-SA 4.0 (README.md).

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

const string Api = "https://foxhole.wiki.gg/api.php";
const string Source = "https://foxhole.wiki.gg/wiki/Special:CargoTables";

var output = args.Length > 0 ? args[0] : "catalog.json";
using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("custos-catalog/1.0");

// Table, fields, the kind, and the field with the category.
(string Table, string Category, string Kind)[] tables =
[
    ("itemdata", "ItemCategory", "item"),
    ("vehicles", "type", "vehicle"),
    ("structures", "type", "structure"),
];

var entries = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
foreach (var (table, category, kind) in tables)
{
    foreach (var row in await QueryAsync(table, $"codename,name,{category},faction"))
    {
        var codeName = row.GetValueOrDefault("codename")?.Trim();
        var name = row.GetValueOrDefault("name")?.Trim();
        if (string.IsNullOrEmpty(codeName) || string.IsNullOrEmpty(name))
            continue;

        var value = row.GetValueOrDefault(category)?.Trim();
        // The first table with a code name wins: an item before a vehicle before a structure.
        entries.TryAdd(
            codeName,
            new Entry(
                codeName,
                System.Net.WebUtility.HtmlDecode(name),
                kind,
                string.IsNullOrEmpty(value) ? codeName : value,
                Faction(row.GetValueOrDefault("faction"))
            )
        );
    }
}

var file = new CatalogFile(Source, DateTime.UtcNow.ToString("yyyy-MM-dd"), [.. entries.Values]);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(file, Json.Options) + "\n");
Console.WriteLine($"{entries.Count} entries in {output}");

// The Cargo API gives at most 500 rows for each request.
async Task<List<Dictionary<string, string?>>> QueryAsync(string table, string fields)
{
    var rows = new List<Dictionary<string, string?>>();
    for (var offset = 0; ; offset += 500)
    {
        var url =
            $"{Api}?action=cargoquery&format=json&tables={table}&fields={fields}"
            + $"&limit=500&offset={offset}";
        var page = await http.GetFromJsonAsync<CargoPage>(url)
            ?? throw new InvalidOperationException($"No answer for {table}.");
        rows.AddRange(page.CargoQuery.Select(r => r.Title));
        if (page.CargoQuery.Length < 500)
            return rows;
    }
}

static string Faction(string? wiki) =>
    wiki?.Trim() switch
    {
        "Col" => "colonial",
        "War" => "warden",
        "Both" => "both",
        null or "" => "both",
        var other => other.ToLowerInvariant(),
    };

record Entry(string CodeName, string DisplayName, string Kind, string Category, string Faction);

record CatalogFile(string Source, string RetrievedAt, Entry[] Entries);

record CargoRow(Dictionary<string, string?> Title);

record CargoPage([property: JsonPropertyName("cargoquery")] CargoRow[] CargoQuery);

static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
