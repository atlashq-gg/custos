using System.Net.Http.Json;
using System.Text.Json;
using Custos.Server.Telemetry;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace Custos.Server;

/// <summary>
/// The current war from the Foxhole war API. <see cref="StartedAt"/> is the start of the conquest,
/// or <c>null</c> if the war API does not give it yet.
/// </summary>
public sealed record CurrentWar(Guid WarId, int WarNumber, DateTimeOffset? StartedAt);

/// <summary>
/// Gets the current war from the Foxhole war API. A result is fresh for five minutes. When the API
/// does not answer, the client gives the last answer that it got, and tries the API again after one
/// minute. Without an earlier answer, it gives <c>null</c>.
/// </summary>
internal sealed partial class WarApiClient(
    HttpClient http,
    IMemoryCache cache,
    IOptions<CustosOptions> options,
    ServerMetrics metrics,
    ILogger<WarApiClient> logger
)
{
    public const string CurrentWarPath = "/api/worldconquest/war";
    private const string FreshKey = "war:current";
    private const string LastKey = "war:last";
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web);

    private sealed record ApiWar(Guid WarId, int WarNumber, long? ConquestStartTime);

    /// <summary>
    /// The standard resilience handler, with shorter times. An upload waits for this client, and
    /// Custos stops to wait for an upload after 60 seconds. With these values, the client gives
    /// its answer in 15 seconds or less.
    /// </summary>
    public static void Resilience(HttpStandardResilienceOptions resilience)
    {
        resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
        resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(15);
        resilience.Retry.MaxRetryAttempts = 2;
        resilience.Retry.Delay = TimeSpan.FromSeconds(1);
    }

    public Task<CurrentWar?> GetCurrentWarAsync(CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(FreshKey, async entry =>
        {
            try
            {
                var war = await http.GetFromJsonAsync<ApiWar>(
                    CustosOptions.Combine(options.Value.WarApiBaseUrl, CurrentWarPath),
                    ApiJson,
                    cancellationToken
                );
                if (war is null)
                    throw new JsonException("The war API answered null.");

                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                var current = new CurrentWar(
                    war.WarId,
                    war.WarNumber,
                    war.ConquestStartTime is { } ms
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                        : null
                );
                cache.Set(LastKey, current);
                metrics.WarApiAnswer(WarApiOutcome.Fresh);
                return current;
            }
            catch (Exception e)
                when (e
                        is HttpRequestException
                            or JsonException
                            or TaskCanceledException
                            or ExecutionRejectedException
                )
            {
                LogNoAnswer(logger, e);
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
                var last = cache.Get<CurrentWar>(LastKey);
                metrics.WarApiAnswer(last is null ? WarApiOutcome.None : WarApiOutcome.Stale);
                return last;
            }
        });

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Foxhole war API did not answer.")]
    private static partial void LogNoAnswer(ILogger logger, Exception exception);
}
