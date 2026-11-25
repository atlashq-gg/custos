using System.Text.Json;
using Json.Schema;

namespace Optio.Tests.Infrastructure;

/// <summary>The JSON Schemas of spec/, copied next to the test assembly.</summary>
internal static class SpecSchemas
{
    private static readonly Lazy<JsonSchema> Discovery = new(() => Load("discovery.schema.json"));

    // A schema registers itself by its $id when it loads, and a $ref resolves only to a schema
    // that loaded before. Thus each schema loads once, after the schemas that it refers to.
    private static readonly Lazy<JsonSchema> Query = new(() => Load("query.schema.json"));

    private static readonly Lazy<JsonSchema> QueryState = new(() =>
    {
        _ = Query.Value;
        return Load("query-state.schema.json");
    });

    private static readonly Lazy<JsonSchema> Message = new(() =>
    {
        _ = QueryState.Value;
        return Load("message.schema.json");
    });

    public static void AssertDiscoveryDocument(JsonElement document) =>
        AssertValid(Discovery.Value, document);

    public static void AssertMessage(JsonElement document) => AssertValid(Message.Value, document);

    public static void AssertQueryState(JsonElement document) =>
        AssertValid(QueryState.Value, document);

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
