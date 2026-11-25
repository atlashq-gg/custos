using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Client.KeyValueStore;
using Optio.Tests.Infrastructure;

namespace Optio.Tests.Feasibility;

/// <summary>
/// The feasibility tests M38 of issue #48: NATS JetStream for the event path, with NATS.Net and
/// <c>optio/nats/nats.conf</c>. Each test writes its measurements to the test output. The
/// findings are in a comment on #48.
/// </summary>
[Category("Feasibility")]
[NonParallelizable]
public sealed class NatsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Test 1: a host makes its connection with no broker. Nothing waits for the broker
    /// except the publisher, and the publisher continues when the broker starts.</summary>
    [Test]
    public async Task Publisher_starts_with_no_broker_and_continues_when_the_broker_starts()
    {
        var port = NatsBroker.FreePort();
        var clock = Stopwatch.StartNew();
        await using var connection = new NatsConnection(
            new NatsOpts
            {
                Url = NatsBroker.Url("custos", port),
                Name = "custos",
                InboxPrefix = "_INBOX.custos",
                RetryOnInitialConnect = true,
            }
        );
        var js = new NatsJSContext(connection);
        var made = clock.Elapsed;

        var queue = Channel.CreateUnbounded<int>();
        var outcomes = Channel.CreateUnbounded<string>();
        using var stop = new CancellationTokenSource();
        var publisher = Task.Run(async () =>
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stop.Token))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    var ack = await js.PublishAsync(
                        $"events.custos.1.test",
                        Encoding.UTF8.GetBytes(item.ToString()),
                        opts: new NatsJSPubOpts { MsgId = item.ToString() },
                        cancellationToken: timeout.Token
                    );
                    ack.EnsureSuccess();
                    await outcomes.Writer.WriteAsync($"published {ack.Seq}");
                }
                catch (Exception e)
                {
                    await outcomes.Writer.WriteAsync($"failed {e.GetType().Name}");
                }
            }
        });

        clock.Restart();
        Assert.That(queue.Writer.TryWrite(1), Is.True);
        var queued = clock.Elapsed;
        var first = await outcomes.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        var stateWithout = connection.ConnectionState;

        await using var broker = await NatsBroker.StartAsync(port);
        await broker.CreateStreamAsync(NatsBroker.StreamConfig());
        clock.Restart();
        queue.Writer.TryWrite(2);
        var second = await outcomes.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        var afterStart = clock.Elapsed;
        await stop.CancelAsync();

        Report(
            $"connection made in {made.TotalMilliseconds:F0} ms, event queued in "
                + $"{queued.TotalMilliseconds:F1} ms; with no broker: {first}, state "
                + $"{stateWithout}; after the broker started: {second} in "
                + $"{afterStart.TotalMilliseconds:F0} ms"
        );
        using (Assert.EnterMultipleScope())
        {
            Assert.That(made, Is.LessThan(TimeSpan.FromSeconds(1)));
            Assert.That(first, Does.StartWith("failed"));
            Assert.That(second, Does.StartWith("published"));
        }
    }

    /// <summary>Test 1, the default: with <c>RetryOnInitialConnect</c> off, a publish with no
    /// broker throws at once. The next use connects again.</summary>
    [Test]
    public async Task Without_retry_of_the_first_connect_a_publish_with_no_broker_throws_at_once()
    {
        var port = NatsBroker.FreePort();
        await using var connection = new NatsConnection(
            new NatsOpts { Url = NatsBroker.Url("custos", port), InboxPrefix = "_INBOX.custos" }
        );
        var js = new NatsJSContext(connection);
        var clock = Stopwatch.StartNew();
        var first = await OutcomeAsync(() =>
            js.PublishAsync("events.custos.1.test", "a"u8.ToArray())
        );
        var firstTime = clock.Elapsed;

        await using var broker = await NatsBroker.StartAsync(port);
        await broker.CreateStreamAsync(NatsBroker.StreamConfig());
        var second = await OutcomeAsync(() =>
            js.PublishAsync("events.custos.1.test", "b"u8.ToArray())
        );

        Report(
            $"with no broker: {first} in {firstTime.TotalMilliseconds:F0} ms; after the broker "
                + $"started: {second}"
        );
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Does.StartWith("failed"));
            Assert.That(firstTime, Is.LessThan(TimeSpan.FromSeconds(1)));
            Assert.That(second, Does.StartWith("ok"));
        }
    }

    /// <summary>Test 2: the permissions of <c>nats.conf</c> for the producer.</summary>
    [Test]
    public async Task Producer_publishes_only_below_its_source_and_cannot_read_or_change()
    {
        await using var broker = await NatsBroker.StartAsync();
        await broker.CreateStreamAsync(NatsBroker.StreamConfig());
        await using var custos = broker.Connect("custos");
        var js = new NatsJSContext(custos);

        var own = await OutcomeAsync(() =>
            js.PublishAsync("events.custos.1.test", "a"u8.ToArray())
        );
        var other = await OutcomeAsync(() =>
            js.PublishAsync("events.other.1.test", "b"u8.ToArray())
        );
        var query = await OutcomeAsync(() => js.PublishAsync("queries.1.2.3", "c"u8.ToArray()));
        var info = await OutcomeAsync(() => js.GetStreamAsync(NatsBroker.Stream));
        var delete = await OutcomeAsync(() => js.DeleteStreamAsync(NatsBroker.Stream));
        // The client makes an ordered consumer at the first read, not at its creation.
        var consumer = await OutcomeAsync(async () =>
            (await ReadAsync(js, ["events.>"], 1, 1, 3)).Count
        );
        // A core publish of admin, with no reply subject. The stream also stores it.
        var read = await ReceivesAsync(custos, "events.>", "events.custos.1.test");
        var stream = await broker.InfoAsync();

        Report(
            $"own source: {own}; other source: {other}; query subject: {query}; stream info: "
                + $"{info}; delete: {delete}; consumer: {consumer}; core subscribe to events.>: "
                + $"{read}; messages in the stream: {stream.State.Messages}"
        );
        using (Assert.EnterMultipleScope())
        {
            Assert.That(own, Does.StartWith("ok"));
            Assert.That(other, Does.StartWith("failed"));
            Assert.That(query, Does.StartWith("failed"));
            Assert.That(info, Does.StartWith("failed"));
            Assert.That(delete, Does.StartWith("failed"));
            Assert.That(consumer, Does.StartWith("failed").Or.EqualTo("ok 0"));
            Assert.That(read, Is.False);
            Assert.That(stream.State.Messages, Is.EqualTo(2));
        }
    }

    /// <summary>Test 2: each user gets only its own replies, and Optio cannot publish an
    /// event or read with a core subscription.</summary>
    [Test]
    public async Task Each_user_reads_only_its_own_reply_subjects()
    {
        await using var broker = await NatsBroker.StartAsync();
        await broker.CreateStreamAsync(NatsBroker.StreamConfig());
        await using var custos = broker.Connect("custos");
        await using var optio = broker.Connect("optio");

        var custosSeesOptio = ReceivesAsync(custos, "_INBOX.optio.>", null);
        var optioSeesCustos = ReceivesAsync(optio, "_INBOX.custos.>", null);
        await Task.Delay(300);
        await new NatsJSContext(optio).GetStreamAsync(NatsBroker.Stream);
        await new NatsJSContext(custos).PublishAsync("events.custos.1.test", "a"u8.ToArray());
        var optioPublish = await OutcomeAsync(() =>
            new NatsJSContext(optio).PublishAsync("events.custos.1.test", "b"u8.ToArray())
        );
        var optioRead = await ReceivesAsync(optio, "events.>", "events.custos.1.test");

        Report(
            $"custos got a reply of optio: {await custosSeesOptio}; optio got a reply of custos: "
                + $"{await optioSeesCustos}; optio publishes an event: {optioPublish}; optio core "
                + $"subscribe to events.>: {optioRead}"
        );
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await custosSeesOptio, Is.False);
            Assert.That(await optioSeesCustos, Is.False);
            Assert.That(optioPublish, Does.StartWith("failed"));
            Assert.That(optioRead, Is.False);
        }
    }

    /// <summary>Test 2: the smallest JetStream API set of Optio. The work of Optio runs once with
    /// the list of the file, and once without each entry.</summary>
    [Test]
    public async Task Optio_needs_each_JetStream_API_subject_of_the_file()
    {
        await using var broker = await NatsBroker.StartAsync();
        var full = await OptioWorkAsync(broker);
        Report($"all entries: {full}");
        Assert.That(full, Is.EqualTo("ok"));

        var entries = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "nats", "nats.conf"))
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("\"$JS.API.", StringComparison.Ordinal))
            .ToList();
        var unused = new List<string>();
        foreach (var entry in entries)
        {
            await broker.ReloadAsync(
                NatsBroker.Config(c => c.Replace(entry + "\n", "", StringComparison.Ordinal))
            );
            var outcome = await OptioWorkAsync(broker);
            Report($"without {entry}: {outcome}");
            if (outcome == "ok")
                unused.Add(entry);
        }
        await broker.ReloadAsync(NatsBroker.Config());
        Assert.That(unused, Is.Empty);
    }

    /// <summary>Test 3: the producer sees a missing stream and a missing acknowledgement.</summary>
    [Test]
    public async Task Producer_sees_a_missing_stream_and_a_missing_acknowledgement()
    {
        await using var broker = await NatsBroker.StartAsync();
        await using var custos = broker.Connect("custos");
        var js = new NatsJSContext(custos);

        var clock = Stopwatch.StartNew();
        var noStream = await OutcomeAsync(() =>
            js.PublishAsync("events.custos.1.test", "a"u8.ToArray())
        );
        var noStreamTime = clock.Elapsed;

        await broker.CreateStreamAsync(NatsBroker.StreamConfig());
        await js.PublishAsync("events.custos.1.test", "b"u8.ToArray());
        await broker.PauseAsync();
        clock.Restart();
        string noAck;
        try
        {
            noAck = await OutcomeAsync(() =>
                js.PublishAsync("events.custos.1.test", "c"u8.ToArray())
            );
        }
        finally
        {
            await broker.UnpauseAsync();
        }
        var noAckTime = clock.Elapsed;

        Report(
            $"no stream: {noStream} in {noStreamTime.TotalMilliseconds:F0} ms; no "
                + $"acknowledgement: {noAck} in {noAckTime.TotalMilliseconds:F0} ms; default "
                + $"publish retries: {new NatsJSPubOpts().RetryAttempts}"
        );
        using (Assert.EnterMultipleScope())
        {
            Assert.That(noStream, Does.StartWith("failed"));
            Assert.That(noAck, Does.StartWith("failed"));
        }
    }

    /// <summary>Test 3: what a restart of the broker loses. A publisher publishes one event
    /// after the other while the broker restarts, gracefully or with SIGKILL.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task Restart_of_the_broker_loses_no_acknowledged_event(bool kill)
    {
        await using var broker = await NatsBroker.StartAsync();
        await broker.CreateStreamAsync(NatsBroker.StreamConfig());
        await using var custos = broker.Connect("custos", o =>
            o with
            {
                ReconnectWaitMin = TimeSpan.FromMilliseconds(100),
            }
        );
        var js = new NatsJSContext(custos);

        var acked = new HashSet<string>();
        var failed = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var publisher = Task.Run(async () =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    var ack = await js.PublishAsync(
                        "events.custos.1.test",
                        Encoding.UTF8.GetBytes(i.ToString()),
                        opts: new NatsJSPubOpts { MsgId = i.ToString() },
                        cancellationToken: timeout.Token
                    );
                    ack.EnsureSuccess();
                    acked.Add(i.ToString());
                }
                catch (Exception)
                {
                    failed++;
                }
            }
        });

        await Task.Delay(TimeSpan.FromSeconds(3));
        var clock = Stopwatch.StartNew();
        if (kill)
            await broker.KillAndStartAsync();
        else
            await broker.RestartAsync();
        var down = clock.Elapsed;
        await publisher;

        var stored = await StoredIdsAsync(broker);
        var lost = acked.Except(stored).Count();
        Report(
            $"kill: {kill}; restart took {down.TotalMilliseconds:F0} ms; acknowledged "
                + $"{acked.Count}, failed {failed}, stored {stored.Count}, acknowledged but not "
                + $"stored {lost}, stored but not acknowledged {stored.Except(acked).Count()}"
        );
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed, Is.GreaterThan(0));
            Assert.That(lost, Is.Zero);
        }
    }

    /// <summary>Test 4: one ordered consumer with two filters gives the messages of both in the
    /// order of the stream.</summary>
    [Test]
    public async Task Reader_with_two_filters_gives_one_order_and_starts_at_a_position()
    {
        await using var broker = await NatsBroker.StartAsync();
        await broker.CreateStreamAsync(NatsBroker.StreamConfig());
        await using var admin = broker.Connect("admin");
        var js = new NatsJSContext(admin);
        string[] subjects =
        [
            "events.custos.1.stockpile.changed",
            "events.custos.2.stockpile.changed",
            "queries.1.7.s1",
            "queries.1.7.s2",
            "events.other.1.thing",
            "queries.1.7.s1",
        ];
        foreach (var subject in subjects)
            await js.PublishAsync(subject, Encoding.UTF8.GetBytes(subject));
        string[] filters = ["events.*.1.>", "queries.1.7.s1"];

        var all = await ReadAsync(js, filters, 1, 4);
        var fromThree = await ReadAsync(js, filters, 3, 3);
        Report($"from 1: {string.Join(", ", all)}; from 3: {string.Join(", ", fromThree)}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(all.Select(m => m.Sequence), Is.EqualTo(new ulong[] { 1, 3, 5, 6 }));
            Assert.That(fromThree.Select(m => m.Sequence), Is.EqualTo(new ulong[] { 3, 5, 6 }));
        }
    }

    /// <summary>Test 4: a start after the end waits for that sequence and skips the messages
    /// before it. Thus Optio starts such a position from now.</summary>
    [Test]
    public async Task Reader_that_starts_after_the_end_skips_the_messages_before_its_start()
    {
        await using var broker = await NatsBroker.StartAsync();
        await broker.CreateStreamAsync(NatsBroker.StreamConfig());
        await using var admin = broker.Connect("admin");
        var js = new NatsJSContext(admin);
        await js.PublishAsync("events.custos.1.test", "1"u8.ToArray());

        var reading = ReadAsync(js, ["events.>"], 4, 2);
        await Task.Delay(500);
        for (var i = 2; i <= 5; i++)
            await js.PublishAsync("events.custos.1.test", Encoding.UTF8.GetBytes($"{i}"));
        var read = await reading;

        Report($"start 4 with last 1, then 4 more: {string.Join(", ", read)}");
        Assert.That(read.Select(m => m.Sequence), Is.EqualTo(new ulong[] { 4, 5 }));
    }

    /// <summary>Test 4: a start before the oldest message gives the oldest message.</summary>
    [Test]
    public async Task Reader_that_starts_before_the_oldest_message_gets_the_oldest()
    {
        await using var broker = await NatsBroker.StartAsync();
        var config = NatsBroker.StreamConfig();
        await broker.CreateStreamAsync(config with { MaxMsgs = 3, DenyPurge = false });
        await using var admin = broker.Connect("admin");
        var js = new NatsJSContext(admin);
        for (var i = 1; i <= 6; i++)
            await js.PublishAsync("events.custos.1.test", Encoding.UTF8.GetBytes($"{i}"));

        var read = await ReadAsync(js, ["events.>"], 1, 3);

        Report($"start 1, oldest 4: {string.Join(", ", read)}");
        Assert.That(read.Select(m => m.Sequence), Is.EqualTo(new ulong[] { 4, 5, 6 }));
    }

    /// <summary>Test 4: an ordered consumer continues after a restart of the broker, with no
    /// duplicate and no gap.</summary>
    [Test]
    public async Task Reader_continues_after_a_restart_of_the_broker_with_no_duplicate()
    {
        await using var broker = await NatsBroker.StartAsync();
        await broker.CreateStreamAsync(NatsBroker.StreamConfig());
        await using var optio = broker.Connect("optio", o =>
            o with
            {
                ReconnectWaitMin = TimeSpan.FromMilliseconds(100),
            }
        );
        await using var admin = broker.Connect("admin");
        var js = new NatsJSContext(admin);
        for (var i = 1; i <= 3; i++)
            await js.PublishAsync("events.custos.1.test", Encoding.UTF8.GetBytes($"{i}"));

        var reading = ReadAsync(new NatsJSContext(optio), ["events.*.1.>"], 1, 6, 60);
        await Task.Delay(1000);
        var clock = Stopwatch.StartNew();
        await broker.RestartAsync();
        for (var i = 4; i <= 6; i++)
            await js.PublishAsync("events.custos.1.test", Encoding.UTF8.GetBytes($"{i}"));
        var read = await reading;

        Report($"after a restart of {clock.Elapsed.TotalSeconds:F1} s: {string.Join(", ", read)}");
        Assert.That(read.Select(m => m.Sequence), Is.EqualTo(new ulong[] { 1, 2, 3, 4, 5, 6 }));
    }

    /// <summary>Test 5: the broker removes each message at its own age, with no new
    /// publish.</summary>
    [Test]
    public async Task Broker_removes_each_message_at_its_age()
    {
        await using var broker = await NatsBroker.StartAsync();
        var maxAge = TimeSpan.FromSeconds(5);
        await broker.CreateStreamAsync(NatsBroker.StreamConfig(maxAge));
        await using var admin = broker.Connect("admin");
        var js = new NatsJSContext(admin);

        var clock = Stopwatch.StartNew();
        await js.PublishAsync("events.custos.1.test", "1"u8.ToArray());
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        var secondAt = clock.Elapsed;
        await js.PublishAsync("events.custos.1.test", "2"u8.ToArray());

        TimeSpan? firstGone = null;
        TimeSpan? secondGone = null;
        while (secondGone is null && clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            var state = (await broker.InfoAsync()).State;
            if (firstGone is null && state.FirstSeq >= 2)
                firstGone = clock.Elapsed;
            if (state.Messages == 0)
                secondGone = clock.Elapsed;
            await Task.Delay(100);
        }

        Report(
            $"max age {maxAge.TotalSeconds} s; first message gone after "
                + $"{firstGone?.TotalSeconds:F2} s; second (published at "
                + $"{secondAt.TotalSeconds:F2} s) gone after {secondGone?.TotalSeconds:F2} s"
        );
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstGone, Is.InRange(maxAge, maxAge + TimeSpan.FromSeconds(1.5)));
            Assert.That(
                secondGone - secondAt,
                Is.InRange(maxAge, maxAge + TimeSpan.FromSeconds(1.5))
            );
        }
    }

    /// <summary>Test 6: an operator removes the stream and makes it again below an open
    /// reader.</summary>
    [Test]
    public async Task Stream_made_again_has_a_new_identity_and_ends_the_open_reader()
    {
        await using var broker = await NatsBroker.StartAsync();
        var config = NatsBroker.StreamConfig();
        var before = (await broker.CreateStreamAsync(config)).Info;
        await using var admin = broker.Connect("admin");
        var js = new NatsJSContext(admin);
        for (var i = 1; i <= 3; i++)
            await js.PublishAsync("events.custos.1.test", Encoding.UTF8.GetBytes($"{i}"));
        await using var optio = broker.Connect("optio");

        var reader = ReadUntilFailureAsync(new NatsJSContext(optio), 1);
        await Task.Delay(1000);
        await js.DeleteStreamAsync(NatsBroker.Stream);
        var after = (await broker.CreateStreamAsync(config)).Info;
        await js.PublishAsync("events.custos.1.test", "4"u8.ToArray());
        await js.PublishAsync("events.custos.1.test", "5"u8.ToArray());
        var (read, end) = await reader;
        var old = await ReadAsync(new NatsJSContext(optio), ["events.>"], 3, 1, 3);

        Report(
            $"created before {before.Created:O}, after {after.Created:O}; the reader got "
                + $"{string.Join(", ", read)} and ended with {end}; an old position 3 on the new "
                + $"stream gives {string.Join(", ", old)}"
        );
        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.Created, Is.Not.EqualTo(before.Created));
            Assert.That((await broker.InfoAsync()).State.LastSeq, Is.EqualTo(2));
        }
    }

    /// <summary>Test 7: many readers start at the same time, as after a restart of
    /// Optio.</summary>
    [TestCase(500)]
    public async Task Many_readers_start_at_the_same_time(int count)
    {
        await using var broker = await NatsBroker.StartAsync();
        await broker.CreateStreamAsync(NatsBroker.StreamConfig());
        await using var optio = broker.Connect("optio");
        var js = new NatsJSContext(optio);
        var last = (await js.GetStreamAsync(NatsBroker.Stream)).Info.State.LastSeq;

        var clock = Stopwatch.StartNew();
        var errors = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var received = new TaskCompletionSource[count];
        var readers = Enumerable
            .Range(0, count)
            .Select(async guild =>
            {
                received[guild] = new TaskCompletionSource();
                try
                {
                    var consumer = await js.CreateOrderedConsumerAsync(
                        NatsBroker.Stream,
                        new NatsJSOrderedConsumerOpts
                        {
                            FilterSubjects = [$"events.*.{guild}.>"],
                            DeliverPolicy = ConsumerConfigDeliverPolicy.ByStartSequence,
                            OptStartSeq = last + 1,
                        },
                        stop.Token
                    );
                    await foreach (
                        var _ in consumer.ConsumeAsync<byte[]>(cancellationToken: stop.Token)
                    )
                    {
                        received[guild].TrySetResult();
                        break;
                    }
                }
                catch (Exception) when (!stop.IsCancellationRequested)
                {
                    Interlocked.Increment(ref errors);
                    received[guild].TrySetResult();
                }
            })
            .ToList();
        await Task.Delay(100);

        await using var admin = broker.Connect("admin");
        var publisher = new NatsJSContext(admin);
        for (var guild = 0; guild < count; guild++)
            await publisher.PublishAsync($"events.custos.{guild}.test", "x"u8.ToArray());
        await Task.WhenAll(received.Select(r => r.Task)).WaitAsync(TimeSpan.FromSeconds(60));
        var all = clock.Elapsed;
        await stop.CancelAsync();
        await Task.WhenAll(readers);

        Report(
            $"{count} readers: each got its message {all.TotalSeconds:F2} s after the start, "
                + $"errors {errors}"
        );
        Assert.That(errors, Is.Zero);
    }

    /// <summary>Test 8: a key-value bucket for the record of a query: the time to live of one
    /// key, the compare-and-set, and the size of a value.</summary>
    [Test]
    public async Task Bucket_gives_a_time_to_live_a_compare_and_set_and_a_size_limit()
    {
        await using var broker = await NatsBroker.StartAsync();
        await using var admin = broker.Connect("admin");
        var kv = new NatsKVContext(new NatsJSContext(admin));
        var store = await kv.CreateStoreAsync(
            new NatsKVConfig("queries") { History = 1, LimitMarkerTTL = TimeSpan.FromSeconds(1) }
        );

        var revision = await store.CreateAsync("short", "a", TimeSpan.FromSeconds(2));
        var stale = await OutcomeAsync(() => store.UpdateAsync("short", "b", revision + 5));
        var update = await OutcomeAsync(() => store.UpdateAsync("short", "c", revision));
        await store.CreateAsync("plain", "a", TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        var updatedKey = await OutcomeAsync(() => store.GetEntryAsync<string>("short"));
        var plainKey = await OutcomeAsync(() => store.GetEntryAsync<string>("plain"));
        var near = await OutcomeAsync(() => store.PutAsync("large", new byte[900_000]));
        var over = await OutcomeAsync(() => store.PutAsync("large", new byte[1_100_000]));

        Report(
            $"update with a stale revision: {stale}; with the right revision: {update}; after "
                + $"3.5 s, the updated key with a TTL of 2 s: {updatedKey}; the key with no "
                + $"update: {plainKey}; value of 900 kB: {near}; value of 1.1 MB: {over}"
        );
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stale, Does.StartWith("failed"));
            Assert.That(update, Does.StartWith("ok"));
            Assert.That(plainKey, Does.StartWith("failed"));
            Assert.That(near, Does.StartWith("ok"));
            Assert.That(over, Does.StartWith("failed"));
        }
    }

    /// <summary>The work of Optio on the broker: it makes the stream, changes it, reads its
    /// information, and reads one event with an ordered consumer that it then removes.</summary>
    private static async Task<string> OptioWorkAsync(NatsBroker broker)
    {
        await using (var admin = broker.Connect("admin"))
        {
            var adminJs = new NatsJSContext(admin);
            try
            {
                await adminJs.DeleteStreamAsync(NatsBroker.Stream);
            }
            catch (NatsJSApiException) { }
        }

        await using var optio = broker.Connect("optio");
        var js = new NatsJSContext(optio);
        try
        {
            await js.CreateOrUpdateStreamAsync(NatsBroker.StreamConfig());
            await js.CreateOrUpdateStreamAsync(NatsBroker.StreamConfig(TimeSpan.FromMinutes(20)));
            var stream = await js.GetStreamAsync(NatsBroker.Stream);
            await using (var custos = broker.Connect("custos"))
                await new NatsJSContext(custos).PublishAsync(
                    "events.custos.1.test",
                    "a"u8.ToArray()
                );
            var read = await ReadAsync(js, ["events.*.1.>"], stream.Info.State.LastSeq + 1, 1, 5);
            if (read.Count != 1)
                return "no event";
        }
        catch (Exception e)
        {
            return $"failed {e.GetType().Name}: {e.Message}";
        }

        await Task.Delay(500);
        await using (var admin = broker.Connect("admin"))
        {
            var consumers = 0;
            await foreach (
                var _ in new NatsJSContext(admin).ListConsumerNamesAsync(NatsBroker.Stream)
            )
                consumers++;
            return consumers == 0 ? "ok" : $"{consumers} consumer left";
        }
    }

    /// <summary>Reads <paramref name="count"/> messages with an ordered consumer from
    /// <paramref name="start"/>, or fewer when <paramref name="seconds"/> pass.</summary>
    private static async Task<List<Received>> ReadAsync(
        NatsJSContext js,
        string[] filters,
        ulong start,
        int count,
        int seconds = 10
    )
    {
        List<Received> read = [];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            var consumer = await js.CreateOrderedConsumerAsync(
                NatsBroker.Stream,
                new NatsJSOrderedConsumerOpts
                {
                    FilterSubjects = filters,
                    DeliverPolicy = ConsumerConfigDeliverPolicy.ByStartSequence,
                    OptStartSeq = start,
                },
                timeout.Token
            );
            await foreach (
                var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: timeout.Token)
            )
            {
                read.Add(new Received(msg.Metadata!.Value.Sequence.Stream, msg.Subject));
                if (read.Count == count)
                    break;
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        return read;
    }

    /// <summary>Reads with an ordered consumer until it fails or 10 seconds pass.</summary>
    private static async Task<(List<Received> Read, string End)> ReadUntilFailureAsync(
        NatsJSContext js,
        ulong start
    )
    {
        List<Received> read = [];
        using var timeout = new CancellationTokenSource(Wait);
        try
        {
            var consumer = await js.CreateOrderedConsumerAsync(
                NatsBroker.Stream,
                new NatsJSOrderedConsumerOpts
                {
                    FilterSubjects = ["events.>"],
                    DeliverPolicy = ConsumerConfigDeliverPolicy.ByStartSequence,
                    OptStartSeq = start,
                },
                timeout.Token
            );
            await foreach (
                var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: timeout.Token)
            )
                read.Add(new Received(msg.Metadata!.Value.Sequence.Stream, msg.Subject));
            return (read, "the end of the messages");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return (read, "no end in 10 s");
        }
        catch (Exception e)
        {
            return (read, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>The IDs of all messages of the stream, read as <c>admin</c>.</summary>
    private static async Task<HashSet<string>> StoredIdsAsync(NatsBroker broker)
    {
        await using var admin = broker.Connect("admin");
        var js = new NatsJSContext(admin);
        var info = (await js.GetStreamAsync(NatsBroker.Stream)).Info.State;
        HashSet<string> ids = [];
        if (info.Messages == 0)
            return ids;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var consumer = await js.CreateOrderedConsumerAsync(
            NatsBroker.Stream,
            cancellationToken: timeout.Token
        );
        await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: timeout.Token))
        {
            ids.Add(Encoding.UTF8.GetString(msg.Data!));
            if (msg.Metadata!.Value.Sequence.Stream >= info.LastSeq)
                break;
        }
        return ids;
    }

    /// <summary>True when a core subscription to <paramref name="subject"/> gets a message in
    /// 1 second. When <paramref name="publish"/> is set, <c>admin</c> publishes to it.</summary>
    private static async Task<bool> ReceivesAsync(
        NatsConnection connection,
        string subject,
        string? publish
    )
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        var receive = Task.Run(async () =>
        {
            try
            {
                await foreach (
                    var _ in connection.SubscribeAsync<byte[]>(
                        subject,
                        cancellationToken: timeout.Token
                    )
                )
                {
                    return true;
                }
            }
            catch (OperationCanceledException) { }
            return false;
        });
        if (publish is not null)
        {
            await Task.Delay(300);
            var opts = connection.Opts;
            await using var admin = new NatsConnection(
                opts with
                {
                    Url = opts.Url.Replace(
                        $"{opts.Name}:{opts.Name}@",
                        "admin:admin@",
                        StringComparison.Ordinal
                    ),
                    InboxPrefix = "_INBOX",
                }
            );
            await admin.PublishAsync(publish, "x"u8.ToArray());
        }
        return await receive;
    }

    private static async Task<string> OutcomeAsync<T>(Func<ValueTask<T>> call)
    {
        try
        {
            var result = await call();
            if (result is PubAckResponse ack)
                ack.EnsureSuccess();
            return $"ok {result}";
        }
        catch (Exception e)
        {
            return $"failed {e.GetType().Name}: {e.Message}";
        }
    }

    private static Task<string> OutcomeAsync(Func<ValueTask> call) =>
        OutcomeAsync(async () =>
        {
            await call();
            return true;
        });

    private static void Report(string line) => TestContext.Out.WriteLine(line);

    private sealed record Received(ulong Sequence, string Subject)
    {
        public override string ToString() => $"{Sequence} {Subject}";
    }
}
