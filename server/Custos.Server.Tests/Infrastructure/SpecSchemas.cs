using System.Text.Json;
using Json.Schema;

namespace Custos.Server.Tests.Infrastructure;

/// <summary>The JSON Schemas of spec/, copied next to the test assembly.</summary>
internal static class SpecSchemas
{
    private static readonly Lazy<JsonSchema> UploadResult = new(() =>
        Load("upload-result.schema.json")
    );

    public static void AssertUploadResult(JsonElement document) =>
        AssertValid(UploadResult.Value, document);

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
