using System.Diagnostics;
using RabbitMQ.Client;

namespace Optio.Tests.Feasibility;

/// <summary>
/// The feasibility test T1.7 of M37 (issue #48): fire and forget, in normal operation and with
/// a restart of the broker. It has its own broker, because it restarts it. Needs Docker.
/// </summary>
[Category("Feasibility")]
public sealed class StreamRestartTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(3);

    private StreamBroker _broker = null!;

    [OneTimeSetUp]
    public async Task StartAsync() => _broker = await StreamBroker.StartAsync();

    [OneTimeTearDown]
    public async Task StopAsync() => await _broker.DisposeAsync();

    [Test]
    public async Task No_message_is_lost_in_normal_operation()
    {
        await _broker.DeclareStreamAsync("t17n", "t17n.#");
        await using var admin = await _broker.ConnectAsync("admin");
        await using var reader = await StreamReader.OpenAsync(admin, "t17n", "next");

        await _broker.PublishAsync("t17n.x", 1000);

        var deliveries = await reader.ReadAsync(2000, Quiet);
        TestContext.Out.WriteLine($"T1.7 normal operation: {deliveries.Count} of 1000");
        Assert.That(deliveries.Select(d => d.Sequence).Distinct().Count(), Is.EqualTo(1000));
    }

    [Test]
    public async Task Restart_of_the_broker_during_a_publish_run()
    {
        const int Count = 20_000;
        await _broker.DeclareStreamAsync("t17r", "t17r.#");
        await using var consumerConnection = await _broker.ConnectAsync("admin", recovery: true);
        await using var reader = await StreamReader.OpenAsync(consumerConnection, "t17r", "first");
        await using var custos = await _broker.ConnectAsync("custos", recovery: true);
        await using var channel = await custos.CreateChannelAsync();

        var accepted = new List<int>();
        var failed = 0;
        using var stop = new CancellationTokenSource();
        var publish = Task.Run(async () =>
        {
            for (var i = 0; i < Count && !stop.IsCancellationRequested; i++)
            {
                try
                {
                    await StreamBroker.PublishOneAsync(channel, "t17r.x", i);
                    accepted.Add(i);
                }
                catch (Exception)
                {
                    // Fire and forget: the producer logs the error and continues.
                    failed++;
                }
                await Task.Delay(2);
            }
        });

        await Task.Delay(TimeSpan.FromSeconds(1));
        var restart = Stopwatch.StartNew();
        await _broker.RestartAsync();
        TestContext.Out.WriteLine($"T1.7 restart took {restart.Elapsed.TotalSeconds:F1} s");
        var beforeRecovery = accepted.Count;
        // The publish run continues for 10 seconds after the restart.
        await Task.Delay(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        await publish;
        await Task.Delay(TimeSpan.FromSeconds(1));

        // A new consumer reads the full stream after the restart.
        await using var check = await _broker.ConnectAsync("admin");
        await using var all = await StreamReader.OpenAsync(check, "t17r", "first");
        var stored = (await all.ReadAsync(Count * 2, Quiet)).Select(d => d.Sequence).ToHashSet();
        var lost = accepted.Count(i => !stored.Contains(i));
        TestContext.Out.WriteLine(
            $"T1.7 restart: {accepted.Count} publishes accepted by the client, {failed} refused "
                + $"by the client, {stored.Count} stored, {lost} accepted but not stored, "
                + $"{accepted.Count - beforeRecovery} accepted in the 10 s after the restart"
        );

        // The consumer that read during the restart.
        var seen = await reader.ReadAsync(Count * 3, Quiet);
        var offsets = seen.Select(d => d.Offset).ToList();
        var restartIndex = offsets.Zip(offsets.Skip(1)).TakeWhile(p => p.Second > p.First).Count();
        TestContext.Out.WriteLine(
            $"T1.7 recovered consumer: {seen.Count} deliveries, "
                + $"{seen.Select(d => d.Sequence).Distinct().Count()} distinct, "
                + $"offsets go back after delivery {restartIndex + 1} of {seen.Count}"
        );
        if (restartIndex + 1 < offsets.Count)
            TestContext.Out.WriteLine(
                $"T1.7 recovered consumer: last offset before the restart {offsets[restartIndex]}, "
                    + $"first offset after it {offsets[restartIndex + 1]}"
            );

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored, Is.Not.Empty);
            Assert.That(failed, Is.GreaterThan(0));
            // The publisher connection recovers and publishes again.
            Assert.That(accepted.Count, Is.GreaterThan(beforeRecovery));
            // The automatic recovery consumes again with the first arguments ("first").
            Assert.That(restartIndex + 1, Is.LessThan(offsets.Count));
            Assert.That(offsets[restartIndex + 1], Is.Zero);
        }
    }
}
