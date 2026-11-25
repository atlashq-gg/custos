using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Optio.Storage;
using Optio.Streams;

namespace Optio.Harness;

/// <summary>A query of a client in the guild of its stream.</summary>
internal sealed record Query(
    StreamClient Client,
    string AccessToken,
    string Reference,
    string Text
);

/// <summary>The state of a query that waits for a confirmation: the messages to the model with
/// the last turn of the model, the tool results of the query, and the number of tool calls.
/// </summary>
internal sealed record Conversation(
    IReadOnlyList<ModelMessage> Messages,
    ModelTurn Turn,
    IReadOnlyList<string> Results,
    int Calls
);

/// <summary>
/// The loop of one query: the model, the tool calls, the model. One query has at most
/// <see cref="MaxToolCalls"/> tool calls and <see cref="MaxDuration"/>. The harness sends one
/// <c>progress</c> message for each tool call, then one <c>answer</c> or one <c>error</c>, to each
/// open stream of the client. Each query starts with no earlier conversation.
/// When the model calls a write tool, the loop stops with a <c>confirm</c> message. After the user
/// accepts, <see cref="ConfirmAsync"/> calls the tools of that turn, and the loop continues: the
/// model reads the results and writes the answer (decisions on #48 of 2026-11-24).
/// </summary>
internal sealed partial class QueryHarness(
    IModelClient model,
    IToolSource tools,
    Confirmations confirmations,
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

    public Task RunAsync(Query query, CancellationToken stoppingToken) =>
        RunAsync(
            query,
            cancellationToken => AnswerAsync(query, null, cancellationToken),
            stoppingToken
        );

    /// <summary>Continues a query after the user accepted its confirmation, with the access token
    /// of the answer.</summary>
    public Task ConfirmAsync(
        Confirmation confirmation,
        string accessToken,
        CancellationToken stoppingToken
    )
    {
        var query = new Query(confirmation.Client, accessToken, confirmation.Reference, "");
        return RunAsync(
            query,
            cancellationToken => AnswerAsync(query, confirmation, cancellationToken),
            stoppingToken
        );
    }

    /// <summary>Runs the work of a query, and sends its result. A null result sends nothing.
    /// </summary>
    private async Task RunAsync(
        Query query,
        Func<CancellationToken, Task<(string Kind, string Text)?>> work,
        CancellationToken stoppingToken
    )
    {
        using var timeout = new CancellationTokenSource(MaxDuration, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            timeout.Token
        );
        try
        {
            if (await work(linked.Token) is { } result)
                Send(query, result.Kind, result.Text);
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

    /// <summary>
    /// The answer, or null when the query waits for a confirmation. After the user accepted a
    /// confirmation, the loop first calls the tools of the turn that waited, then continues.
    /// </summary>
    private async Task<(string Kind, string Text)?> AnswerAsync(
        Query query,
        Confirmation? confirmation,
        CancellationToken cancellationToken
    )
    {
        var guild = query.Client.Guild;
        var guildSettings = await settings.GetAsync(guild, cancellationToken);
        var system = SystemPrompt(options.Value.SystemPrompt!, guild, guildSettings.Prompt);
        var user = new ToolUser(query.AccessToken, guild, query.Client.User);
        await using var session = await tools.OpenAsync(user, cancellationToken);
        List<ModelTool> modelTools =
        [
            .. session.Tools.Select(t =>
                t.Write ? t with { Schema = Confirmations.WithTextArgument(t.Schema) } : t
            ),
        ];

        var conversation = confirmation?.Conversation;
        List<ModelMessage> messages = conversation is null
            ? [new UserMessage(query.Text)]
            : [.. conversation.Messages];
        List<string> results = [.. conversation?.Results ?? []];
        var calls = conversation?.Calls ?? 0;
        if (confirmation is not null)
        {
            await CallToolsAsync(
                query,
                user,
                session,
                confirmation.Conversation.Turn,
                confirmation.Calls,
                messages,
                results,
                cancellationToken
            );
        }

        while (true)
        {
            var turn = await model.NextAsync(
                new ModelRequest(system, modelTools, messages),
                cancellationToken
            );
            switch (turn.Stop)
            {
                case ModelStop.Refusal:
                    throw new QueryException("The model did not answer this query.");
                case ModelStop.Limit:
                    throw new QueryException("The answer was too long. Ask a smaller question.");
                case ModelStop.End or ModelStop.ToolCalls when turn.ToolCalls.Count == 0:
                    return ("answer", turn.Text);
            }

            calls += turn.ToolCalls.Count;
            if (calls > MaxToolCalls)
                throw new QueryException("The query needs too many steps. Ask a smaller question.");
            messages.Add(new AssistantMessage(turn));
            if (Confirm(query, session, new Conversation(messages, turn, results, calls)))
                return null;
            await CallToolsAsync(
                query,
                user,
                session,
                turn,
                [],
                messages,
                results,
                cancellationToken
            );
        }
    }

    /// <summary>Calls the tools of a turn, and adds the results to the conversation. A call in
    /// <paramref name="writes"/> goes to its write tool.</summary>
    private async Task CallToolsAsync(
        Query query,
        ToolUser user,
        IToolSession session,
        ModelTurn turn,
        IReadOnlyList<WriteCall> writes,
        List<ModelMessage> messages,
        List<string> results,
        CancellationToken cancellationToken
    )
    {
        List<ToolResult> turnResults = [];
        foreach (var call in turn.ToolCalls)
        {
            var title = session.Tools.FirstOrDefault(t => t.Name == call.Name)?.Title;
            Send(query, "progress", $"{title ?? call.Name}…");
            turnResults.Add(
                writes.FirstOrDefault(w => w.Call.Id == call.Id) is { } write
                    ? await tools.CallAsync(user, write, cancellationToken)
                    : await session.CallAsync(call, cancellationToken)
            );
        }
        messages.Add(new ToolResultsMessage(turnResults));
        results.AddRange(turnResults.Select(r => r.Content));
    }

    /// <summary>
    /// Opens a confirmation for the write calls of the last turn, and sends the <c>confirm</c>
    /// message. No tool of that turn runs before the user accepts. False when the turn has no
    /// write call.
    /// </summary>
    private bool Confirm(Query query, IToolSession session, Conversation conversation)
    {
        string? modelText = null;
        List<WriteCall> writes = [];
        foreach (var call in conversation.Turn.ToolCalls)
        {
            var arguments = call.Arguments.ToDictionary();
            arguments.Remove(Confirmations.TextArgument, out var text);
            if (session.AsWrite(call with { Arguments = arguments }) is not { } write)
                continue;
            if (writes.Count == 0 && text.ValueKind == JsonValueKind.String)
                modelText = text.GetString();
            writes.Add(write);
        }
        if (writes.Count == 0)
            return false;

        var question = Confirmations.Text(modelText, writes, conversation.Results);
        if (question.Length > MaxTextLength)
            throw new QueryException("The query asks for too many changes at one time.");
        confirmations.Open(query.Client, query.Reference, writes, conversation);
        Send(query, "confirm", question);
        return true;
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

    private void Send(Query query, string kind, string? text)
    {
        text = string.IsNullOrWhiteSpace(text) ? "The model gave no answer." : text.Trim();
        if (text.Length > MaxTextLength)
            text = text[..(MaxTextLength - 1)] + "…";
        hub.SendToClient(query.Client, new StreamMessage(kind, text, query.Reference, Source));
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

    /// <summary>The wait for a user with a running query.</summary>
    public static readonly TimeSpan RunningWait = TimeSpan.FromSeconds(5);

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
                return RunningWait;

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

    /// <summary>Starts the continuation of a query after a confirmation. It does not count in the
    /// hour. False when a query of the user runs.</summary>
    public bool TryContinue(ulong user)
    {
        lock (_lock)
            return _running.Add(user);
    }

    public void Finish(ulong user)
    {
        lock (_lock)
            _running.Remove(user);
    }
}
