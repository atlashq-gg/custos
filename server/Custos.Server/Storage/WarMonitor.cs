using Microsoft.Extensions.Options;

namespace Custos.Server.Storage;

/// <summary>
/// Asks the war API for the current war on an interval, and records it. This detects the next war
/// also when no guild sends an upload. <see cref="CustosOptions.WarPollIntervalSeconds"/> <c>0</c>
/// stops the monitor.
/// </summary>
internal sealed class WarMonitor(
    IServiceScopeFactory scopes,
    IOptions<CustosOptions> options,
    TimeProvider time,
    ILogger<WarMonitor> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.WarPollIntervalSeconds;
        if (interval == 0)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval), time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var war = await scope
                    .ServiceProvider.GetRequiredService<WarApiClient>()
                    .GetCurrentWarAsync(stoppingToken);
                if (war is not null)
                    await scope
                        .ServiceProvider.GetRequiredService<WarRegistry>()
                        .RecordAsync(war, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "The server could not record the current war.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
