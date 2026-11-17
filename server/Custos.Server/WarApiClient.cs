using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Custos.Server;

/// <summary>The current war, as the upload result gives it.</summary>
public sealed record WarInfo(string WarId, int WarNumber);

/// <summary>
/// Gets the current war from the Foxhole war API. A result is kept for five minutes. A failure gives
/// <c>null</c> and is kept for one minute, so that a down API does not slow every upload.
/// </summary>
internal sealed class WarApiClient(HttpClient http, IMemoryCache cache, IOptions<CustosOptions> options, ILogger<WarApiClient> logger)
{
    public const string CurrentWarPath = "/api/worldconquest/war";
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web);
    private sealed record ApiWar(string WarId, int WarNumber);

    public Task<WarInfo?> GetCurrentWarAsync(CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync("war:current", async entry =>
        {
            try
            {
                var war = await http.GetFromJsonAsync<ApiWar>(
                    CustosOptions.Combine(options.Value.WarApiBaseUrl, CurrentWarPath), ApiJson, cancellationToken);
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                return war is null ? null : new WarInfo(war.WarId, war.WarNumber);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
            {
                logger.LogWarning(e, "The Foxhole war API did not answer.");
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
                return null;
            }
        });
}
