using NATS.Client.JetStream;
using Optio.Events;
using Optio.Tests.Infrastructure;

namespace Optio.Tests;

/// <summary>The permissions of <c>optio/nats/nats.conf</c> (optio/docs/producers.md). The
/// feasibility tests of M38 hold more cases. Needs Docker.</summary>
public sealed class NatsConfigTests
{
    private NatsBroker _broker = null!;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        _broker = await NatsBroker.StartAsync();
        await _broker.CreateStreamAsync(NatsBroker.StreamConfig());
    }

    [OneTimeTearDown]
    public async Task StopAsync() => await _broker.DisposeAsync();

    [Test]
    public async Task Producer_publishes_below_its_own_source() =>
        await _broker.PublishAsync("events.custos.1.stockpile.changed", "a"u8.ToArray());

    [Test]
    public async Task Producer_cannot_publish_below_another_source()
    {
        await using var custos = _broker.Connect("custos");

        Assert.That(
            async () =>
                await new NatsJSContext(custos).PublishAsync(
                    "events.other.1.test",
                    "a"u8.ToArray()
                ),
            Throws.InstanceOf<NatsJSPublishNoResponseException>()
        );
    }

    [Test]
    public async Task Producer_cannot_use_the_stream()
    {
        await using var custos = _broker.Connect("custos");

        Assert.That(
            async () => await new NatsJSContext(custos).GetStreamAsync(EventStream.Name),
            Throws.InstanceOf<NatsJSApiNoResponseException>()
        );
    }

    [Test]
    public async Task Optio_cannot_publish_an_event()
    {
        await using var optio = _broker.Connect("optio");

        Assert.That(
            async () =>
                await new NatsJSContext(optio).PublishAsync(
                    "events.custos.1.stockpile.changed",
                    "a"u8.ToArray()
                ),
            Throws.InstanceOf<NatsJSPublishNoResponseException>()
        );
    }

    [Test]
    public async Task Producer_cannot_publish_a_query_message()
    {
        await using var custos = _broker.Connect("custos");

        Assert.That(
            async () =>
                await new NatsJSContext(custos).PublishAsync("queries.1.2.abc", "a"u8.ToArray()),
            Throws.InstanceOf<NatsJSPublishNoResponseException>()
        );
    }
}
