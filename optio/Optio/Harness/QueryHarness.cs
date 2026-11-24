using System.Globalization;
using Microsoft.Extensions.Options;
using Optio.Storage;
using Optio.Streams;

namespace Optio.Harness;

/// <summary>A query of a user in the guild of the stream.</summary>
internal sealed record Query(
    ulong Guild,
    ulong User,
    string AccessToken,
    string Reference,
    string Text
);

/// <summary>
/// The loop of one query: the model, the tool calls, the model. One query has at most
/// <see cref="MaxToolCalls"/> tool calls and <see cref="MaxDuration"/>. The harness sends one
/// <c>progress</c> message for each tool call, then one <c>answer</c> or one <c>error</c>, to each
/// open stream of the user. Each query starts with no earlier conversation.
/// </summary>
internal sealed partial class QueryHarness(
    IModelClient model,
    IToolSource tools,
    GuildSettingsStore settings,
    StreamHub hub,
    IOptions<OptioOptions> options,
    TimeProvider time,
    ILogger<QueryHarness> logger
)
{
    public const int MaxToolCalls = 8;
    public const string Source = "optio";
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(60);

    /// <summary>The limit of the text of a stream message (spec/05-stream.md).</summary>
    public const int MaxTextLength = 500;

    public async Task RunAsync(Query query, CancellationToken stoppingToken)
    {
        using var timeout = new CancellationTokenSource(MaxDuration, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            timeout.Token
        );
        try
        {
            Send(query, "answer", await AnswerAsync(query, linked.Token));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Send(query, "error", "The query took too long. Ask a smaller question.");
        }
        catch (QueryException e)
        {
            Send(query, "error", e.Message);
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            LogQueryFailed(logger, e, query.Reference);
            Send(query, "error", "The query failed. Try again later.");
        }
    }

    private async Task<string> AnswerAsync(Query query, CancellationToken cancellationToken)
    {
        var guildSettings = await settings.GetAsync(query.Guild, cancellationToken);
        var system = SystemPrompt(options.Value.SystemPrompt!, query.Guild, guildSettings.Prompt);
        await using var session = await tools.OpenAsync(
            query.AccessToken,
            query.Guild,
            cancellationToken
        );

        List<ModelMessage> messages = [new UserMessage(query.Text)];
        var calls = 0;
        while (true)
        {
            var turn = await model.NextAsync(
                new ModelRequest(system, session.Tools, messages),
                cancellationToken
            );
            switch (turn.Stop)
            {
                case ModelStop.Refusal:
                    throw new QueryException("The model did not answer this query.");
                case ModelStop.Limit:
                    throw new QueryException("The answer was too long. Ask a smaller question.");
                case ModelStop.End or ModelStop.ToolCalls when turn.ToolCalls.Count == 0:
                    return turn.Text;
            }

            calls += turn.ToolCalls.Count;
            if (calls > MaxToolCalls)
                throw new QueryException("The query needs too many steps. Ask a smaller question.");

            messages.Add(new AssistantMessage(turn));
            List<ToolResult> results = [];
            foreach (var call in turn.ToolCalls)
            {
                var title = session.Tools.FirstOrDefault(t => t.Name == call.Name)?.Title;
                Send(query, "progress", $"{title ?? call.Name}…");
                results.Add(await session.CallAsync(call, cancellationToken));
            }
            messages.Add(new ToolResultsMessage(results));
        }
    }

    /// <summary>
    /// The system prompt of the configuration, then the guild, then the prompt of the guild. The
    /// prompt of the guild comes last and in its own block, so that the system prompt can state
    /// that its rules come first.
    /// </summary>
    internal static string SystemPrompt(string system, ulong guild, string? guildPrompt)
    {
        var text =
            $"{system.Trim()}\n\nThe guild of the user has the ID "
            + $"{guild.ToString(CultureInfo.InvariantCulture)}. Each tool reads this guild.";
        return guildPrompt is null
            ? text
            : $"{text}\n\nThe administrators of the guild give these instructions:\n"
                + $"<guild_instructions>\n{guildPrompt}\n</guild_instructions>";
    }

    private void Send(Query query, string kind, string text)
    {
        text = string.IsNullOrWhiteSpace(text) ? "The model gave no answer." : text.Trim();
        if (text.Length > MaxTextLength)
            text = text[..(MaxTextLength - 1)] + "…";
        hub.SendToUser(query.User, new StreamMessage(kind, text, query.Reference, Source));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The query {Reference} failed")]
    private static partial void LogQueryFailed(
        ILogger logger,
        Exception exception,
        string reference
    );
}

/// <summary>
/// The limits of the queries of each user: one running query, and
/// <see cref="OptioOptions.MaxQueriesPerHour"/> queries in each hour. The limits are in the memory
/// of one instance.
/// </summary>
internal sealed class QueryLimiter(IOptions<OptioOptions> options, TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private readonly Dictionary<ulong, Queue<DateTimeOffset>> _starts = [];
    private readonly HashSet<ulong> _running = [];

    /// <summary>Starts a query. Null when the user can start one now, else the wait.</summary>
    public TimeSpan? TryStart(ulong user)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (_running.Contains(user))
                return TimeSpan.FromSeconds(5);

            var starts = _starts.TryGetValue(user, out var queue) ? queue : _starts[user] = new();
            while (starts.TryPeek(out var first) && first <= now - Window)
                starts.Dequeue();
            if (starts.Count >= options.Value.MaxQueriesPerHour)
                return starts.Peek() + Window - now;

            starts.Enqueue(now);
            _running.Add(user);
            return null;
        }
    }

    public void Finish(ulong user)
    {
        lock (_lock)
            _running.Remove(user);
    }
}
