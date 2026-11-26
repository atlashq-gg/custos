using System.Text.Json;
using Optio.Harness;

namespace Optio.Tests;

public sealed class McpCatalogTests
{
    private static readonly Catalog Custos = new(
        "Custos",
        "Custos holds the stockpiles.",
        [
            Tool("find_items", """{"guild_id":{"type":"string"},"items":{"type":"array"}}"""),
            Tool("list_guilds", "{}"),
            Tool(
                "set_label",
                """
                {"guild_id":{"type":"string"},"structure_id":{"type":"string"},
                "label":{"type":["string","null"]},"confirm":{"type":"boolean"}}
                """,
                readOnly: false
            ),
        ],
        default
    );

    [Test]
    public void Read_tool_has_the_name_of_the_service_and_no_argument_that_code_sets()
    {
        var (tool, route) = Tools(writeScope: null).Single(t => t.Tool.Name == "custos_find_items");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tool.Status, Is.EqualTo("Custos: Find items"));
            Assert.That(tool.Write, Is.False);
            Assert.That(Properties(tool), Is.EqualTo(new[] { "items" }));
            Assert.That(Required(tool), Is.EqualTo(new[] { "items" }));
            Assert.That(route.HasGuild, Is.True);
        }
    }

    [Test]
    public void Write_tool_needs_the_write_scope_and_gets_the_question_instead_of_confirm()
    {
        var without = Tools(writeScope: null).Select(t => t.Tool.Name);
        var (tool, route) = Tools(writeScope: "custos:write")
            .Single(t => t.Tool.Name == "custos_set_label");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(without, Does.Not.Contain("custos_set_label"));
            Assert.That(tool.Write, Is.True);
            Assert.That(
                Properties(tool),
                Is.EquivalentTo(new[] { "structure_id", "label", Questions.TextArgument })
            );
            Assert.That(
                Required(tool),
                Is.EquivalentTo(new[] { "structure_id", "label", Questions.TextArgument })
            );
            Assert.That(route.Confirm, Is.True);
            Assert.That(route.Shown, Is.EqualTo(new[] { "label" }));
        }
    }

    [Test]
    public void Hidden_tool_is_not_in_the_list()
    {
        var names = McpCatalog
            .Tools(Service("custos:write"), Custos, ["custos_list_guilds"])
            .Select(t => t.Tool.Name);

        Assert.That(names, Is.EqualTo(new[] { "custos_find_items", "custos_set_label" }));
    }

    [Test]
    public void New_tools_are_the_tools_that_the_earlier_catalog_did_not_have()
    {
        var earlier = Custos with { Tools = [.. Custos.Tools.Take(2)] };

        Assert.That(McpCatalog.NewTools(earlier, Custos), Is.EqualTo(new[] { "set_label" }));
    }

    private static List<(ModelTool Tool, ToolRoute Route)> Tools(string? writeScope) =>
        [.. McpCatalog.Tools(Service(writeScope), Custos, [])];

    private static McpServiceOptions Service(string? writeScope) =>
        new() { Name = "custos", WriteScope = writeScope };

    private static CatalogTool Tool(string name, string properties, bool readOnly = true)
    {
        var schema = JsonDocument.Parse(properties).RootElement;
        var required = schema.EnumerateObject().Select(p => p.Name).Where(n => n != "confirm");
        return new CatalogTool(
            name,
            name == "find_items" ? "Find items" : name,
            $"The tool {name}.",
            JsonSerializer.SerializeToElement(
                new
                {
                    type = "object",
                    properties = schema,
                    required,
                }
            ),
            readOnly
        );
    }

    private static string[] Properties(ModelTool tool) =>
        [.. tool.Schema.GetProperty("properties").EnumerateObject().Select(p => p.Name)];

    private static string[] Required(ModelTool tool) =>
        [.. tool.Schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!)];
}
