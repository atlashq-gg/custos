using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Optio.Harness;

namespace Optio.Queries;

/// <summary>
/// A step that waits for the decision of the user: the messages of the query up to the turn with
/// the calls, the calls of that turn, its write calls, and the texts of the tool results of the
/// query. It is in the record of the query, so that the decision works after a restart.
/// </summary>
internal sealed record PendingStep(
    List<StoredMessage> Messages,
    List<ToolCall> Calls,
    List<WriteCall> Writes,
    List<string> Results
);

/// <summary>The end of a part, with the messages of the query.</summary>
internal abstract record PartResult(IReadOnlyList<ModelMessage> Messages);

/// <summary>The answer. The query ends.</summary>
internal sealed record PartAnswer(string Answer, IReadOnlyList<ModelMessage> Messages)
    : PartResult(Messages);

/// <summary>A question before a change. The query waits for the decision.</summary>
internal sealed record PartQuestion(
    string Question,
    PendingStep Step,
    IReadOnlyList<ModelMessage> Messages
) : PartResult(Messages);

/// <summary>The continuation of a query after a decision: its messages with the results of the
/// step, and the texts of all its tool results.</summary>
internal sealed record QueryResume(IReadOnlyList<ModelMessage> Messages, List<string> Results);

/// <summary>A failure of a query with a text for the user.</summary>
internal sealed class QueryFailedException(string message) : Exception(message);

