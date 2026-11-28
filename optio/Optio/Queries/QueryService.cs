using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Optio.Events;
using Optio.Harness;
using Optio.Storage;
using Optio.Telemetry;

namespace Optio.Queries;

/// <summary>A query of spec/06-query.md as Custos sends it.</summary>
internal sealed record QueryRequest(string Reference, string Text, bool NewConversation);

/// <summary>The outcome of a request: a status, and the state document or a problem.</summary>
internal sealed record QueryOutcome(
    int Status,
    QueryRecord? Record = null,
    string? Problem = null,
    TimeSpan? RetryAfter = null
);

/// <summary>
/// The queries of the clients. A query starts with its record in the bucket, then runs in the
/// background. Each change of state goes first to the record, and the winner of the change
/// publishes its message. A client has one open query, and a user has one open query.
/// <para>
/// A part that asks a question ends in the state <c>waiting</c>. Optio owns the timer of the
/// question. After <c>accept</c>, the query goes to <c>applying</c>, runs the step with the token
/// of the decision, and goes on in a new part. A failure in <c>applying</c> closes the query with
/// <c>result_unknown</c>.
/// </para>
/// <para>
/// At the start, Optio closes each running query that a stopped instance left open, and starts the
/// timer of each question again. At a normal stop, it closes its running queries. A question
/// stays.
/// </para>
/// </summary>
internal sealed partial class QueryService(
    QueryStore store,
    QueryHarness harness,
    QueryLimits limits,
    EventStream events,
    IServiceScopeFactory scopes,
    OptioMetrics metrics,
    IOptions<OptioOptions> options,
    TimeProvider time,
    ILogger<QueryService> logger
) : BackgroundService
{
    /// <summary>The time for a closing message at a stop of Optio.</summary>
    private static readonly TimeSpan StopTime = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait of a new query for a change in progress.</summary>
    internal static readonly TimeSpan ApplyWait = TimeSpan.FromSeconds(10);

    /// <summary>The time after the end of a budget in which the next message comes.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(5);

    /// <summary>The instance of Optio. A running record of another owner at the start is
    /// interrupted.</summary>
    private readonly string _owner = Guid.NewGuid().ToString("N");

    private readonly ConcurrentDictionary<string, Run> _runs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _timers = new();

    /// <summary>The messages of each waiting query with the content of the provider, for the
    /// part after the decision. After a restart, the record gives them without that
    /// content.</summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<ModelMessage>> _live = new();

    private readonly SemaphoreSlim[] _locks =
    [
        .. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)),
    ];

    public bool IsReady => events.IsReady;

    /// <summary>Starts a query, or gives the state of the same query again.</summary>
    public async Task<QueryOutcome> SubmitAsync(
        QueryClient client,
        QueryRequest request,
        string accessToken,
        DateTimeOffset tokenExpiry,
        CancellationToken cancellationToken
    )
    {
        var gate = _locks[client.User % (ulong)_locks.Length];
        await gate.WaitAsync(cancellationToken);
        try
        {
            var (record, _) = await store.GetAsync(client.Key, cancellationToken);
            if (record?.Reference == request.Reference)
                return new QueryOutcome(StatusCodes.Status202Accepted, record);
            if (await limits.CheckAsync(client, cancellationToken) is { } refusal)
                return refusal;
            if (!await store.ClaimUserAsync(client, cancellationToken))
                return new QueryOutcome(
                    StatusCodes.Status409Conflict,
                    Problem: "Another Custos of yours has an open query. Wait for its end."
                );

            // A change in progress completes first.
            for (var waited = TimeSpan.Zero; record?.State == QueryStates.Applying; )
            {
                if (waited >= ApplyWait)
                    return new QueryOutcome(
                        StatusCodes.Status409Conflict,
                        Problem: "A change of your last query is in progress. Ask again soon."
                    );
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                waited += TimeSpan.FromMilliseconds(100);
                (record, _) = await store.GetAsync(client.Key, cancellationToken);
            }
            if (record is { IsOpen: true })
            {
                var replaced = await store.ChangeAsync(
                    client.Key,
                    record.Reference,
                    r =>
                        r.State is QueryStates.Working or QueryStates.Waiting
                            ? r.Close(QueryStates.Replaced, "A new query replaced this query.")
                            : null,
                    cancellationToken
                );
                if (replaced is not null)
                    metrics.QueryClosed(QueryStates.Replaced);
                EndWork(client.Key, record.Reference);
            }

            var now = time.GetUtcNow();
            var end = PartEnd(now, tokenExpiry);
            var query = new QueryRecord(
                request.Reference,
                client.Key,
                client.Guild,
                client.User,
                request.Text,
                request.NewConversation,
                QueryStates.Working,
                null,
                end + QueryHarness.LastStep + Margin,
                null,
                _owner,
                now
            );
            var (_, revision) = await store.GetAsync(client.Key, cancellationToken);
            if (await store.TryPutAsync(query, revision, cancellationToken) is null)
            {
                await store.ReleaseUserAsync(client, cancellationToken);
                return new QueryOutcome(
                    StatusCodes.Status409Conflict,
                    Problem: "The query changed at the same time. Send it again."
                );
            }
            await store.PublishAsync(query, cancellationToken);
            StartPart(
                client,
                query,
                new ToolUser(accessToken, client.Guild, client.User),
                end,
                null
            );
            return new QueryOutcome(StatusCodes.Status202Accepted, query);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Applies an action of spec/06-query.md to the query of the client. The token of
    /// <c>accept</c> applies the change.</summary>
    public async Task<QueryOutcome> ActAsync(
        QueryClient client,
        string reference,
        string action,
        string accessToken,
        DateTimeOffset tokenExpiry,
        CancellationToken cancellationToken
    )
    {
        var (record, _) = await store.GetAsync(client.Key, cancellationToken);
        if (record?.Reference != reference)
            return new QueryOutcome(
                StatusCodes.Status404NotFound,
                Problem: "This client has no query with this reference."
            );
        // A second equal action gets the same response.
        if (record.Decision == action)
            return new QueryOutcome(StatusCodes.Status200OK, record);

        var now = time.GetUtcNow();
        QueryRecord? before = null;
        var changed = await store.ChangeAsync(
            client.Key,
            reference,
            r =>
            {
                before = r;
                return (action, r.State) switch
                {
                    ("stop", QueryStates.Working or QueryStates.Waiting) => r.Close(
                        QueryStates.Stopped,
                        "You stopped the query."
                    ) with
                    {
                        Decision = action,
                    },
                    ("refuse", QueryStates.Waiting) => r.Close(
                        QueryStates.Refused,
                        "You refused the change."
                    ) with
                    {
                        Decision = action,
                    },
                    ("accept", QueryStates.Waiting) when r.Deadline > now && r.Pending is { } p =>
                        r with
                        {
                            State = QueryStates.Applying,
                            Text = p.Writes[0].Status,
                            Deadline = PartEnd(now, tokenExpiry) + QueryHarness.LastStep + Margin,
                            Decision = action,
                            Owner = _owner,
                        },
                    _ => null,
                };
            },
            cancellationToken
        );
        if (changed is null)
        {
            (record, _) = await store.GetAsync(client.Key, cancellationToken);
            return new QueryOutcome(
                StatusCodes.Status409Conflict,
                record,
                $"The query is {record?.State ?? "gone"}. It does not permit this action."
            );
        }

        CancelTimer(client.Key);
        if (changed.State == QueryStates.Applying)
        {
            var user = new ToolUser(accessToken, client.Guild, client.User);
            StartApply(client, changed, before!.Pending!, user, PartEnd(now, tokenExpiry));
            return new QueryOutcome(StatusCodes.Status200OK, changed);
        }

        EndWork(client.Key, reference);
        metrics.QueryClosed(changed.Cause!);
        await store.ReleaseUserAsync(client, cancellationToken);
        if (changed.Cause == QueryStates.Refused && before?.Pending is { } step)
        {
            await SaveNotDoneAsync(
                changed,
                step,
                "The user refused the change. No call of this step ran.",
                cancellationToken
            );
        }
        return new QueryOutcome(StatusCodes.Status200OK, changed);
    }

    /// <summary>The message of the open query of the client, for a stream that opens. Null when
    /// the client has no open query.</summary>
    public async Task<QueryMessage?> OpenMessageAsync(
        QueryClient client,
        CancellationToken cancellationToken
    ) =>
        (await store.GetAsync(client.Key, cancellationToken)).Record is { IsOpen: true } record
            ? record.Message()
            : null;

    /// <summary>Closes the running queries that an instance of Optio left open, and starts the
    /// timer of each question again. The broker can be unavailable at the start, so this tries
    /// again after a wait.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Real time: the waits do not depend on the clock of the queries.
        while (!events.IsReady)
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        var wait = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var record in store.RecordsAsync(stoppingToken))
                {
                    if (record.State == QueryStates.Waiting)
                        ArmTimer(record);
                    else if (record.IsOpen && record.Owner != _owner)
                        await InterruptAsync(record, stoppingToken);
                }
                return;
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                LogRecoveryFailed(logger, e);
                await Task.Delay(wait, stoppingToken);
                wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, TimeSpan.FromMinutes(1).Ticks));
            }
        }
    }

    /// <summary>Closes the running queries with the cause <c>interrupted</c>, or
    /// <c>result_unknown</c> during a change. A question stays: the next start continues
    /// it.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var timer in _timers.Values)
            await timer.CancelAsync();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(StopTime);
        foreach (var run in _runs.Values)
        {
            await run.Cancellation.CancelAsync();
            try
            {
                if (
                    (await store.GetAsync(run.Client.Key, limit.Token)).Record is { } record
                    && record.Reference == run.Reference
                    && record.State != QueryStates.Waiting
                )
                {
                    await InterruptAsync(record, limit.Token);
                }
            }
            catch (Exception e)
            {
                // The stop goes on. The next start closes the query.
                LogStopFailed(logger, e, run.Reference);
            }
        }
        await base.StopAsync(cancellationToken);
    }

    /// <summary>Closes an open query that has no owner now. A change that was in progress has a
    /// result that Optio does not know.</summary>
    private async Task InterruptAsync(QueryRecord record, CancellationToken cancellationToken)
    {
        var closed = await store.ChangeAsync(
            record.Client,
            record.Reference,
            r =>
                r.State switch
                {
                    QueryStates.Working => r.Close(
                        QueryStates.Interrupted,
                        "The service stopped during the query. Ask again."
                    ),
                    QueryStates.Applying => r.Close(
                        QueryStates.ResultUnknown,
                        "The service stopped during the change. Its result is not known."
                    ),
                    _ => null,
                },
            cancellationToken
        );
        if (closed is null)
            return;
        metrics.QueryClosed(closed.Cause!);
        await store.ReleaseUserAsync(Client(record), cancellationToken);
    }

    /// <summary>Runs a part of a query in the background until its answer, its question, its
    /// stop, or the stop of Optio.</summary>
    private void StartPart(
        QueryClient client,
        QueryRecord query,
        ToolUser user,
        DateTimeOffset end,
        QueryResume? resume
    ) => Track(client, query, token => PartAsync(client, query, user, end, resume, token));

    /// <summary>Runs the step that the user accepted, then the next part.</summary>
    private void StartApply(
        QueryClient client,
        QueryRecord query,
        PendingStep step,
        ToolUser user,
        DateTimeOffset end
    ) => Track(client, query, async token =>
        {
            var messages = _live.TryRemove(query.Reference, out var live)
                ? live
                : [.. step.Messages.Select(m => m.ToModel())];
            List<ToolResult> results;
            try
            {
                results = await harness.ApplyAsync(
                    step,
                    user,
                    text =>
                        store.ChangeAsync(
                            client.Key,
                            query.Reference,
                            r => r.State == QueryStates.Applying ? r with { Text = text } : null,
                            token
                        ),
                    token
                );
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                LogApplyFailed(logger, e, query.Reference);
                await CloseAsync(
                    client,
                    query,
                    QueryStates.Applying,
                    QueryStates.ResultUnknown,
                    "The change failed. Its result is not known. Look at the records.",
                    token
                );
                return;
            }

            var working = await store.ChangeAsync(
                client.Key,
                query.Reference,
                r =>
                    r.State == QueryStates.Applying
                        ? r with
                        {
                            State = QueryStates.Working,
                            Text = null,
                            Pending = null,
                        }
                        : null,
                token
            );
            if (working is null)
                return;
            await PartAsync(
                client,
                working,
                user,
                end,
                new QueryResume(
                    [.. messages, new ToolResultsMessage(results)],
                    [.. step.Results, .. results.Where(r => !r.IsError).Select(r => r.Content)]
                ),
                token
            );
        });

    /// <summary>One part: the loop of the model, then the answer or the question.</summary>
    private async Task PartAsync(
        QueryClient client,
        QueryRecord query,
        ToolUser user,
        DateTimeOffset end,
        QueryResume? resume,
        CancellationToken token
    )
    {
        var history = query.NewConversation
            ? []
            : (await store.GetConversationAsync(client.Key, token))?.Messages().ToList() ?? [];
        var result = await harness.RunAsync(
            query,
            user,
            history,
            await GuildPromptAsync(client.Guild, token),
            end,
            text =>
                store.ChangeAsync(
                    client.Key,
                    query.Reference,
                    r => r.State == QueryStates.Working ? r with { Text = text } : null,
                    token
                ),
            token,
            resume
        );

        if (result is PartQuestion question)
        {
            var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(options.Value.QuestionSeconds);
            var waiting = await store.ChangeAsync(
                client.Key,
                query.Reference,
                r =>
                    r.State == QueryStates.Working
                        ? r with
                        {
                            State = QueryStates.Waiting,
                            Text = question.Question,
                            Deadline = deadline,
                            Pending = question.Step,
                            Decision = null,
                        }
                        : null,
                token
            );
            if (waiting is not null)
            {
                _live[query.Reference] = question.Messages;
                ArmTimer(waiting);
            }
            return;
        }

        var answer = (PartAnswer)result;
        if (
            await CloseAsync(
                client,
                query,
                QueryStates.Working,
                QueryStates.Answer,
                answer.Answer,
                token
            )
        )
        {
            await store.AddExchangeAsync(
                client.Key,
                query.NewConversation,
                Exchange.Of(answer.Messages),
                token
            );
        }
    }

    /// <summary>Runs the work of a query in the background, and closes it on a failure.</summary>
    private void Track(QueryClient client, QueryRecord query, Func<CancellationToken, Task> work)
    {
        var run = new Run(client, query.Reference, new CancellationTokenSource());
        _runs[client.Key] = run;
        _ = Task.Run(
            async () =>
            {
                var token = run.Cancellation.Token;
                try
                {
                    await work(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // A stop, a new query, or the stop of Optio closed the query.
                }
                catch (QueryFailedException e)
                {
                    await CloseAsync(
                        client,
                        query,
                        QueryStates.Working,
                        QueryStates.Failed,
                        e.Message,
                        token
                    );
                }
                catch (Exception e)
                {
                    LogQueryFailed(logger, e, query.Reference);
                    try
                    {
                        await CloseAsync(
                            client,
                            query,
                            QueryStates.Working,
                            QueryStates.Failed,
                            "The query failed. Try again later.",
                            token
                        );
                    }
                    catch (Exception again)
                    {
                        LogQueryFailed(logger, again, query.Reference);
                    }
                }
                finally
                {
                    // No dispose: a stop or a new query can cancel the run at the same time. The
                    // source has no timer.
                    _runs.TryRemove(new(client.Key, run));
                }
            },
            CancellationToken.None
        );
    }

    /// <summary>Starts the timer of a question. At its end, the query closes as
    /// expired.</summary>
    private void ArmTimer(QueryRecord record)
    {
        var timer = new CancellationTokenSource();
        if (_timers.TryRemove(record.Client, out var old))
            old.Cancel();
        _timers[record.Client] = timer;
        var wait = record.Deadline!.Value - time.GetUtcNow();
        _ = Task.Run(
            async () =>
            {
                try
                {
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait, time, timer.Token);
                    await ExpireAsync(record, timer.Token);
                }
                catch (OperationCanceledException) when (timer.IsCancellationRequested) { }
                catch (Exception e)
                {
                    LogQueryFailed(logger, e, record.Reference);
                }
                finally
                {
                    _timers.TryRemove(new(record.Client, timer));
                }
            },
            CancellationToken.None
        );
    }

    private async Task ExpireAsync(QueryRecord record, CancellationToken cancellationToken)
    {
        QueryRecord? before = null;
        var closed = await store.ChangeAsync(
            record.Client,
            record.Reference,
            r =>
            {
                before = r;
                return r.State == QueryStates.Waiting
                    ? r.Close(QueryStates.Expired, "The question expired. Nothing changed.")
                    : null;
            },
            cancellationToken
        );
        if (closed is null)
            return;
        _live.TryRemove(record.Reference, out _);
        metrics.QueryClosed(QueryStates.Expired);
        await store.ReleaseUserAsync(Client(record), cancellationToken);
        if (before?.Pending is { } step)
        {
            await SaveNotDoneAsync(
                closed,
                step,
                "The question expired with no decision. No call of this step ran.",
                cancellationToken
            );
        }
    }

    /// <summary>Adds a query that ended at a question to the conversation of its client, with
    /// the <paramref name="reason"/> as the result of each call. Thus the model of a follow-up
    /// knows that the change did not happen.</summary>
    private Task SaveNotDoneAsync(
        QueryRecord query,
        PendingStep step,
        string reason,
        CancellationToken cancellationToken
    ) =>
        store.AddExchangeAsync(
            query.Client,
            query.NewConversation,
            new Exchange([
                .. step.Messages,
                StoredMessage.Of(QueryHarness.NotDone(step.Calls, reason)),
            ]),
            cancellationToken
        );

    /// <summary>The prompt of the guild, or null for a guild without one.</summary>
    private async Task<string?> GuildPromptAsync(ulong guild, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<GuildSettingsStore>();
        try
        {
            return (await store.GetAsync(guild, cancellationToken)).Prompts.Guild;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            // The query does not run without the rules of the guild.
            LogNoGuildSettings(logger, e, guild);
            throw new QueryFailedException(
                "The settings of your guild are not available. Try again later."
            );
        }
    }

    /// <summary>Closes a query in the state <paramref name="from"/>. False when another change
    /// was first.</summary>
    private async Task<bool> CloseAsync(
        QueryClient client,
        QueryRecord query,
        string from,
        string cause,
        string text,
        CancellationToken cancellationToken
    )
    {
        var closed = await store.ChangeAsync(
            client.Key,
            query.Reference,
            r => r.State == from ? r.Close(cause, text) : null,
            cancellationToken
        );
        if (closed is null)
            return false;
        metrics.QueryClosed(cause);
        await store.ReleaseUserAsync(client, cancellationToken);
        return true;
    }

    /// <summary>Stops the run, the timer, and the messages of a query that another change
    /// closed.</summary>
    private void EndWork(string client, string reference)
    {
        if (_runs.TryGetValue(client, out var run) && run.Reference == reference)
            run.Cancellation.Cancel();
        CancelTimer(client);
        _live.TryRemove(reference, out _);
    }

    private void CancelTimer(string client)
    {
        if (_timers.TryRemove(client, out var timer))
            timer.Cancel();
    }

    /// <summary>The end of the budget of a part: the budget of time, but 5 seconds before the
    /// token expires at the latest, because the tools use the token.</summary>
    private DateTimeOffset PartEnd(DateTimeOffset now, DateTimeOffset tokenExpiry)
    {
        var budget = now + options.Value.PartBudget;
        var token = tokenExpiry - Margin;
        return budget < token ? budget : token;
    }

    private static QueryClient Client(QueryRecord record) =>
        new(record.Client, record.Guild, record.User);

    private sealed record Run(
        QueryClient Client,
        string Reference,
        CancellationTokenSource Cancellation
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The settings of guild {GuildId} were not available for a query"
    )]
    private static partial void LogNoGuildSettings(
        ILogger logger,
        Exception exception,
        ulong guildId
    );

    [LoggerMessage(Level = LogLevel.Warning, Message = "The query {Reference} failed")]
    private static partial void LogQueryFailed(
        ILogger logger,
        Exception exception,
        string reference
    );

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "The change of the query {Reference} failed. Its result is not known"
    )]
    private static partial void LogApplyFailed(
        ILogger logger,
        Exception exception,
        string reference
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Optio could not close the query {Reference} at its stop"
    )]
    private static partial void LogStopFailed(
        ILogger logger,
        Exception exception,
        string reference
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Optio could not close the queries that an earlier instance left open"
    )]
    private static partial void LogRecoveryFailed(ILogger logger, Exception exception);
}
