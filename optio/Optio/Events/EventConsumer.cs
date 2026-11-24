using Microsoft.Extensions.Options;
using Optio.Streams;
using Optio.Telemetry;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Optio.Events;

/// <summary>
/// Reads the stream <c>events.&lt;source&gt;.stream</c> of each source and gives each event to
/// the hub. At the start, and after the broker cancels the consumer, it attaches with the time
/// "now minus the replay window". After a lost connection, it attaches from the last offset plus
/// 1. It does not use the recovery of the client, which consumes again with the first arguments.
/// </summary>
internal sealed partial class EventConsumer(
    ConnectionFactory factory,
    StreamHub hub,
    IOptions<OptioOptions> options,
    TimeProvider time,
    OptioMetrics metrics,
    ILogger<EventConsumer> logger
) : BackgroundService
{
    public const string OffsetHeader = "x-stream-offset";

    /// <summary>A stream consumer needs a prefetch and manual acknowledgements.</summary>
    private const ushort Prefetch = 100;

    private static readonly TimeSpan MinWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    public static string StreamName(string source) => $"events.{source}.stream";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(options.Value.Sources.Select(source => ConsumeAsync(source, stoppingToken)));

    private async Task ConsumeAsync(string source, CancellationToken stoppingToken)
    {
        long? last = null;
        var wait = MinWait;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(
                    cancellationToken: stoppingToken
                );
                await channel.BasicQosAsync(0, Prefetch, false, stoppingToken);

                var consumer = new StreamConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    var offset = Convert.ToInt64(delivery.BasicProperties.Headers![OffsetHeader]);
                    last = offset;
                    if (ProducerEvent.Parse(source, offset, delivery.Body) is { } producerEvent)
                        hub.Publish(producerEvent);
                    else
                        metrics.Event("ignored");
                    await channel.BasicAckAsync(delivery.DeliveryTag, false);
                };

                await channel.BasicConsumeAsync(
                    StreamName(source),
                    autoAck: false,
                    consumerTag: "",
                    noLocal: false,
                    exclusive: false,
                    arguments: new Dictionary<string, object?>
                    {
                        [OffsetHeader] = last is { } offset ? offset + 1 : Since(),
                    },
                    consumer,
                    stoppingToken
                );
                LogConsuming(logger, source, last);
                wait = MinWait;

                if (await consumer.Ended.WaitAsync(stoppingToken))
                {
                    LogCancelled(logger, source);
                    last = null;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                LogBrokerFailed(logger, e, source, wait.TotalSeconds);
                await Task.Delay(
                    wait * (0.5 + Random.Shared.NextDouble() / 2),
                    time,
                    stoppingToken
                );
                wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, MaxWait.Ticks));
            }
        }
    }

    /// <summary>A consumer that tells why it ended: true when the broker cancelled it, for example
    /// because the stream was deleted, and false when the channel closed.</summary>
    private sealed class StreamConsumer : AsyncEventingBasicConsumer
    {
        private readonly TaskCompletionSource<bool> _ended = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public StreamConsumer(IChannel channel)
            : base(channel) =>
            channel.ChannelShutdownAsync += (_, _) =>
            {
                _ended.TrySetResult(false);
                return Task.CompletedTask;
            };

        public Task<bool> Ended => _ended.Task;

        public override Task HandleBasicCancelAsync(
            string consumerTag,
            CancellationToken cancellationToken = default
        )
        {
            _ended.TrySetResult(true);
            return base.HandleBasicCancelAsync(consumerTag, cancellationToken);
        }
    }

    /// <summary>The start of the replay window, in seconds, as the broker takes it.</summary>
    private AmqpTimestamp Since() =>
        new((time.GetUtcNow() - options.Value.ReplayWindow).ToUnixTimeSeconds());

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Reading the events of {Source} after offset {Offset}"
    )]
    private static partial void LogConsuming(ILogger logger, string source, long? offset);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The broker cancelled the consumer of {Source}. Attaching at the window"
    )]
    private static partial void LogCancelled(ILogger logger, string source);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The broker is not available for {Source}. Next try in about {WaitSeconds} s"
    )]
    private static partial void LogBrokerFailed(
        ILogger logger,
        Exception exception,
        string source,
        double waitSeconds
    );
}