/// <summary>
/// The loop of one part of a query: the model, the tools, the model, until the answer or a
/// question. A part has a budget of time and of tool calls. At the end of a budget, the model gets
/// one last step in which it cannot call a tool. Each read call gives a status text. A failure of
/// a tool goes to the model as a tool error. When a step holds a write call, no call of the step
/// runs: the part ends with the question, and <see cref="ApplyAsync"/> runs the step after the
/// user accepts. Each ID of a write call must come from a tool result of the query.
/// </summary>
internal sealed class QueryHarness(
    IModelClient model,
    IToolSource tools,
    IOptions<OptioOptions> options,
    TimeProvider time
)
{
    /// <summary>The time of the last step after the budget.</summary>
    public static readonly TimeSpan LastStep = TimeSpan.FromSeconds(20);

    /// <summary>The longest text of a stream message of a query (spec/06-query.md).</summary>
    public const int MaxText = 2000;

    /// <summary>
    /// Runs one part. <paramref name="end"/> is the end of the budget of time. The work stops at
    /// <paramref name="end"/> plus <see cref="LastStep"/> at the latest. With
    /// <paramref name="resume"/>, the part continues the query after a decision.
    /// </summary>
    public async Task<PartResult> RunAsync(
        QueryRecord query,
        ToolUser user,
        IReadOnlyList<ModelMessage> history,
        string? guildPrompt,
        DateTimeOffset end,
        Func<string, Task> status,
        CancellationToken cancellationToken,
        QueryResume? resume = null
    )
    {
        var o = options.Value;
        using var limitTimer = new CancellationTokenSource(
            Clamp(end + LastStep - time.GetUtcNow()),
            time
        );
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            limitTimer.Token
        );
        try
        {
            await using var session = await tools.OpenAsync(user, limit.Token);
            var system = SystemPrompt(o, session.Services, query, guildPrompt);
            List<ModelMessage> messages = resume is null
                ? [.. history, new UserMessage(query.Question)]
                : [.. history, .. resume.Messages];
            List<string> results = [.. resume?.Results ?? []];
            var first = history.Count;
            var calls = 0;
            using var budgetTimer = new CancellationTokenSource(
                Clamp(end - time.GetUtcNow()),
                time
            );
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(
                limit.Token,
                budgetTimer.Token
            );

            while (true)
            {
                var last = calls >= o.MaxToolCallsPerPart || budget.IsCancellationRequested;
                ModelTurn turn;
                try
                {
                    turn = await model.NextAsync(
                        new ModelRequest(system, session.Tools, messages, Answer: last),
                        last ? limit.Token : budget.Token
                    );
                }
                catch (OperationCanceledException)
                    when (budget.IsCancellationRequested && !limit.IsCancellationRequested)
                {
                    continue;
                }

                switch (turn.Stop)
                {
                    case ModelStop.Refusal:
                        throw new QueryFailedException("The model did not answer this query.");
                    case ModelStop.Limit:
                        throw new QueryFailedException(
                            "The answer was too long. Ask a smaller question."
                        );
                }
                if (last || turn.ToolCalls.Count == 0)
                {
                    // A call in the last step does not run, so the conversation keeps no call
                    // without its result.
                    messages.Add(new AssistantMessage(turn with { ToolCalls = [] }));
                    return new PartAnswer(Fit(turn.Text), messages[first..]);
                }
                messages.Add(new AssistantMessage(turn));
                calls += turn.ToolCalls.Count;

                var writes = turn.ToolCalls.Select(session.AsWrite).OfType<WriteCall>().ToList();
                if (writes.Count > 0)
                {
                    var known = Questions.Strings(results);
                    if (
                        writes.SelectMany(w => w.Ids).FirstOrDefault(id => !known.Contains(id)) is
                        { } unknown
                    )
                    {
                        messages.Add(NotRun(turn.ToolCalls, writes, unknown));
                        continue;
                    }
                    var question = Questions.Text(ModelQuestion(turn), writes, known);
                    var own = messages[first..];
                    return new PartQuestion(
                        question,
                        new PendingStep(
                            [.. own.Select(StoredMessage.Of)],
                            [.. turn.ToolCalls],
                            writes,
                            results
                        ),
                        own
                    );
                }

                List<ToolResult> step = [];
                foreach (var call in turn.ToolCalls)
                {
                    var tool = session.Tools.FirstOrDefault(t => t.Name == call.Name);
                    await status(tool?.Status ?? call.Name);
                    step.Add(await session.CallAsync(call, limit.Token));
                }
                messages.Add(new ToolResultsMessage(step));
                results.AddRange(step.Where(r => !r.IsError).Select(r => r.Content));
            }
        }
        catch (OperationCanceledException)
            when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new QueryFailedException("The query took too long. Ask a smaller question.");
        }
    }

    /// <summary>
    /// Runs the calls of a step that the user accepted, in their order: each write call with a
    /// token of the write scope, and each read call as in a part. A failure of a service during a
    /// write call throws, because the result of the change is then not known.
    /// </summary>
    public async Task<List<ToolResult>> ApplyAsync(
        PendingStep step,
        ToolUser user,
        Func<string, Task> status,
        CancellationToken cancellationToken
    )
    {
        await using var session = await tools.OpenAsync(user, cancellationToken);
        List<ToolResult> results = [];
        foreach (var call in step.Calls)
        {
            if (step.Writes.FirstOrDefault(w => w.Call.Id == call.Id) is { } write)
            {
                await status(write.Status);
                results.Add(await tools.CallWriteAsync(user, write, cancellationToken));
            }
            else
            {
                results.Add(await session.CallAsync(call, cancellationToken));
            }
        }
        return results;
    }

    /// <summary>The results of a step whose calls did not run, for the
    /// <paramref name="reason"/>.</summary>
    internal static ToolResultsMessage NotDone(IEnumerable<ToolCall> calls, string reason) =>
        new([.. calls.Select(c => new ToolResult(c.Id, reason, true))]);

    /// <summary>The question of the model: the argument <see cref="Questions.TextArgument"/> of
    /// the first write call of the turn that has it.</summary>
    private static string? ModelQuestion(ModelTurn turn) =>
        turn
            .ToolCalls.Select(c =>
                c.Arguments.TryGetValue(Questions.TextArgument, out var text)
                && text.ValueKind == System.Text.Json.JsonValueKind.String
                    ? text.GetString()
                    : null
            )
            .FirstOrDefault(t => t is not null);

    /// <summary>The results of a step with an ID that no tool result gave: no call runs, and the
    /// model learns why.</summary>
    private static ToolResultsMessage NotRun(
        IReadOnlyList<ToolCall> calls,
        List<WriteCall> writes,
        string unknown
    ) =>
        new([
            .. calls.Select(c => new ToolResult(
                c.Id,
                writes.Any(w => w.Call.Id == c.Id && w.Ids.Contains(unknown))
                    ? $"The ID {unknown} does not come from a tool result of this query. "
                        + "Find the record with a read tool first."
                    : "Not run: a write call of this step has an ID that no tool result gave.",
                true
            )),
        ]);

    /// <summary>
    /// The system prompt: the prompt of the configuration, one section for each service with its
    /// card, the text of the operator, the guild, and the form of the answer. The prompt of the
    /// guild comes last and in its own block, so that the system prompt can state that its rules
    /// come first.
    /// </summary>
    internal static string SystemPrompt(
        OptioOptions options,
        IReadOnlyList<ServiceCard> services,
        QueryRecord query,
        string? guildPrompt
    )
    {
        var text = new StringBuilder(options.SystemPrompt?.Trim());
        text.Append("\n\n<services>");
        foreach (var service in services)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"\n<service name=\"{service.Name}\" title=\"{service.Title}\">\n"
            );
            text.Append(
                service.Available
                    ? service.Card?.Trim() ?? "This service gives no description."
                    : "This service is not available now. Its tools are not in the list."
            );
            text.Append("\n</service>");
        }
        text.Append("\n</services>");
        if (!string.IsNullOrWhiteSpace(options.OperatorText))
            text.Append("\n\n").Append(options.OperatorText.Trim());
        text.Append(
            CultureInfo.InvariantCulture,
            $"\n\nThe guild of the user has the ID {query.Guild}. Each tool reads this guild."
        );
        text.Append("\n\nWrite the answer as plain text with no markup.");
        if (!string.IsNullOrWhiteSpace(guildPrompt))
        {
            text.Append("\n\nThe administrators of the guild give these instructions:\n")
                .Append("<guild_instructions>\n")
                .Append(guildPrompt.Trim())
                .Append("\n</guild_instructions>");
        }
        return text.ToString();
    }

    /// <summary>The answer for the stream message: not empty, and at most
    /// <see cref="MaxText"/> characters.</summary>
    internal static string Fit(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return "The model gave no answer.";
        return text.Length <= MaxText ? text : text[..(MaxText - 1)] + "…";
    }

    private static TimeSpan Clamp(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
