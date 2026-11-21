using Custos.Server.Telemetry;
using NetCord.Rest;

namespace Custos.Server.Discord;

/// <summary>
/// Measures each request of the bot to the REST API of Discord: the duration and the outcome, with
/// the <c>429</c> and the <c>5xx</c> answers. NetCord handles a <c>429</c> itself.
/// </summary>
internal sealed class MeteredRestRequestHandler(IRestRequestHandler inner, ServerMetrics metrics)
    : IRestRequestHandler
{
    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default
    )
    {
        var started = TimeProvider.System.GetTimestamp();
        try
        {
            var response = await inner.SendAsync(request, cancellationToken);
            metrics.DiscordRequest(
                TimeProvider.System.GetElapsedTime(started),
                (int)response.StatusCode
            );
            return response;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            metrics.DiscordRequest(TimeProvider.System.GetElapsedTime(started), null);
            throw;
        }
    }

    public void AddDefaultHeader(string name, IEnumerable<string> values) =>
        inner.AddDefaultHeader(name, values);

    public void Dispose() => inner.Dispose();
}
