using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Optio.Harness;
using Optio.Queries;
using Optio.Telemetry;

namespace Optio.Storage;

/// <summary>The tokens of one request to the model for a query, for the audit and the
/// limits.</summary>
public sealed class QueryUsageRow
{
    public long Id { get; init; }
    public required DateTimeOffset At { get; init; }
    public required long GuildId { get; init; }
    public required long UserId { get; init; }

    /// <summary>The key of the client: a hash of the claim <c>sid</c>, not the claim.</summary>
    public required string ClientKey { get; init; }

    public required string QueryReference { get; init; }
    public required string Model { get; init; }

    /// <summary>The ID of the response message at the provider of the model, for example
    /// <c>msg_...</c> of Anthropic. Null when the provider gave none.</summary>
    public string? ExternalReference { get; init; }

    public required long InputTokens { get; init; }
    public required long OutputTokens { get; init; }
    public required long CacheReadTokens { get; init; }
    public required long CacheWriteTokens { get; init; }
}

/// <summary>
/// Records the token use of each request to the model: a row in the database, when Optio has
/// one, and the metric <see cref="OptioMetrics.ModelTokens"/>. A failure of the database does not
/// stop the query.
/// </summary>
internal sealed partial class UsageRecorder(
    IServiceScopeFactory scopes,
    OptioMetrics metrics,
    TimeProvider time,
    ILogger<UsageRecorder> logger
)
{
    public async Task RecordAsync(QueryRecord query, ModelUsage usage)
    {
        metrics.TokensUsed(query.Guild, usage);
        await using var scope = scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<OptioDbContext>() is not { } db)
            return;
        try
        {
            db.QueryUsage.Add(
                new QueryUsageRow
                {
                    At = time.GetUtcNow(),
                    GuildId = (long)query.Guild,
                    UserId = (long)query.User,
                    ClientKey = query.Client,
                    QueryReference = query.Reference,
                    Model = usage.Model,
                    ExternalReference = usage.MessageId,
                    InputTokens = usage.Input,
                    OutputTokens = usage.Output,
                    CacheReadTokens = usage.CacheRead,
                    CacheWriteTokens = usage.CacheWrite,
                }
            );
            // The row stays when the query stops: the model used the tokens.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            metrics.UsageNotRecorded();
            LogNotRecorded(logger, e, query.Reference);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "The token use of query {Reference} is not in the database"
    )]
    private static partial void LogNotRecorded(ILogger logger, Exception e, string reference);
}

/// <summary>Removes the usage rows older than <see cref="OptioOptions.UsageRetentionDays"/>, when
/// Optio starts and then each day.</summary>
internal sealed partial class UsagePurge(
    IServiceScopeFactory scopes,
    IOptions<OptioOptions> options,
    TimeProvider time,
    ILogger<UsagePurge> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1), time);
        do
        {
            try
            {
                await PurgeAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                LogPurgeFailed(logger, e);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Removes the old rows, and gives their number.</summary>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        var before = time.GetUtcNow().AddDays(-options.Value.UsageRetentionDays);
        await using var scope = scopes.CreateAsyncScope();
        return await scope
            .ServiceProvider.GetRequiredService<OptioDbContext>()
            .QueryUsage.Where(u => u.At < before)
            .ExecuteDeleteAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The removal of old usage rows failed")]
    private static partial void LogPurgeFailed(ILogger logger, Exception e);
}
