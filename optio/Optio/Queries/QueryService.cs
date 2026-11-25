using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Optio.Events;
using Optio.Harness;
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
/// publishes its message. A client has one open query, and a user has one open query. At the
/// start, Optio closes each query that a stopped instance left open. At a normal stop, it closes
/// its running queries. Both get the cause <c>interrupted</c>.
/// </summary>
internal sealed partial class QueryService(
    QueryStore store,
    QueryHarness harness,
    EventStream events,
    OptioMetrics metrics,
    IOptions<OptioOptions> options,
    TimeProvider time,
    ILogger<QueryService> logger
) : BackgroundService
{
    /// <summary>The time for a closing message at a stop of Optio.</summary>
    private static readonly TimeSpan StopTime = TimeSpan.FromSeconds(5);

    /// <summary>The instance of Optio. A record of another owner at the start is
    /// interrupted.</summary>
    private readonly string _owner = Guid.NewGuid().ToString("N");

    private readonly ConcurrentDictionary<string, Run> _runs = new();
    private readonly SemaphoreSlim[] _locks =
    [
        .. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)),
    ];
    private readonly Dictionary<ulong, Queue<DateTimeOffset>> _starts = [];

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
            if (Wait(client.User) is { } wait)
                return new QueryOutcome(
                    StatusCodes.Status429TooManyRequests,
                    Problem: "You sent too many queries. Wait, then ask again.",
                    RetryAfter: wait
                );
            if (!await store.ClaimUserAsync(client, cancellationToken))
                return new QueryOutcome(
                    StatusCodes.Status409Conflict,
                    Problem: "Another Custos of yours has an open query. Wait for its end."
                );

            if (record is { IsOpen: true })
            {
                var replaced = await store.ChangeAsync(
                    client.Key,
                    record.Reference,
                    r =>
                        r.IsOpen
                            ? r.Close(QueryStates.Replaced, "A new query replaced this query.")
                            : null,
                    cancellationToken
                );
                if (replaced is not null)
                    metrics.QueryClosed(QueryStates.Replaced);
                Cancel(client.Key, record.Reference);
            }

            var now = time.GetUtcNow();
            // The budget ends before the token, because the tools use it.
            var end = Min(now + options.Value.PartBudget, tokenExpiry - TimeSpan.FromSeconds(5));
            var query = new QueryRecord(
                request.Reference,
                client.Key,
                client.Guild,
                client.User,
                request.Text,
                request.NewConversation,
                QueryStates.Working,
                null,
                end + QueryHarness.LastStep + TimeSpan.FromSeconds(5),
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
            Count(client.User);
            await store.PublishAsync(query, cancellationToken);
            Start(client, query, new ToolUser(accessToken, client.Guild, client.User), end);
            return new QueryOutcome(StatusCodes.Status202Accepted, query);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Applies an action of spec/06-query.md to the query of the client.</summary>
    public async Task<QueryOutcome> ActAsync(
        QueryClient client,
        string reference,
        string action,
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
        if (record.Cause == CauseOf(action))
            return new QueryOutcome(StatusCodes.Status200OK, record);

        if (action == "stop" && record.State is QueryStates.Working or QueryStates.Waiting)
        {
            var stopped = await store.ChangeAsync(
                client.Key,
                reference,
                r =>
                    r.State is QueryStates.Working or QueryStates.Waiting
                        ? r.Close(QueryStates.Stopped, "You stopped the query.")
                        : null,
                cancellationToken
            );
            if (stopped is not null)
            {
                metrics.QueryClosed(QueryStates.Stopped);
                Cancel(client.Key, reference);
                await store.ReleaseUserAsync(client, cancellationToken);
                return new QueryOutcome(StatusCodes.Status200OK, stopped);
            }
            (record, _) = await store.GetAsync(client.Key, cancellationToken);
        }
        return new QueryOutcome(
            StatusCodes.Status409Conflict,
            record,
            $"The query is {record?.State ?? "gone"}. It does not permit this action."
        );
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

    /// <summary>Closes the queries that an instance of Optio left open. The broker can be
    /// unavailable at the start, so this tries again after a wait.</summary>
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
                    if (record.IsOpen && record.Owner != _owner)
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

    /// <summary>Closes the running queries with the cause <c>interrupted</c>.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
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
                    QueryStates.Closed => null,
                    QueryStates.Applying => r.Close(
                        QueryStates.ResultUnknown,
                        "The service stopped during the change. Its result is not known."
                    ),
                    _ => r.Close(
                        QueryStates.Interrupted,
                        "The service stopped during the query. Ask again."
                    ),
                },
            cancellationToken
        );
        if (closed is null)
            return;
        metrics.QueryClosed(closed.Cause!);
        await store.ReleaseUserAsync(
            new QueryClient(record.Client, record.Guild, record.User),
            cancellationToken
        );
    }

    /// <summary>Runs a query in the background until its end, its stop, or the stop of
    /// Optio.</summary>
    private void Start(QueryClient client, QueryRecord query, ToolUser user, DateTimeOffset end)
    {
        var run = new Run(client, query.Reference, new CancellationTokenSource());
        _runs[client.Key] = run;
        _ = Task.Run(
            async () =>
            {
                var token = run.Cancellation.Token;
                try
                {
                    var history = query.NewConversation
                        ? []
                        : (await store.GetConversationAsync(client.Key, token))?.Messages().ToList()
                            ?? [];
                    var result = await harness.RunAsync(
                        query,
                        user,
                        history,
                        end,
                        text =>
                            store.ChangeAsync(
                                client.Key,
                                query.Reference,
                                r => r.State == QueryStates.Working ? r with { Text = text } : null,
                                token
                            ),
                        token
                    );
                    if (await CloseAsync(client, query, QueryStates.Answer, result.Answer, token))
                        await store.AddExchangeAsync(
                            client.Key,
                            query.NewConversation,
                            Exchange.Of(result.Messages),
                            token
                        );
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // A stop, a new query, or the stop of Optio closed the query.
                }
                catch (QueryFailedException e)
                {
                    await CloseAsync(client, query, QueryStates.Failed, e.Message, token);
                }
                catch (Exception e)
                {
                    LogQueryFailed(logger, e, query.Reference);
                    try
                    {
                        await CloseAsync(
                            client,
                            query,
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

    /// <summary>Closes a working query. False when another change closed it first.</summary>
    private async Task<bool> CloseAsync(
        QueryClient client,
        QueryRecord query,
        string cause,
        string text,
        CancellationToken cancellationToken
    )
    {
        var closed = await store.ChangeAsync(
            client.Key,
            query.Reference,
            r => r.State == QueryStates.Working ? r.Close(cause, text) : null,
            cancellationToken
        );
        if (closed is null)
            return false;
        metrics.QueryClosed(cause);
        await store.ReleaseUserAsync(client, cancellationToken);
        return true;
    }

    private void Cancel(string client, string reference)
    {
        if (_runs.TryGetValue(client, out var run) && run.Reference == reference)
            run.Cancellation.Cancel();
    }

    /// <summary>The wait until the user can start a query, or null.</summary>
    private TimeSpan? Wait(ulong user)
    {
        var now = time.GetUtcNow();
        lock (_starts)
        {
            if (!_starts.TryGetValue(user, out var starts))
                return null;
            while (starts.TryPeek(out var first) && first <= now - TimeSpan.FromHours(1))
                starts.Dequeue();
            return starts.Count >= options.Value.MaxQueriesPerHour
                ? starts.Peek() + TimeSpan.FromHours(1) - now
                : null;
        }
    }

    private void Count(ulong user)
    {
        lock (_starts)
        {
            if (!_starts.TryGetValue(user, out var starts))
                _starts[user] = starts = new();
            starts.Enqueue(time.GetUtcNow());
        }
    }

    private static string? CauseOf(string action) =>
        action switch
        {
            "stop" => QueryStates.Stopped,
            "refuse" => QueryStates.Refused,
            _ => null,
        };

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private sealed record Run(
        QueryClient Client,
        string Reference,
        CancellationTokenSource Cancellation
    );

    [LoggerMessage(Level = LogLevel.Warning, Message = "The query {Reference} failed")]
    private static partial void LogQueryFailed(
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
