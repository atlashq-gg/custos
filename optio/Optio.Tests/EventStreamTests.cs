using Microsoft.AspNetCore.Hosting;
using NATS.Client.JetStream.Models;
using Optio.Events;
using Optio.Tests.Infrastructure;

namespace Optio.Tests;

/// <summary>Optio makes the stream of the events on the broker (optio/docs/producers.md). Needs
/// Docker.</summary>
[NonParallelizable]
public sealed class EventStreamTests
{
    [Test]
    public async Task Optio_makes_the_stream_with_the_settings_of_the_contract()
    {
        await using var broker = await NatsBroker.StartAsync();
        using var factory = new OptioFactory(stream: true, broker: broker.Url("optio"));
        await factory.WaitForStreamAsync();

        var config = (await broker.InfoAsync(EventStream.Name)).Config;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.Subjects, Is.EqualTo(new[] { "events.>", "queries.>" }));
            Assert.That(config.Retention, Is.EqualTo(StreamConfigRetention.Limits));
            Assert.That(config.Storage, Is.EqualTo(StreamConfigStorage.File));
            Assert.That(config.Discard, Is.EqualTo(StreamConfigDiscard.Old));
            Assert.That(config.MaxAge, Is.EqualTo(TimeSpan.FromMinutes(15)));
            Assert.That(config.MaxBytes, Is.EqualTo(100_000_000));
            Assert.That(config.DuplicateWindow, Is.EqualTo(TimeSpan.FromMinutes(2)));
            Assert.That(config.AllowRollupHdrs, Is.False);
            Assert.That(config.DenyDelete, Is.True);
            Assert.That(config.DenyPurge, Is.True);
        }
    }

    [Test]
    public async Task Optio_sets_a_new_replay_window_on_the_stream_when_it_starts()
    {
        await using var broker = await NatsBroker.StartAsync();
        using (var first = new OptioFactory(stream: true, broker: broker.Url("optio")))
            await first.WaitForStreamAsync();
        using var factory = new OptioFactory(stream: true, broker: broker.Url("optio"));
        using var changed = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Optio:ReplayWindowMinutes", "20")
        );
        var events = (EventStream)changed.Services.GetService(typeof(EventStream))!;
        await WaitAsync(() => events.IsReady);

        var config = (await broker.InfoAsync(EventStream.Name)).Config;

        Assert.That(config.MaxAge, Is.EqualTo(TimeSpan.FromMinutes(20)));
    }

    [Test]
    public async Task Optio_starts_with_no_broker_and_makes_the_stream_when_the_broker_starts()
    {
        var port = NatsBroker.FreePort();
        using var factory = new OptioFactory(stream: true, broker: NatsBroker.Url("optio", port));
        var events = (EventStream)factory.Services.GetService(typeof(EventStream))!;
        Assert.That(events.IsReady, Is.False);

        await using var broker = await NatsBroker.StartAsync(port);

        // The wait between two tries uses the clock of the test.
        await WaitAsync(() =>
        {
            factory.Time.Advance(TimeSpan.FromSeconds(1));
            return events.IsReady;
        });
        Assert.That((await broker.InfoAsync(EventStream.Name)).Config.Name, Is.EqualTo("OPTIO"));
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; !condition(); i++)
        {
            Assert.That(i, Is.LessThan(300), "The condition did not become true.");
            await Task.Delay(100);
        }
    }
}
