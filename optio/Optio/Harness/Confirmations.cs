using System.Text.Json;
using System.Text.Json.Nodes;
using Optio.Streams;

namespace Optio.Harness;

/// <summary>The write calls of a query that wait for the answer of the client.</summary>
internal sealed record Confirmation(
    StreamClient Client,
    string Reference,
    IReadOnlyList<WriteCall> Calls,
    DateTimeOffset Expires
);

/// <summary>
/// The open confirmations, in the memory of one instance. A client has one open confirmation at
/// most. A confirmation has the reference of its query, and expires after
/// <see cref="Lifetime"/> (decisions on #48 of 2026-11-24).
/// </summary>
internal sealed class Confirmations(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    /// <summary>The argument that optio adds to each write tool for the question of the model.
    /// </summary>
    public const string TextArgument = "confirm_text";

    /// <summary>The limit of the question of the model.</summary>
    public const int MaxModelText = 120;

    private const string TextDescription =
        "The question to the user before the change. It shows on a screen 50 characters wide: "
        + "leave out each word that is not necessary. Use the form "
        + "'{Verb} {object clause}?', and add 'and {verb} {object clause}' for each other "
        + "change. Hold each value of the change, in quotes. Do not show an ID. Examples: "
        + "'Set label \"Main Depot\"?', 'Remove label?', "
        + "'Set label \"Main Depot\" and set label \"North Bunker\"?'. When you call more than "
        + "one write tool in one turn, give one question for all the changes in the first call.";

    private readonly object _lock = new();
    private readonly Dictionary<StreamClient, Confirmation> _open = [];

    /// <summary>Opens a confirmation. It replaces the open confirmation of the client.</summary>
    public void Open(StreamClient client, string reference, IReadOnlyList<WriteCall> calls)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            foreach (var expired in _open.Where(o => o.Value.Expires <= now).ToList())
                _open.Remove(expired.Key);
            _open[client] = new Confirmation(client, reference, calls, now + Lifetime);
        }
    }

    /// <summary>Removes the open confirmation of the client. Gives its reference, or null.
    /// </summary>
    public string? Cancel(StreamClient client)
    {
        lock (_lock)
            return _open.Remove(client, out var confirmation) ? confirmation.Reference : null;
    }

    /// <summary>Removes and gives the open confirmation of the client with the reference. Null
    /// when it is not open.</summary>
    public Confirmation? Take(StreamClient client, string reference)
    {
        lock (_lock)
        {
            if (
                !_open.TryGetValue(client, out var confirmation)
                || confirmation.Reference != reference
            )
            {
                return null;
            }
            _open.Remove(client);
            return confirmation.Expires > time.GetUtcNow() ? confirmation : null;
        }
    }

    /// <summary>The JSON Schema of a write tool with the argument <see cref="TextArgument"/>.
    /// </summary>
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
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>
    /// The text of the <c>confirm</c> message. It is the question of the model when it is one line
    /// of <see cref="MaxModelText"/> characters at most, and holds each value of the calls. Else
    /// code makes it from the title of each tool and the values.
    /// </summary>
    public static string Text(string? modelText, IReadOnlyList<WriteCall> calls)
    {
        var values = calls.SelectMany(Values).ToList();
        if (
            modelText is { Length: > 0 and <= MaxModelText }
            && !modelText.Any(char.IsControl)
            && values.All(v =>
                string.IsNullOrEmpty(v) || modelText.Contains(v, StringComparison.Ordinal)
            )
        )
        {
            return modelText;
        }

        return string.Join(
            " ",
            calls
                .GroupBy(c => c.Title)
                .Select(g =>
                {
                    var shown = g.SelectMany(Values)
                        .Select(v => string.IsNullOrEmpty(v) ? "(none)" : $"\"{OneLine(v)}\"")
                        .ToList();
                    return shown.Count == 0
                        ? $"{OneLine(g.Key)}?"
                        : $"{OneLine(g.Key)}? {string.Join(", ", shown)}";
                })
        );
    }

    /// <summary>The value of each shown argument of a call. Null when the call has no value.
    /// </summary>
    private static IEnumerable<string?> Values(WriteCall call) =>
        call.Shown.Select(name =>
            call.Call.Arguments.TryGetValue(name, out var value)
                ? value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Null => null,
                    _ => value.GetRawText(),
                }
                : null
        );

    private static string OneLine(string text) =>
        string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c));
}
