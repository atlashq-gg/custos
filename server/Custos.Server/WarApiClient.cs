using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Custos.Server;

/// <summary>
/// The current war from the Foxhole war API. <see cref="StartedAt"/> is the start of the conquest, or <c>null</c>
/// if the war API does not give it yet.
/// </summary>
public sealed record CurrentWar(Guid WarId, int WarNumber, DateTimeOffset? StartedAt);

/// <summary>
/// Gets the current war from the Foxhole war API. A result is fresh for five minutes. When the API does not
/// answer, the client gives the last answer that it got, and tries the API again after one minute. Without an
/// earlier answer, it gives <c>null</c>.
/// </summary>
internal sealed class WarApiClient(HttpClient http, IMemoryCache cache, IOptions<CustosOptions> options, ILogger<WarApiClient> logger)
{
    public const string CurrentWarPath = "/api/worldconquest/war";
    private const string FreshKey = "war:current";
    private const string LastKey = "war:last";
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web);
    private sealed record ApiWar(Guid WarId, int WarNumber, long? ConquestStartTime);

    public Task<CurrentWar?> GetCurrentWarAsync(CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(FreshKey, async entry =>
        {
            try
            {
                var war = await http.GetFromJsonAsync<ApiWar>(
                    CustosOptions.Combine(options.Value.WarApiBaseUrl, CurrentWarPath), ApiJson, cancellationToken);
                if (war is null) throw new JsonException("The war API answered null.");

                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                var current = new CurrentWar(war.WarId, war.WarNumber,
                    war.ConquestStartTime is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null);
                cache.Set(LastKey, current);
                return current;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
            {
                logger.LogWarning(e, "The Foxhole war API did not answer.");
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
                return cache.Get<CurrentWar>(LastKey);
            }
        });
}
