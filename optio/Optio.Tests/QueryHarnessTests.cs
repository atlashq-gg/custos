using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Optio.Harness;
using Optio.Queries;
using Optio.Storage;
using Optio.Telemetry;
using Optio.Tests.Infrastructure;

namespace Optio.Tests;

/// <summary>The loop of one part of a query, with a fake model and fake tools.</summary>
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class QueryHarnessTests
{
    private static readonly OptioOptions Options = new()
    {
        SystemPrompt = "You help the players of a guild.",
        OperatorText = "A label in Custos names a structure.",
        MaxToolCallsPerPart = 2,
    };

    private readonly FakeModel _model = new();
    private readonly FakeTools _tools = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly List<string> _status = [];

    [Test]
    public async Task Each_tool_call_gives_a_status_and_the_answer_ends_the_part()
    {
        _model.Turns.Enqueue(FakeModel.Call(FakeTools.FindItems));
        _model.Turns.Enqueue(FakeModel.Answer("  40 rifles.  "));

        var result = await RunAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(((PartAnswer)result).Answer, Is.EqualTo("40 rifles."));
            Assert.That(_status, Is.EqualTo(new[] { "Custos: Find items" }));
            Assert.That(result.Messages, Has.Count.EqualTo(4));
            Assert.That(_model.Requests.All(r => !r.Answer), Is.True);
        }
    }

    [Test]
    public async Task At_the_end_of_the_tool_calls_the_model_gets_a_last_step_with_no_tool()
    {
        for (var i = 0; i < 5; i++)
            _model.Turns.Enqueue(FakeModel.Call(FakeTools.FindItems));

        var result = await RunAsync();
        var requests = _model.Requests.ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_tools.Calls, Has.Count.EqualTo(2));
            Assert.That(requests, Has.Length.EqualTo(3));
            Assert.That(requests[^1].Answer, Is.True);
            Assert.That(requests[^1].Tools, Is.Not.Empty);
            // The last step gave a call and no text.
            Assert.That(((PartAnswer)result).Answer, Is.EqualTo("The model gave no answer."));
        }
    }

    [Test]
    public async Task At_the_end_of_the_budget_of_time_the_model_gets_a_last_step()
    {
        _model.Gate = new();
        var run = RunAsync();
        await _model.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _model.Reset();

        _time.Advance(TimeSpan.FromSeconds(61));
        await _model.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _model.Gate.SetResult();
        await run;

        Assert.That(_model.Requests.Last().Answer, Is.True);
    }

    [Test]
    public void Part_that_passes_its_last_step_fails_with_a_text_for_the_user()
    {
        _model.Gate = new();
        var run = RunAsync();
        _time.Advance(TimeSpan.FromSeconds(61) + QueryHarness.LastStep);

        Assert.That(
            async () => await run,
            Throws.InstanceOf<QueryFailedException>().With.Message.Contains("took too long")
        );
    }

    [Test]
    public async Task Write_call_ends_the_part_with_the_question_and_runs_no_call_of_its_step()
    {
        _model.Turns.Enqueue(FakeModel.Call(FakeTools.FindItems));
        _model.Turns.Enqueue(
            FakeModel.Call(
                FakeTools.SetLabel,
                """{"structure_id":"s-1","label":"Main Depot","""
                    + "\"confirm_text\":\"Set label? \\\"main depot\\\"\"}"
            )
        );

        var result = await RunAsync();

        var question = (PartQuestion)result;
        using (Assert.EnterMultipleScope())
        {
            // Code puts the case of the value in the question.
            Assert.That(question.Question, Is.EqualTo("Set label? \"Main Depot\""));
            Assert.That(question.Step.Writes.Single().Call.Name, Is.EqualTo("set_structure_label"));
            Assert.That(
                question.Step.Writes.Single().Call.Arguments.ContainsKey(Questions.TextArgument),
                Is.False
            );
            Assert.That(question.Step.Messages, Has.Count.EqualTo(4));
            Assert.That(_tools.Calls, Has.Count.EqualTo(1));
            Assert.That(_tools.Writes, Is.Empty);
        }
    }

    [Test]
    public async Task Write_call_with_an_id_from_no_tool_result_goes_back_to_the_model()
    {
        _model.Turns.Enqueue(
            FakeModel.Call(FakeTools.SetLabel, """{"structure_id":"s-9","label":"Main Depot"}""")
        );
        _model.Turns.Enqueue(FakeModel.Answer("I need to find the structure first."));

        var result = await RunAsync();
        var results = (ToolResultsMessage)_model.Requests.Last().Messages[^1];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.TypeOf<PartAnswer>());
            Assert.That(results.Results.Single().IsError, Is.True);
            Assert.That(results.Results.Single().Content, Does.Contain("s-9"));
        }
    }

    [Test]
    public async Task Apply_runs_the_write_call_with_the_status_of_the_tool()
    {
        _model.Turns.Enqueue(FakeModel.Call(FakeTools.FindItems));
        _model.Turns.Enqueue(
            FakeModel.Call(FakeTools.SetLabel, """{"structure_id":"s-1","label":"Main Depot"}""")
        );
        var question = (PartQuestion)await RunAsync();
        _status.Clear();

        var results = await Harness()
            .ApplyAsync(
                question.Step,
                new ToolUser("decision", FakeMembers.Guild, FakeMembers.Reader),
                text =>
                {
                    _status.Add(text);
                    return Task.CompletedTask;
                },
                CancellationToken.None
            );

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Content, Is.EqualTo(_tools.WriteResult));
            Assert.That(_tools.Writes.Single().User.AccessToken, Is.EqualTo("decision"));
            Assert.That(_status, Is.EqualTo(new[] { "Custos: Set the label of a structure" }));
        }
    }

    [Test]
    public void Refusal_of_the_model_fails_the_query()
    {
        _model.Turns.Enqueue(new ModelTurn("", [], ModelStop.Refusal));

        Assert.That(async () => await RunAsync(), Throws.InstanceOf<QueryFailedException>());
    }

    [Test]
    public void System_prompt_has_the_cards_the_operator_text_the_guild_and_the_guild_prompt_last()
    {
        var prompt = QueryHarness.SystemPrompt(
            Options,
            [
                new ServiceCard("custos", "Custos", "Custos holds the stockpiles.", true),
                new ServiceCard("second", "Second", null, false),
            ],
            Record().Guild,
            "  Be brief.  "
        );

        Assert.That(
            prompt,
            Is.EqualTo(
                "You help the players of a guild.\n\n<services>\n"
                    + "<service name=\"custos\" title=\"Custos\">\nCustos holds the stockpiles.\n"
                    + "</service>\n<service name=\"second\" title=\"Second\">\nThis service is "
                    + "not available now. Its tools are not in the list.\n</service>\n</services>"
                    + "\n\nA label in Custos names a structure.\n\nThe guild of the user has the "
                    + "ID 300000000000000001. Each tool reads this guild.\n\nWrite the answer as "
                    + "plain text with no markup.\n\nThe administrators of the guild give these "
                    + "instructions:\n<guild_instructions>\nBe brief.\n</guild_instructions>"
            )
        );
    }

    [Test]
    public void Long_answer_is_cut_to_the_limit_of_the_message()
    {
        var text = QueryHarness.Fit(new string('a', 3000));

        Assert.That(text, Has.Length.EqualTo(QueryHarness.MaxText));
    }

    private QueryHarness Harness() =>
        new(
            _model,
            _tools,
            Microsoft.Extensions.Options.Options.Create(Options),
            _time,
            Recorder()
        );

    /// <summary>A recorder with no database: it counts the tokens, and each row gives an error in
    /// the log.</summary>
    private static UsageRecorder Recorder() =>
        new ServiceCollection()
            .AddMetrics()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<OptioMetrics>()
            .AddSingleton<UsageRecorder>()
            .BuildServiceProvider()
            .GetRequiredService<UsageRecorder>();

    private Task<PartResult> RunAsync() =>
        Harness()
            .RunAsync(
                Record(),
                new ToolUser("token", FakeMembers.Guild, FakeMembers.Reader),
                [],
                null,
                _time.GetUtcNow() + TimeSpan.FromSeconds(60),
                text =>
                {
                    _status.Add(text);
                    return Task.CompletedTask;
                },
                CancellationToken.None
            );

    private QueryRecord Record() =>
        new(
            "query-0001",
            "client",
            FakeMembers.Guild,
            FakeMembers.Reader,
            "How many rifles?",
            false,
            QueryStates.Working,
            null,
            null,
            null,
            "owner",
            _time.GetUtcNow()
        );
}
