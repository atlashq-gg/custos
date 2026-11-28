using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Custos.SystemTests;

/// <summary>
/// Starts the AppHost of the full system one time for all tests of this assembly, and stops it
/// after them. The parameters of outside accounts are empty: the system starts without Discord
/// and without the model provider (ADR 0008).
/// </summary>
[SetUpFixture]
public sealed class FullSystem
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The resources that the tests need, in the order of their start.</summary>
    private static readonly string[] Services = ["keycloak", "server", "optio"];

    private static DistributedApplication? _app;

    /// <summary>The running system. Only valid inside a test.</summary>
    public static DistributedApplication App =>
        _app ?? throw new InvalidOperationException("The full system is not started.");

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        using var timeout = new CancellationTokenSource(StartTimeout);
        // The ports stay fixed: each service compares the iss claim with the URL of the realm on
        // its fixed port. Thus the tests cannot run while the AppHost runs. The empty values
        // replace the user secrets of the developer.
        var builder =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.Custos_AppHost>(
                [
                    "DcpPublisher:RandomizePorts=false",
                    "Parameters:discord-token=",
                    "Parameters:discord-user-id=",
                    "Parameters:optio-discord-token=",
                    "Parameters:claude-api-key=",
                ],
                timeout.Token
            );
        var startLog = new StartLog();
        builder.Services.AddLogging(logging =>
            logging
                .AddProvider(startLog)
                .SetMinimumLevel(LogLevel.Information)
                .AddFilter("Microsoft", LogLevel.Warning)
                .AddFilter("Polly", LogLevel.Warning)
        );
        _app = await builder.BuildAsync(timeout.Token);

        try
        {
            await _app.StartAsync(timeout.Token);
            await Task.WhenAll(
                Services.Select(name =>
                    _app.ResourceNotifications.WaitForResourceHealthyAsync(name, timeout.Token)
                )
            );
        }
        catch
        {
            await startLog.WriteToAsync(TestContext.Progress);
            throw;
        }
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}
