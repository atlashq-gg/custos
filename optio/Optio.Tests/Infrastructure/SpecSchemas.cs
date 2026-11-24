using System.Text.Json;
using Json.Schema;

namespace Optio.Tests.Infrastructure;

/// <summary>The JSON Schemas of spec/, copied next to the test assembly.</summary>
internal static class SpecSchemas
{
    private static readonly Lazy<JsonSchema> Discovery = new(() => Load("discovery.schema.json"));

    public static void AssertDiscoveryDocument(JsonElement document) =>
        AssertValid(Discovery.Value, document);

    private static JsonSchema Load(string name) =>
        JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "spec", name));

    private static void AssertValid(JsonSchema schema, JsonElement document)
    {
        var result = schema.Evaluate(
            document,
            new EvaluationOptions { OutputFormat = OutputFormat.List }
        );
        var errors = (result.Details ?? [])
            .Where(d => d.Errors is not null)
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"));

        Assert.That(result.IsValid, Is.True, () => string.Join(Environment.NewLine, errors));
    }
}
