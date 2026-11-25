using System.Text.Json;
using System.Text.Json.Nodes;

namespace Optio.Harness;

/// <summary>
/// The question to the user before a change, and the rules that code applies to it (decisions on
/// #48 of 2026-11-24). The model writes the question in the argument <see cref="TextArgument"/> of
/// a write tool. Code examines it, and else makes the question from the titles and the values.
/// </summary>
internal static class Questions
{
    /// <summary>The argument that Optio adds to each write tool for the question of the
    /// model.</summary>
    public const string TextArgument = "confirm_text";

    /// <summary>The limit of the question of the model.</summary>
    public const int MaxModelText = 120;

    /// <summary>A text value of this length or less must be in the question. The text of code
    /// cuts a longer value to this length.</summary>
    public const int MaxShownValue = 40;

    private const string TextDescription =
        "The question to the user before the change. It shows on a screen 50 characters wide: "
        + "leave out each word that is not necessary. Use the form '{Verb} {Object}? "
        + "\"value\"', with 'and \"value\"' for each other value. Copy each value exactly, "
        + "with its case. For a new value, give the new value. For a removal, give the current "
        + "value from a tool result. Do not show an ID. Do not show a long text: name the action "
        + "only. Examples: 'Set Label? \"Main Depot\"', 'Set Labels? \"Main Depot\" and "
        + "\"North Bunker\"', 'Remove Label? \"Old Depot\"', 'Post Progress Log?'. When you call "
        + "more than one write tool in one turn, give one question for all the changes in the "
        + "first call.";

    /// <summary>The JSON Schema of a write tool with the required argument
    /// <see cref="TextArgument"/>.</summary>
    public static JsonElement WithTextArgument(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        if (node["properties"] is not JsonObject properties)
            node["properties"] = properties = [];
        properties[TextArgument] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = TextDescription,
        };
        if (node["required"] is not JsonArray required)
            node["required"] = required = [];
        required.Add(TextArgument);
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>
    /// The question. It is the question of the model when it is one line of
    /// <see cref="MaxModelText"/> characters at most, and holds each text value of the calls of
    /// <see cref="MaxShownValue"/> characters or less, in any case. Each quoted text in it must
    /// also be a value of the calls or a text in <paramref name="known"/>, the texts of the tool
    /// results of the query: for example the current label of a removal. A quoted text that ends
    /// in … must start such a text. Code puts the source of each quoted text in the question. Else
    /// code makes the question from the title of each tool and the values.
    /// </summary>
    public static string Text(
        string? modelText,
        IReadOnlyList<WriteCall> calls,
        IReadOnlySet<string> known
    )
    {
        var values = calls.SelectMany(c => c.Shown).OfType<string>().Where(v => v.Length > 0);
        HashSet<string> sources = new(known, StringComparer.Ordinal);
        sources.UnionWith(values);
        if (
            modelText is { Length: > 0 and <= MaxModelText }
            && !modelText.Any(char.IsControl)
            && values
                .Where(v => v.Length <= MaxShownValue)
                .All(v => modelText.Contains(v, StringComparison.OrdinalIgnoreCase))
            && WithSources(modelText, sources) is { } question
        )
        {
            return question;
        }

        return string.Join(
            " ",
            calls
                .GroupBy(c => c.Title)
                .Select(g =>
                {
                    var shown = g.SelectMany(c => c.Shown)
                        .Select(v => string.IsNullOrEmpty(v) ? "(none)" : $"\"{Cut(OneLine(v))}\"")
                        .ToList();
                    return shown.Count == 0
                        ? $"{OneLine(g.Key)}?"
                        : $"{OneLine(g.Key)}? {string.Join(" and ", shown)}";
                })
        );
    }

    /// <summary>The texts of tool results: each string of a JSON result, else the
    /// result.</summary>
    public static HashSet<string> Strings(IEnumerable<string> results)
    {
        HashSet<string> strings = new(StringComparer.Ordinal);
        foreach (var result in results)
        {
            try
            {
                using var document = JsonDocument.Parse(result);
                Add(document.RootElement);
            }
            catch (JsonException)
            {
                strings.Add(result);
            }
        }
        return strings;

        void Add(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    strings.Add(element.GetString()!);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Add(item);
                    break;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                        Add(property.Value);
                    break;
            }
        }
    }

    /// <summary>
    /// The question with the source of each quoted text, or null when a quoted text has no
    /// source. The source is a known text with the same characters in any case, or for a quoted
    /// text that ends in …, a known text that starts with it. The user then sees the case of the
    /// source.
    /// </summary>
    private static string? WithSources(string question, HashSet<string> known)
    {
        var parts = question.Split('"');
        for (var i = 1; i < parts.Length; i += 2)
        {
            var quoted = parts[i];
            var cut = quoted.EndsWith('…') && quoted.Length > 1;
            // A source with the same case comes first.
            var source = new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase }
                .Select(comparison =>
                    known.FirstOrDefault(k =>
                        cut ? k.StartsWith(quoted[..^1], comparison) : k.Equals(quoted, comparison)
                    )
                )
                .FirstOrDefault(k => k is not null);
            if (source is null)
                return null;
            parts[i] = cut ? source[..(quoted.Length - 1)] + "…" : source;
        }
        return string.Join('"', parts);
    }

    private static string Cut(string value) =>
        value.Length <= MaxShownValue ? value : value[..(MaxShownValue - 1)] + "…";

    private static string OneLine(string text) =>
        string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c));
}
