using System.Net;
using Custos.Server.Discord;
using Custos.Server.Telemetry;
using Custos.Server.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using NetCord.Gateway;
using NetCord.Rest;

namespace Custos.Server.Tests.Discord;

public sealed class DiscordSetupTests
{
    [Test]
    public void Without_a_token_the_server_has_no_discord_connection()
    {
        using var factory = new CustosFactory();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory.Services.GetService<GatewayClient>(), Is.Null);
            Assert.That(factory.Services.GetService<IDiscordDirectory>(), Is.Null);
        }
    }

    [TestCase(HttpStatusCode.OK, "ok")]
    [TestCase(HttpStatusCode.NotFound, "client_error")]
    [TestCase(HttpStatusCode.TooManyRequests, "rate_limited")]
    [TestCase(HttpStatusCode.BadGateway, "server_error")]
    public async Task Discord_requests_are_measured_by_outcome(
        HttpStatusCode status,
        string outcome
    )
    {
        using var meter = CustosTelemetry.CreateMeter();
        using var collector = new MetricCollector<double>(
            meter,
            ServerMetrics.DiscordRequestDuration
        );
        using var handler = new MeteredRestRequestHandler(
            new FixedRestHandler(status),
            new ServerMetrics(meter)
        );

        using var response = await handler.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/guilds/1")
        );

        Assert.That(
            collector.GetMeasurementSnapshot().Single().Tags[ServerMetrics.OutcomeTag],
            Is.EqualTo(outcome)
        );
    }

    private sealed class FixedRestHandler(HttpStatusCode status) : IRestRequestHandler
    {
        public Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new HttpResponseMessage(status));

        public void AddDefaultHeader(string name, IEnumerable<string> values) { }

        public void Dispose() { }
    }
}
