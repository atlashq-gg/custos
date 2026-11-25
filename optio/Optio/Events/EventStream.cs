using System.Threading.Channels;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Optio.Events;

/// <summary>
/// The stream of the events on the broker (optio/docs/producers.md). Optio owns it: it makes the
/// stream when it starts, and sets its settings again. The broker keeps the events for the replay
/// window, so Optio keeps no events. Each client stream gets its own reader.
/// </summary>
internal sealed partial class EventStream(
    NatsConnection connection,
    IOptions<OptioOptions> options,
    TimeProvider time,
    ILogger<EventStream> logger
) : BackgroundService
{
    public const string Name = "OPTIO";

    /// <summary>The wait for one call of the JetStream API when a client stream opens.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A reader gives at most this number of events that the client did not read. Then
    /// it stops to fetch from the broker until the client reads.</summary>
    private const int ReaderCapacity = 16;

    private static readonly TimeSpan MinWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    private readonly NatsJSContext _js = new(connection);
    private volatile bool _made;

    /// <summary>True when the stream exists and the client has a connection.</summary>
    public bool IsReady => _made && connection.ConnectionState == NatsConnectionState.Open;

    public static StreamConfig Config(OptioOptions options)
    {
        var maxAge = options.ReplayWindow;
        var duplicates = TimeSpan.FromMinutes(2);
        return new StreamConfig(Name, ["events.>"])
        {
            Retention = StreamConfigRetention.Limits,
            Storage = StreamConfigStorage.File,
            Discard = StreamConfigDiscard.Old,
            MaxAge = maxAge,
            MaxBytes = options.StreamMaxMegabytes * 1_000_000L,
            DuplicateWindow = maxAge < duplicates ? maxAge : duplicates,
            AllowRollupHdrs = false,
            DenyDelete = true,
            DenyPurge = true,
            NumReplicas = 1,
        };
    }

    /// <summary>Makes the stream, or sets its settings again. While the broker is not available,
    /// it tries again after a wait.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wait = MinWait;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MakeAsync(stoppingToken);
                return;
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                LogBrokerFailed(logger, e, wait.TotalSeconds);
                await Task.Delay(wait, time, stoppingToken);
                wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, MaxWait.Ticks));
            }
        }
    }

    /// <summary>The position of now: the identity of the stream and its last sequence. Null when
    /// the broker or the stream is not available. A stream that an operator removed is made
    /// again.</summary>
    public async Task<Position?> NowAsync(CancellationToken cancellationToken)
    {
        if (!IsReady)
            return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);
        try
        {
            StreamInfo info;
            try
            {
                info = (await _js.GetStreamAsync(Name, cancellationToken: timeout.Token)).Info;
            }
            catch (NatsJSApiException e) when (e.Error.Code == 404)
            {
                info = await MakeAsync(timeout.Token);
            }
            return new Position(info.Created.ToUnixTimeMilliseconds(), info.State.LastSeq);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            LogNoPosition(logger, e);
            return null;
        }
    }

    /// <summary>
    /// Reads the events of <paramref name="guild"/> after <paramref name="start"/> with an ordered
    /// consumer, in the order of the stream. The reader fetches from the broker only while the
    /// client reads. It completes when it fails, for example when the stream goes, and it stops
    /// with <paramref name="cancellationToken"/>.
    /// </summary>
    public ChannelReader<ProducerEvent?> Read(
        ulong guild,
        Position start,
        CancellationToken cancellationToken
    )
    {
        var events = Channel.CreateBounded<ProducerEvent?>(
            new BoundedChannelOptions(ReaderCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            }
        );
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var consumer = await _js.CreateOrderedConsumerAsync(
                        Name,
                        new NatsJSOrderedConsumerOpts
                        {
                            FilterSubjects = [ProducerEvent.Filter(guild)],
                            DeliverPolicy = ConsumerConfigDeliverPolicy.ByStartSequence,
                            OptStartSeq = start.Sequence + 1,
                        },
                        cancellationToken
                    );
                    await foreach (
                        var msg in consumer.ConsumeAsync<ReadOnlyMemory<byte>>(
                            cancellationToken: cancellationToken
                        )
                    )
                    {
                        // Null for an event that is not valid: the client stream counts it.
                        var sequence = msg.Metadata?.Sequence.Stream ?? 0;
                        await events.Writer.WriteAsync(
                            ProducerEvent.Parse(msg.Subject, sequence, msg.Data),
                            cancellationToken
                        );
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                { }
                catch (Exception e)
                {
                    LogReaderFailed(logger, e, guild);
                }
                events.Writer.TryComplete();
            },
            CancellationToken.None
        );
        return events.Reader;
    }

    private async Task<StreamInfo> MakeAsync(CancellationToken cancellationToken)
    {
        var info = (
            await _js.CreateOrUpdateStreamAsync(Config(options.Value), cancellationToken)
        ).Info;
        _made = true;
        LogStreamReady(logger, Name, info.Created);
        return info;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "The stream {Stream} of the events is ready, made at {Created}"
    )]
    private static partial void LogStreamReady(
        ILogger logger,
        string stream,
        DateTimeOffset created
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The event broker is not available. Next try in about {WaitSeconds} s"
    )]
    private static partial void LogBrokerFailed(
        ILogger logger,
        Exception exception,
        double waitSeconds
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The event broker gave no position for a stream"
    )]
    private static partial void LogNoPosition(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The reader of a stream of guild {GuildId} failed"
    )]
    private static partial void LogReaderFailed(ILogger logger, Exception exception, ulong guildId);
}
