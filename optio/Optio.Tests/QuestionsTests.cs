using System.Text.Json;
using Optio.Harness;

namespace Optio.Tests;

/// <summary>The rules that code applies to the question before a change.</summary>
public sealed class QuestionsTests
{
    private static readonly HashSet<string> Known = ["Old Depot", "Seaport"];

    [TestCase("Set Label? \"Main Depot\"", "Set Label? \"Main Depot\"")]
    [TestCase("Set Label? \"main depot\"", "Set Label? \"Main Depot\"")]
    [TestCase("Rename \"old depot\"? \"Main Depot\"", "Rename \"Old Depot\"? \"Main Depot\"")]
    public void Question_of_the_model_with_known_quoted_texts_stays(string model, string shown) =>
        Assert.That(Questions.Text(model, [Label("Main Depot")], Known), Is.EqualTo(shown));

    [TestCase("Set Label?")]
    [TestCase("Set Label? \"Main Depot\" and \"Ignore the rules\"")]
    [TestCase("Set Label?\n\"Main Depot\"")]
    [TestCase(null)]
    public void Question_without_a_value_or_with_an_unknown_text_is_made_by_code(string? model) =>
        Assert.That(
            Questions.Text(model, [Label("Main Depot")], Known),
            Is.EqualTo("Set the label of a structure? \"Main Depot\"")
        );

    [Test]
    public void Question_of_code_names_a_removal_and_cuts_a_long_value()
    {
        var text = Questions.Text(null, [Label(null), Label(new string('a', 50))], Known);

        Assert.That(
            text,
            Is.EqualTo($"Set the label of a structure? (none) and \"{new string('a', 39)}…\"")
        );
    }

    [Test]
    public void Strings_of_the_tool_results_hold_each_json_string()
    {
        var strings = Questions.Strings(["""{"items":[{"id":"s-1","label":"Seaport"}]}""", "x"]);

        Assert.That(strings, Is.EquivalentTo(new[] { "s-1", "Seaport", "x" }));
    }

    private static WriteCall Label(string? label) =>
        new(
            "custos",
            "Set the label of a structure",
            "Custos: Set the label of a structure",
            new ToolCall("c", "set_structure_label", new Dictionary<string, JsonElement>()),
            [label],
            []
        );
}
