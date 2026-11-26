using System.Text.Json;
using System.Text.Json.Nodes;

namespace Optio.Harness;

/// <summary>The card and the tools of a service, as the service gave them at
/// <paramref name="Made"/>.</summary>
internal sealed record Catalog(
    string Title,
    string? Card,
    IReadOnlyList<CatalogTool> Tools,
    DateTimeOffset Made
);

internal sealed record CatalogTool(
    string Name,
    string Title,
    string Description,
    JsonElement Schema,
    bool ReadOnly
);

/// <summary>The route of a tool name of the model to its service. A write tool has
/// <paramref name="Shown"/>, its text arguments with no <c>_id</c> at the end, and
/// <paramref name="Confirm"/> when it has the boolean argument <c>confirm</c>.</summary>
internal sealed record ToolRoute(
    McpServiceOptions Service,
    CatalogTool Tool,
    string Status,
    bool HasGuild,
    bool Write,
    bool Confirm,
    string[] Shown
);

/// <summary>
/// The tools of a catalog as the model gets them. Each name is <c>{service}_{tool}</c>, without the
/// hidden tools. A tool that the service does not mark read-only is a write tool, and only a
/// service with a write scope gives its write tools. The model does not see the arguments that
/// code sets: <c>guild_id</c>, and <c>confirm</c> of a write tool. A write tool gets the argument
/// <see cref="Questions.TextArgument"/>. The query and the evaluation set use the same tools.
/// </summary>
internal static class McpCatalog
{
    public const string GuildArgument = "guild_id";
    public const string ConfirmArgument = "confirm";

    public static IEnumerable<(ModelTool Tool, ToolRoute Route)> Tools(
        McpServiceOptions service,
        Catalog catalog,
        IReadOnlyCollection<string> hidden
    )
    {
        foreach (var tool in catalog.Tools)
        {
            var name = $"{service.Name}_{tool.Name}";
            var write = !tool.ReadOnly;
            if (hidden.Contains(name) || (write && string.IsNullOrEmpty(service.WriteScope)))
                continue;
            var properties = tool.Schema.TryGetProperty("properties", out var p)
                ? p.EnumerateObject().ToList()
                : [];
            var confirm =
                write
                && properties.Any(a => a.Name == ConfirmArgument && HasType(a.Value, "boolean"));
            string[] codeSet = confirm ? [GuildArgument, ConfirmArgument] : [GuildArgument];
            var status = $"{catalog.Title}: {tool.Title}";
            var route = new ToolRoute(
                service,
                tool,
                status,
                properties.Any(a => a.Name == GuildArgument),
                write,
                confirm,
                [
                    .. properties
                        .Where(a =>
                            write
                            && !a.Name.EndsWith("_id", StringComparison.Ordinal)
                            && !codeSet.Contains(a.Name)
                            && HasType(a.Value, "string")
                        )
                        .Select(a => a.Name),
                ]
            );
            var schema = Without(tool.Schema, codeSet);
            yield return (
                new ModelTool(
                    name,
                    status,
                    tool.Description,
                    write ? Questions.WithTextArgument(schema) : schema,
                    write
                ),
                route
            );
        }
    }

    /// <summary>The names of the tools of <paramref name="next"/> that
    /// <paramref name="earlier"/> did not have.</summary>
    public static IEnumerable<string> NewTools(Catalog earlier, Catalog next) =>
        next.Tools.Select(t => t.Name).Except(earlier.Tools.Select(t => t.Name));

    /// <summary>The JSON Schema of a tool without the arguments that code sets.</summary>
    private static JsonElement Without(JsonElement schema, IReadOnlyCollection<string> names)
    {
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        if (node["properties"] is JsonObject properties)
        {
            foreach (var name in names)
                properties.Remove(name);
        }
        if (node["required"] is JsonArray required)
        {
            foreach (
                var item in required.Where(r => names.Contains(r?.GetValue<string>())).ToList()
            )
                required.Remove(item);
        }
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>True when the JSON Schema of an argument allows the type.</summary>
    private static bool HasType(JsonElement schema, string type) =>
        schema.TryGetProperty("type", out var t)
        && (
            t.ValueKind == JsonValueKind.String
                ? t.GetString() == type
                : t.ValueKind == JsonValueKind.Array
                    && t.EnumerateArray().Any(i => i.GetString() == type)
        );
}
