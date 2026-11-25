using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Optio.Harness;

namespace Optio.Queries;

/// <summary>The end of a part: the answer, and the messages of the query for the
/// conversation.</summary>
internal sealed record PartResult(string Answer, IReadOnlyList<ModelMessage> Messages);

/// <summary>A failure of a query with a text for the user.</summary>
internal sealed class QueryFailedException(string message) : Exception(message);

/// <summary>
/// The loop of one part of a query: the model, the read tools, the model, until the answer. A part
/// has a budget of time and of tool calls. At the end of a budget, the model gets one last step
/// in which it cannot call a tool. Each tool call gives a status text. A failure of a tool goes to
/// the model as a tool error.
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
    /// <paramref name="end"/> plus <see cref="LastStep"/> at the latest.
    /// </summary>
    public async Task<PartResult> RunAsync(
        QueryRecord query,
        ToolUser user,
        IReadOnlyList<ModelMessage> history,
        string? guildPrompt,
        DateTimeOffset end,
        Func<string, Task> status,
        CancellationToken cancellationToken
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
            List<ModelMessage> messages = [.. history, new UserMessage(query.Question)];
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
                    return new PartResult(Fit(turn.Text), messages[first..]);
                }
                messages.Add(new AssistantMessage(turn));

                List<ToolResult> results = [];
                foreach (var call in turn.ToolCalls)
                {
                    calls++;
                    var tool = session.Tools.FirstOrDefault(t => t.Name == call.Name);
                    await status(tool?.Status ?? call.Name);
                    results.Add(await session.CallAsync(call, limit.Token));
                }
                messages.Add(new ToolResultsMessage(results));
            }
        }
        catch (OperationCanceledException)
            when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new QueryFailedException("The query took too long. Ask a smaller question.");
        }
    }

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
