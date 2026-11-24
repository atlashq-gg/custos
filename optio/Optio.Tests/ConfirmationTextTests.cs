using System.Text.Json;
using Optio.Harness;

namespace Optio.Tests;

/// <summary>The text of a <c>confirm</c> message (decisions on #48 of 2026-11-24).</summary>
public sealed class ConfirmationTextTests
{
    private const string Title = "Set the label of a structure";

    /// <summary>A result of find_structures with the current label of a structure.</summary>
    private static readonly string[] Results =
    [
        JsonSerializer.Serialize(
            new { rows = new[] { new { structure_id = "s-3", label = "Old Depot & Co" } } }
        ),
    ];

    [Test]
    public void Question_of_the_model_with_the_new_value_is_the_text() =>
        AssertModelText("Set Label? \"Main Depot\"", [Label("Main Depot")]);

    [Test]
    public void One_question_of_the_model_can_hold_the_values_of_all_calls() =>
        AssertModelText(
            "Set Labels? \"Main Depot\" and \"North Bunker\"",
            [Label("Main Depot"), Label("North Bunker")]
        );

    /// <summary>The JSON of the result escapes the character &amp;: the check reads the string.
    /// </summary>
    [Test]
    public void Question_of_a_removal_can_hold_the_current_value_from_a_tool_result() =>
        AssertModelText("Remove Label? \"Old Depot & Co\"", [Label(null)]);

    [TestCase("Set Label? \"Depot\"")]
    [TestCase("Set Label? \"Main Depot\" and \"Old Depot\"")]
    [TestCase("Set Label?\n\"Main Depot\"")]
    [TestCase("")]
    [TestCase(null)]
    public void Code_makes_the_text_when_the_question_of_the_model_is_not_valid(string? question) =>
        Assert.That(
            Confirmations.Text(question, [Label("Main Depot")], Results),
            Is.EqualTo($"{Title}? \"Main Depot\"")
        );

    [Test]
    public void Removal_with_a_label_that_no_tool_result_holds_gets_the_text_of_code() =>
        Assert.That(
            Confirmations.Text("Remove Label? \"Main Depot\"", [Label(null)], Results),
            Is.EqualTo($"{Title}? (none)")
        );

    [Test]
    public void Question_of_the_model_over_the_limit_is_not_used() =>
        Assert.That(
            Confirmations.Text(
                $"Set Label? \"Main Depot\" {new string('x', Confirmations.MaxModelText)}",
                [Label("Main Depot")],
                Results
            ),
            Is.EqualTo($"{Title}? \"Main Depot\"")
        );

    [Test]
    public void Code_text_groups_the_calls_of_a_tool_and_shows_an_absent_value_as_none() =>
        Assert.That(
            Confirmations.Text(null, [Label("Main Depot"), Label(null), Label("A\nB")], []),
            Is.EqualTo($"{Title}? \"Main Depot\" and (none) and \"A B\"")
        );

    [TestCase("Set Label? \"main depot\"", "Set Label? \"Main Depot\"")]
    [TestCase("Remove Label? \"OLD DEPOT & CO\"", "Remove Label? \"Old Depot & Co\"")]
    public void Quoted_text_in_another_case_gets_the_case_of_its_source(
        string question,
        string expected
    ) =>
        Assert.That(
            Confirmations.Text(
                question,
                [question.StartsWith("Set") ? Label("Main Depot") : Label(null)],
                Results
            ),
            Is.EqualTo(expected)
        );

    [Test]
    public void Long_text_value_need_not_be_in_the_question_and_a_short_one_in_any_case() =>
        Assert.That(
            Confirmations.Text("Post Progress Note?", [Post("progress", LongText)], []),
            Is.EqualTo("Post Progress Note?")
        );

    [Test]
    public void Quoted_start_of_a_long_value_ends_in_an_ellipsis() =>
        Assert.That(
            Confirmations.Text(
                "Post Progress Note? \"Fuel run done at…\"",
                [Post("progress", LongText)],
                []
            ),
            Is.EqualTo("Post Progress Note? \"Fuel run done at…\"")
        );

    [Test]
    public void Code_text_cuts_a_long_value() =>
        Assert.That(
            Confirmations.Text("Post Note?", [Post("progress", LongText)], []),
            Is.EqualTo($"Post a note? \"progress\" and \"{LongText[..39]}…\"")
        );

    private const string LongText =
        "Fuel run done at the seaport, 40 crates of diesel moved to the front depot.";

    private static WriteCall Post(string type, string text) =>
        new(
            "second",
            "Post a note",
            new ToolCall(
                "call-1",
                "post_note",
                new Dictionary<string, JsonElement>
                {
                    ["team_id"] = JsonSerializer.SerializeToElement("c-1"),
                    ["type"] = JsonSerializer.SerializeToElement(type),
                    ["text"] = JsonSerializer.SerializeToElement(text),
                }
            ),
            ["type", "text"]
        );

    private static void AssertModelText(string question, List<WriteCall> calls) =>
        Assert.That(Confirmations.Text(question, calls, Results), Is.EqualTo(question));

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
