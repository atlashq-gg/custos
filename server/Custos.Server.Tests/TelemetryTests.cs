using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using Custos.Server.Telemetry;
using Custos.Server.Tests.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Custos.Server.Tests;

public sealed class TelemetryTests
{
    private const string Path = "/api/v1/uploads";
    private static readonly byte[] SaveFile = MapDataWriter.File(
        new Pinned(
            "OriginHex",
            0.25f,
            0.75f,
            "StorageFacility",
            FakeWarApi.War142.StartedAt!.Value.AddDays(1),
            [("ItemCrates", "RifleC", 20)],
            []
        )
    );

    private CustosFactory _factory = null!;

    /// <summary>The meter of the server. The container disposes it.</summary>
    private Meter ServerMeter => _factory.Services.GetRequiredService<Meter>();

    [SetUp]
    public void SetUp() => _factory = new CustosFactory();

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public void Meter_has_the_name_and_the_version_of_the_server()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ServerMeter.Name, Is.EqualTo("Custos.Server"));
            Assert.That(ServerMeter.Version, Is.EqualTo(ServerVersion.Value));
        }
    }

    [Test]
    public async Task Upload_is_counted_by_outcome()
    {
        using var uploads = new MetricCollector<long>(ServerMeter, ServerMetrics.Uploads);
        using var parsed = new MetricCollector<double>(
            ServerMeter,
            ServerMetrics.UploadParseDuration
        );
        using var client = _factory.CreateAuthorizedClient();

        (await client.PostAsync(Path, Body(SaveFile, "application/octet-stream"))).Dispose();
        (await client.PostAsync(Path, Body([0x47], "application/octet-stream"))).Dispose();
        (
            await client.PostAsync(
                Path,
                Body(new byte[CustosFactory.UploadMaxBytes + 1], "application/octet-stream")
            )
        ).Dispose();

        Assert.That(
            uploads.GetMeasurementSnapshot().Select(m => m.Tags[ServerMetrics.OutcomeTag]),
            Is.EqualTo(new[] { "accepted", "not_map_data", "too_large" })
        );
        Assert.That(parsed.GetMeasurementSnapshot(), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task War_api_requests_are_counted_by_outcome()
    {
        using var answers = new MetricCollector<long>(ServerMeter, ServerMetrics.WarApiAnswers);
        var warApi = _factory.Services.GetRequiredService<WarApiClient>();
        var cache = _factory.Services.GetRequiredService<IMemoryCache>();

        _factory.WarApi.Fail = true;
        await warApi.GetCurrentWarAsync(CancellationToken.None);
        cache.Remove("war:current");
        _factory.WarApi.Fail = false;
        await warApi.GetCurrentWarAsync(CancellationToken.None);
        cache.Remove("war:current");
        _factory.WarApi.Fail = true;
        await warApi.GetCurrentWarAsync(CancellationToken.None);

        Assert.That(
            answers.GetMeasurementSnapshot().Select(m => m.Tags[ServerMetrics.OutcomeTag]),
            Is.EqualTo(new[] { "none", "fresh", "stale" })
        );
    }

    private static ByteArrayContent Body(byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }
}
