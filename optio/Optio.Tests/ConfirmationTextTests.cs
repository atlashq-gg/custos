using System.Text.Json;
using Optio.Harness;

namespace Optio.Tests;

/// <summary>The text of a <c>confirm</c> message (decisions on #48 of 2026-11-24).</summary>
public sealed class ConfirmationTextTests
{
    private const string Title = "Set the label of a structure";

    [Test]
    public void Question_of_the_model_with_each_value_is_the_text() =>
        Assert.That(
            Confirmations.Text("Set label? \"Main Depot\"", [Label("Main Depot")]),
            Is.EqualTo("Set label? \"Main Depot\"")
        );

    [Test]
    public void One_question_of_the_model_can_hold_the_values_of_all_calls() =>
        Assert.That(
            Confirmations.Text(
                "Set labels? \"Main Depot\", \"North Bunker\"",
                [Label("Main Depot"), Label("North Bunker")]
            ),
            Is.EqualTo("Set labels? \"Main Depot\", \"North Bunker\"")
        );

    [TestCase("Set label? \"Depot\"")]
    [TestCase("Set label?\n\"Main Depot\"")]
    [TestCase("")]
    [TestCase(null)]
    public void Code_makes_the_text_when_the_question_of_the_model_is_not_valid(string? question) =>
        Assert.That(
            Confirmations.Text(question, [Label("Main Depot")]),
            Is.EqualTo($"{Title}? \"Main Depot\"")
        );

    [Test]
    public void Question_of_the_model_over_the_limit_is_not_used() =>
        Assert.That(
            Confirmations.Text(
                $"Set label? \"Main Depot\" {new string('x', Confirmations.MaxModelText)}",
                [Label("Main Depot")]
            ),
            Is.EqualTo($"{Title}? \"Main Depot\"")
        );

    [Test]
    public void Code_text_groups_the_calls_of_a_tool_and_shows_an_absent_value_as_none() =>
        Assert.That(
            Confirmations.Text(null, [Label("Main Depot"), Label(null), Label("A\nB")]),
            Is.EqualTo($"{Title}? \"Main Depot\", (none), \"A B\"")
        );

    private static WriteCall Label(string? label)
    {
        Dictionary<string, JsonElement> arguments = new()
        {
            ["structure_id"] = JsonSerializer.SerializeToElement("s-1"),
        };
        if (label is not null)
            arguments["label"] = JsonSerializer.SerializeToElement(label);
        return new WriteCall(
            "server",
            Title,
            new ToolCall("call-1", "set_structure_label", arguments),
            ["label"]
        );
    }
}
