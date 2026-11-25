using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Optio.Telemetry;
using Optio.Tests.Infrastructure;
using static Optio.Tests.Infrastructure.FakeMembers;

namespace Optio.Tests;

/// <summary>Optio reads the events of the stream of a source from the broker. Needs Docker.
/// </summary>
public sealed class EventConsumerTests
{
    private EventBroker _broker = null!;

    // A broker for each test, because the consumer reads the stream from the start of the window.
    [SetUp]
    public async Task StartAsync() => _broker = await EventBroker.StartAsync();

    [TearDown]
    public async Task StopAsync() => await _broker.DisposeAsync();

    [Test]
    public async Task Event_from_the_broker_reaches_the_stream_and_an_invalid_event_is_ignored()
    {
        using var factory = new OptioFactory(stream: true, broker: _broker.OptioConnectionString);
        using var events = new MetricCollector<long>(
            factory.Services.GetRequiredService<OptioMetrics>().Meter,
            OptioMetrics.Events
        );
        using var stream = await OpenAsync(factory);

        await _broker.PublishAsync(
            EventBroker.Event("unknown", Guild, ReadRole, version: 2),
            Guild
        );
        await _broker.PublishAsync("not json", Guild);
        await _broker.PublishAsync(EventBroker.Event("first", Guild, ReadRole), Guild);

        Assert.That(await TextAsync(stream), Is.EqualTo("first"));
        Assert.That(
            events.GetMeasurementSnapshot().Select(m => m.Tags[OptioMetrics.OutcomeTag]),
            Is.EquivalentTo(new[] { "ignored", "ignored", "delivered" })
        );
    }

    [Test]
    public async Task Consumer_continues_after_its_last_offset_when_the_connection_fails()
    {
        using var factory = new OptioFactory(stream: true, broker: _broker.OptioConnectionString);
        using var stream = await OpenAsync(factory);
        await _broker.PublishAsync(EventBroker.Event("before", Guild, ReadRole), Guild);
        Assert.That(await TextAsync(stream), Is.EqualTo("before"));

        await _broker.CloseConnectionsAsync();
        await _broker.PublishAsync(EventBroker.Event("after", Guild, ReadRole), Guild);

        // The consumer connects again after about 1 s, with no duplicate of "before".
        Assert.That(await TextAsync(stream, TimeSpan.FromSeconds(30)), Is.EqualTo("after"));
    }

    /// <summary>Opens a stream when the consumer reads the broker: the first event that it reads
    /// appears in the hub.</summary>
    private static async Task<SseReader> OpenAsync(OptioFactory factory)
    {
        var client = factory.CreateMemberClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/stream");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return new SseReader(response);
    }

    private static async Task<string?> TextAsync(SseReader stream, TimeSpan? timeout = null)
    {
        while (await stream.NextAsync(timeout ?? TimeSpan.FromSeconds(20)) is { } item)
        {
            if (item.Data is { } data)
                return data.GetProperty("text").GetString();
        }
        return null;
    }
}
