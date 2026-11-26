using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.Server.Tests.Infrastructure;
using static Custos.Server.Tests.Mcp.McpClients;

namespace Custos.Server.Tests.Mcp;

/// <summary>
/// The evaluation set of Optio (<c>optio/evaluation/custos.json</c>) holds the card and the tools
/// of the server, and the model gets them as in a query. Thus a change of a tool or of the card
/// needs the same change in that file.
/// </summary>
public sealed class EvaluationSetTests
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Test]
    public async Task Evaluation_set_of_optio_has_the_card_and_the_tools_of_the_server()
    {
        using var factory = new CustosFactory(discord: new FakeDiscordDirectory(), mcp: true);
        using var http = factory.CreateMcpClient("400000000000000001", "custos:read");
        await using var client = await ConnectAsync(http);
        var tools = await client.ListToolsAsync();

        // The title of a tool as Optio gets it.
        var actual = new JsonObject
        {
            ["title"] = client.ServerInfo?.Title,
            ["card"] = client.ServerInstructions,
            ["tools"] = new JsonArray([
                .. tools.Select(t => new JsonObject
                {
                    ["name"] = t.Name,
                    ["title"] = t.Title ?? t.ProtocolTool.Annotations?.Title ?? t.Name,
                    ["description"] = t.Description,
                    ["read_only"] = t.ProtocolTool.Annotations?.ReadOnlyHint == true,
                    ["input_schema"] = JsonNode.Parse(t.JsonSchema.GetRawText()),
                }),
            ]),
        };
        var set = JsonNode.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "evaluation", "custos.json")
            )
        )!;
        var service = set["services"]!.AsArray().Single(s => (string?)s!["name"] == "custos")!;
        var expected = new JsonObject
        {
            ["title"] = service["title"]?.DeepClone(),
            ["card"] = service["card"]?.DeepClone(),
            ["tools"] = service["tools"]?.DeepClone(),
        };

        Assert.That(
            JsonNode.DeepEquals(actual, expected),
            Is.True,
            "Give the service custos in optio/evaluation/custos.json these members:\n"
                + actual.ToJsonString(Indented)
        );
    }
}
