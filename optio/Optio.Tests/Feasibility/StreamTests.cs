using System.Diagnostics;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Optio.Tests.Feasibility;

/// <summary>
/// The feasibility tests T1 of M37 (issue #48): a RabbitMQ stream over AMQP 0.9.1. Each test
/// that counts messages uses its own stream and its own routing keys. Needs Docker.
/// </summary>
[Category("Feasibility")]
public sealed class StreamTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    private StreamBroker _broker = null!;

    [OneTimeSetUp]
    public async Task StartAsync() => _broker = await StreamBroker.StartAsync();

    [OneTimeTearDown]
    public async Task StopAsync() => await _broker.DisposeAsync();

    /// <summary>T1.0</summary>
    [Test]
    public async Task Definitions_declare_the_stream_and_a_publish_of_custos_reaches_it()
    {
        var queues = await _broker.CtlAsync(
            "list_queues",
            "name",
            "type",
            "arguments",
            "--formatter",
            "json"
        );
        TestContext.Out.WriteLine($"T1.0 queues: {queues}");
        Assert.That(queues, Does.Contain("\"name\":\"optio.events\""));
        Assert.That(queues, Does.Contain("\"type\":\"stream\""));
        var bindings = await _broker.CtlAsync("list_bindings", "--formatter", "json");
        Assert.That(bindings, Does.Contain("\"destination_name\":\"optio.events\""));

        await using var optio = await _broker.ConnectAsync("optio");
        await using var reader = await StreamReader.OpenAsync(optio, StreamBroker.Stream, "next");
        await _broker.PublishAsync("t10.guild.stockpile.changed", 1, first: 10);

        var deliveries = await reader.ReadAsync(1, Quiet);
        Assert.That(deliveries.Select(d => d.Sequence), Is.EqualTo(new[] { 10 }));
    }

    /// <summary>T1.1</summary>
    [Test]
    public async Task Each_delivery_carries_its_offset()
    {
        await _broker.DeclareStreamAsync("t11", "t11.#");
        await _broker.PublishAsync("t11.x", 5);

        await using var admin = await _broker.ConnectAsync("admin");
        await using var reader = await StreamReader.OpenAsync(admin, "t11", "first");
        var deliveries = await reader.ReadAsync(5, Quiet);

        Assert.That(deliveries.Select(d => d.Offset), Is.EqualTo(new long[] { 0, 1, 2, 3, 4 }));
    }

    /// <summary>T1.2</summary>
    [Test]
    public async Task Consumer_starts_at_the_offset_that_it_names()
    {
        await _broker.DeclareStreamAsync("t12", "t12.#");
        await _broker.PublishAsync("t12.a", 10);
        await Task.Delay(TimeSpan.FromSeconds(2.2));
        var between = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        await _broker.PublishAsync("t12.b", 10, first: 10);

        await using var admin = await _broker.ConnectAsync("admin");
        async Task<List<Delivery>> FromAsync(object offset)
        {
            await using var reader = await StreamReader.OpenAsync(admin, "t12", offset);
            var deliveries = await reader.ReadAsync(100, Quiet);
            TestContext.Out.WriteLine(
                $"T1.2 offset {offset}: {deliveries.Count} messages, offsets "
                    + string.Join(",", deliveries.Select(d => d.Offset))
            );
            return deliveries;
        }

        var number = await FromAsync(7L);
        var first = await FromAsync("first");
        var last = await FromAsync("last");
        var timestamp = await FromAsync(new AmqpTimestamp(between));
        var interval = await FromAsync("1h");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(number.First().Offset, Is.EqualTo(7));
            Assert.That(number, Has.Count.EqualTo(13));
            Assert.That(first.First().Offset, Is.Zero);
            Assert.That(first, Has.Count.EqualTo(20));
            // "last" starts at the first message of the last chunk, not at the last message.
            Assert.That(last.Last().Offset, Is.EqualTo(19));
            Assert.That(last.First().Offset, Is.InRange(10, 19));
            Assert.That(timestamp.First().Offset, Is.EqualTo(10));
            Assert.That(interval, Has.Count.EqualTo(20));
        }

        await using var next = await StreamReader.OpenAsync(admin, "t12", "next");
        Assert.That(await next.ReadAsync(1, Quiet), Is.Empty);
        await _broker.PublishAsync("t12.c", 1, first: 20);
        Assert.That((await next.ReadAsync(1, Quiet)).Single().Offset, Is.EqualTo(20));
    }

    /// <summary>T1.2: the rules for the prefetch and the acknowledgement.</summary>
    [Test]
    public async Task Stream_consumer_needs_a_prefetch_and_manual_acknowledgements()
    {
        await _broker.DeclareStreamAsync("t12q", "t12q.#");
        await _broker.PublishAsync("t12q.a", 20);
        await using var admin = await _broker.ConnectAsync("admin");
        var arguments = new Dictionary<string, object?> { ["x-stream-offset"] = "first" };

        await using (var channel = await admin.CreateChannelAsync())
        {
            var consumer = new StreamReaderProbe(channel);
            await Assert.ThatAsync(
                () =>
                    channel.BasicConsumeAsync("t12q", false, "", false, false, arguments, consumer),
                Throws.Exception
            );
            TestContext.Out.WriteLine($"T1.2 no prefetch: {channel.CloseReason}");
            Assert.That(channel.CloseReason?.ReplyCode, Is.EqualTo(406));
        }

        // 540 NOT_IMPLEMENTED is a connection error: the broker closes the connection.
        await using (var other = await _broker.ConnectAsync("admin"))
        await using (var channel = await other.CreateChannelAsync())
        {
            await channel.BasicQosAsync(0, 10, false);
            var consumer = new StreamReaderProbe(channel);
            await Assert.ThatAsync(
                () =>
                    channel.BasicConsumeAsync("t12q", true, "", false, false, arguments, consumer),
                Throws.Exception
            );
            await Task.Delay(300);
            TestContext.Out.WriteLine($"T1.2 automatic acknowledgement: {other.CloseReason}");
            Assert.That(other.CloseReason?.ReplyCode, Is.EqualTo(540));
        }

        // With no acknowledgement, the broker stops at the prefetch count.
        await using var reader = await StreamReader.OpenAsync(
            admin,
            "t12q",
            "first",
            prefetch: 5,
            acknowledge: false
        );
        var deliveries = await reader.ReadAsync(20, Quiet);
        TestContext.Out.WriteLine($"T1.2 prefetch 5, no acknowledgement: {deliveries.Count}");
        Assert.That(deliveries, Has.Count.EqualTo(5));
    }

    /// <summary>T1.3</summary>
    [Test]
    public async Task Optio_needs_a_read_permission_on_the_stream()
    {
        await using var file = await _broker.ConnectAsync("optio-file");
        await AssertRefusedAsync(file, c => ConsumeAsync(c, StreamBroker.Stream));

        await using var optio = await _broker.ConnectAsync("optio");
        await using var reader = await StreamReader.OpenAsync(optio, StreamBroker.Stream, "first");
        Assert.That(await reader.ReadAsync(1, Quiet), Is.Not.Null);
    }

    /// <summary>T1.3</summary>
    [Test]
    public async Task Custos_cannot_consume_the_stream()
    {
        await using var custos = await _broker.ConnectAsync("custos");
        await AssertRefusedAsync(custos, c => ConsumeAsync(c, StreamBroker.Stream));
    }

    /// <summary>T1.3</summary>
    [Test]
    public async Task Optio_cannot_write_to_the_stream_or_delete_it()
    {
        await using var optio = await _broker.ConnectAsync("optio");
        await AssertRefusedAsync(optio, c =>
            c.BasicPublishAsync(
                    "",
                    StreamBroker.Stream,
                    false,
                    new BasicProperties(),
                    "1"u8.ToArray()
                )
                .AsTask()
        );
        await AssertRefusedAsync(optio, c => c.QueueDeleteAsync(StreamBroker.Stream));
    }

    /// <summary>
    /// T1.4. The broker removes a segment only when all its messages are older than
    /// <c>x-max-age</c>. It never removes the active segment.
    /// </summary>
    [Test]
    public async Task Broker_removes_old_segments_after_the_maximum_age()
    {
        await _broker.DeclareStreamAsync("t14", "t14.#", maxAge: "5s", segmentBytes: 4096);
        await PublishChunksAsync(0);
        TestContext.Out.WriteLine($"T1.4 segments: {await _broker.SegmentsAsync("t14")}");

        var idle = Stopwatch.StartNew();
        var first = await FirstOffsetUntilAsync(idle, TimeSpan.FromSeconds(30));
        TestContext.Out.WriteLine(
            $"T1.4 with no publish: first offset {first} after {idle.Elapsed.TotalSeconds:F1} s, "
                + $"segments {await _broker.SegmentsAsync("t14")}"
        );

        // A publish that fills the active segment makes a new segment.
        var roll = Stopwatch.StartNew();
        await PublishChunksAsync(200);
        var published = roll.Elapsed;
        var after = await FirstOffsetUntilAsync(roll, TimeSpan.FromSeconds(30));
        TestContext.Out.WriteLine(
            $"T1.4 after a new publish of {published.TotalSeconds:F1} s: first offset {after} "
                + $"after {roll.Elapsed.TotalSeconds:F1} s, "
                + $"segments {await _broker.SegmentsAsync("t14")}"
        );

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.Zero);
            Assert.That(after, Is.GreaterThan(0));
        }

        async Task PublishChunksAsync(int from)
        {
            await using var custos = await _broker.ConnectAsync("custos");
            await using var channel = await custos.CreateChannelAsync();
            // One message for each chunk: the wait closes each chunk.
            for (var i = from; i < from + 200; i++)
            {
                await StreamBroker.PublishOneAsync(channel, "t14.x", i);
                await Task.Delay(20);
            }
        }

        async Task<long?> FirstOffsetUntilAsync(Stopwatch clock, TimeSpan limit)
        {
            long? offset = 0;
            while (offset == 0 && clock.Elapsed < limit)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                offset = await _broker.FirstOffsetAsync("t14");
            }
            return offset;
        }
    }

    /// <summary>T1.5</summary>
    [Test]
    public async Task Two_consumers_each_get_all_messages()
    {
        await _broker.DeclareStreamAsync("t15", "t15.#");
        await using var one = await _broker.ConnectAsync("admin");
        await using var two = await _broker.ConnectAsync("admin");
        await using var first = await StreamReader.OpenAsync(one, "t15", "next");
        await using var second = await StreamReader.OpenAsync(two, "t15", "next");

        await _broker.PublishAsync("t15.x", 100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await first.ReadAsync(200, Quiet), Has.Count.EqualTo(100));
            Assert.That(await second.ReadAsync(200, Quiet), Has.Count.EqualTo(100));
        }
    }

    /// <summary>
    /// T1.6. The broker filters a full chunk with a Bloom filter. A chunk that holds a message
    /// of the guild goes to the consumer with all its messages. The client must filter again.
    /// </summary>
    [Test]
    public async Task Stream_filter_removes_only_the_chunks_with_no_message_of_the_guild()
    {
        const string Guild = "300000000000000001";
        const string Other = "300000000000000002";
        await _broker.DeclareStreamAsync("t16", "t16.#");
        Dictionary<string, object?> Filter(string guild) =>
            new() { ["x-stream-filter-value"] = guild };

        // Separate batches: each batch is one guild.
        await _broker.PublishAsync("t16.x", 200, first: 0, Filter(Other));
        await _broker.PublishAsync("t16.x", 200, first: 200, Filter(Guild));
        await _broker.PublishAsync("t16.x", 200, first: 400, Filter(Other));
        // One batch with the two guilds in turn.
        await using (var custos = await _broker.ConnectAsync("custos"))
        await using (var channel = await custos.CreateChannelAsync())
        {
            for (var i = 600; i < 800; i++)
                await StreamBroker.PublishOneAsync(
                    channel,
                    "t16.x",
                    i,
                    Filter(i % 2 == 0 ? Guild : Other)
                );
            await Task.Delay(300);
        }

        await using var optio = await _broker.ConnectAsync("admin");
        await using var reader = await StreamReader.OpenAsync(
            optio,
            "t16",
            "first",
            arguments: new Dictionary<string, object?>
            {
                ["x-stream-filter"] = Guild,
                ["x-stream-match-unfiltered"] = false,
            }
        );
        var deliveries = await reader.ReadAsync(1000, Quiet);

        int Count(int from, int to, string guild) =>
            deliveries.Count(d => d.Sequence >= from && d.Sequence < to && d.Filter == guild);
        var separateOther = Count(0, 200, Other) + Count(400, 600, Other);
        var mixedOther = Count(600, 800, Other);
        TestContext.Out.WriteLine(
            $"T1.6 separate batches: {Count(200, 400, Guild)} of 200 of the guild, "
                + $"{separateOther} of 400 of the other guild"
        );
        TestContext.Out.WriteLine(
            $"T1.6 mixed batch: {Count(600, 800, Guild)} of 100 of the guild, "
                + $"{mixedOther} of 100 of the other guild"
        );
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Count(200, 400, Guild), Is.EqualTo(200));
            Assert.That(Count(600, 800, Guild), Is.EqualTo(100));
            Assert.That(separateOther, Is.LessThan(400));
            Assert.That(mixedOther, Is.GreaterThan(0));
        }
    }

    /// <summary>
    /// T1.8. A consumer at an offset after the end waits until the stream gets to that offset.
    /// After a delete and a new declare, the offsets start at 0 again.
    /// </summary>
    [Test]
    public async Task Offset_after_the_end_waits_until_the_stream_gets_to_it()
    {
        await _broker.DeclareStreamAsync("t18", "t18.#");
        await _broker.PublishAsync("t18.x", 10);
        await using var admin = await _broker.ConnectAsync("admin");

        await using var after = await StreamReader.OpenAsync(admin, "t18", 1000L);
        await _broker.PublishAsync("t18.x", 1, first: 10);
        var early = await after.ReadAsync(10, Quiet);
        await _broker.PublishAsync("t18.x", 991, first: 11);
        var late = await after.ReadAsync(10, Quiet);
        TestContext.Out.WriteLine(
            $"T1.8 offset 1000 on 11 messages: {early.Count} messages. On 1002 messages: offsets "
                + string.Join(",", late.Select(d => d.Offset))
        );
        using (Assert.EnterMultipleScope())
        {
            Assert.That(early, Is.Empty);
            Assert.That(late.Select(d => d.Offset), Is.EqualTo(new long[] { 1000, 1001 }));
        }

        // The stream is deleted while the consumer reads it.
        await _broker.DeleteStreamAsync("t18");
        await Task.Delay(500);
        TestContext.Out.WriteLine(
            $"T1.8 delete: the broker cancelled the consumer: {after.CancelledByBroker}"
        );
        Assert.That(after.CancelledByBroker, Is.True);

        await _broker.DeclareStreamAsync("t18", "t18.#");
        await _broker.PublishAsync("t18.x", 3, first: 100);
        await using var old = await StreamReader.OpenAsync(admin, "t18", 11L);
        await using var inside = await StreamReader.OpenAsync(admin, "t18", 1L);
        await _broker.PublishAsync("t18.x", 2, first: 103);
        var fromInside = await inside.ReadAsync(100, Quiet);
        var oldEarly = await old.ReadAsync(100, Quiet);
        await _broker.PublishAsync("t18.x", 10, first: 105);
        var oldLate = await old.ReadAsync(100, Quiet);
        string Text(List<Delivery> d) =>
            string.Join(",", d.Select(m => $"{m.Sequence}@{m.Offset}"));
        TestContext.Out.WriteLine($"T1.8 new stream, offset 1: {Text(fromInside)}");
        TestContext.Out.WriteLine(
            $"T1.8 new stream, offset 11: {Text(oldEarly)} at 5 messages, "
                + $"{Text(oldLate)} at 15 messages"
        );
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                fromInside.Select(d => d.Sequence),
                Is.EqualTo(new[] { 101, 102, 103, 104 })
            );
            Assert.That(oldEarly, Is.Empty);
            Assert.That(oldLate.Select(d => d.Sequence), Is.EqualTo(new[] { 111, 112, 113, 114 }));
        }
    }

    /// <summary>T1.9</summary>
    [Test]
    public async Task Mandatory_publish_with_no_binding_comes_back_with_no_confirms()
    {
        await using var admin = await _broker.ConnectAsync("admin");
        await using var channel = await admin.CreateChannelAsync();
        var returned = new TaskCompletionSource<(ushort Code, string Text)>();
        channel.BasicReturnAsync += (_, r) =>
        {
            returned.TrySetResult((r.ReplyCode, r.ReplyText));
            return Task.CompletedTask;
        };

        await channel.BasicPublishAsync(
            "events.unbound",
            "300000000000000001.stockpile.changed",
            true,
            new BasicProperties(),
            "{}"u8.ToArray()
        );

        var result = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        TestContext.Out.WriteLine($"T1.9 return: {result.Code} {result.Text}");
        Assert.That(result, Is.EqualTo(((ushort)312, "NO_ROUTE")));
    }

    /// <summary>T1.9: a routed mandatory publish does not come back.</summary>
    [Test]
    public async Task Mandatory_publish_to_a_bound_exchange_does_not_come_back()
    {
        await using var custos = await _broker.ConnectAsync("custos");
        await using var channel = await custos.CreateChannelAsync();
        var returned = false;
        channel.BasicReturnAsync += (_, _) =>
        {
            returned = true;
            return Task.CompletedTask;
        };

        await StreamBroker.PublishOneAsync(channel, "t19.x", 1, mandatory: true);
        await Task.Delay(Quiet);

        Assert.That(returned, Is.False);
    }

    private static Task ConsumeAsync(IChannel channel, string stream) =>
        channel.BasicConsumeAsync(
            stream,
            false,
            "",
            false,
            false,
            new Dictionary<string, object?> { ["x-stream-offset"] = "first" },
            new StreamReaderProbe(channel)
        );

    /// <summary>
    /// The broker closes the channel with 403 ACCESS_REFUSED. A method with an answer throws.
    /// A publish with no confirms does not throw: the broker closes the channel later.
    /// </summary>
    private static async Task AssertRefusedAsync(
        IConnection connection,
        Func<IChannel, Task> action
    )
    {
        await using var channel = await connection.CreateChannelAsync();
        await channel.BasicQosAsync(0, 10, false);
        try
        {
            await action(channel);
        }
        catch (OperationInterruptedException) { }
        await Task.Delay(300);
        Assert.That(channel.CloseReason?.ReplyCode, Is.EqualTo(403), $"{channel.CloseReason}");
    }

    /// <summary>A consumer that does nothing, for a consume that the broker refuses.</summary>
    private sealed class StreamReaderProbe(IChannel channel) : AsyncDefaultBasicConsumer(channel);
}
