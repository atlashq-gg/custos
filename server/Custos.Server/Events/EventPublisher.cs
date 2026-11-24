using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Custos.Server.Telemetry;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Custos.Server.Events;

/// <summary>
/// Publishes the events of the server to the broker, as optio/docs/producers.md describes. A
/// request gives its events to a bounded queue in memory and does not wait for the broker. When
/// the queue is full, the oldest event goes. Delivery is at-most-once: the publisher does not
/// publish an event again.
/// </summary>
internal sealed partial class EventPublisher : BackgroundService
{
    /// <summary>The name of the connection string of the broker.</summary>
    public const string ConnectionName = "events";
    public const string Source = "custos";
    public const string Exchange = "events." + Source;
    public const string ContentType = "application/cloudevents+json";
    public const int Capacity = 1000;

    private static readonly TimeSpan MinWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    // The attribute names of CloudEvents are lowercase words with no separator, and the members of
    // the data are snake_case.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConnectionFactory _factory;
    private readonly TimeProvider _time;
    private readonly ServerMetrics _metrics;
    private readonly ILogger<EventPublisher> _logger;
    private readonly Channel<Message> _queue;

    public EventPublisher(
        ConnectionFactory factory,
        TimeProvider time,
        ServerMetrics metrics,
        ILogger<EventPublisher> logger
    )
    {
        _factory = factory;
        _time = time;
        _metrics = metrics;
        _logger = logger;
        _queue = Channel.CreateBounded<Message>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => _metrics.Event(EventOutcome.Dropped)
        );
    }

    /// <summary>Gives one event to the queue. The routing key is the guild of the policy and the
    /// type.</summary>
    public void Publish(string type, DateTimeOffset time, EventData data)
    {
        var trace = Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity
            ? activity.Id
            : null;
        var id = Guid.CreateVersion7(_time.GetUtcNow()).ToString();
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new CloudEvent("1.0", id, Source, type, time, "application/json", trace, data),
            Json
        );
        _queue.Writer.TryWrite(new Message($"{data.Policy.Guild}.{type}", id, body));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wait = MinWait;
        while (!stoppingToken.IsCancellationRequested)
        {
            Message? current = null;
            try
            {
                await using var connection = await _factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(
                    new CreateChannelOptions(
                        publisherConfirmationsEnabled: true,
                        publisherConfirmationTrackingEnabled: true
                    ),
                    stoppingToken
                );
                LogConnected(_logger);
                wait = MinWait;

                await foreach (var message in _queue.Reader.ReadAllAsync(stoppingToken))
                {
                    current = message;
                    _metrics.Event(await PublishAsync(channel, message, stoppingToken));
                    current = null;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // At-most-once: the event of the failed call is lost.
                if (current is not null)
                    _metrics.Event(EventOutcome.Failed);
                LogBrokerFailed(_logger, e, wait.TotalSeconds);
                await Task.Delay(Jitter(wait), _time, stoppingToken);
                wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, MaxWait.Ticks));
            }
        }
    }

    /// <summary>Publishes with <c>mandatory</c> true. With the confirm tracking, the call returns
    /// after the broker confirms, and throws when the broker returns the event or does not accept
    /// it. The confirms are only for the counter.</summary>
    private async Task<EventOutcome> PublishAsync(
        IChannel channel,
        Message message,
        CancellationToken cancellationToken
    )
    {
        var properties = new BasicProperties
        {
            ContentType = ContentType,
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = message.Id,
        };
        try
        {
            await channel.BasicPublishAsync(
                Exchange,
                message.RoutingKey,
                mandatory: true,
                properties,
                message.Body,
                cancellationToken
            );
            return EventOutcome.Published;
        }
        catch (PublishException e)
        {
            // A returned event shows that the stream or its binding is missing.
            LogNotAccepted(_logger, message.RoutingKey, e.IsReturn);
            return e.IsReturn ? EventOutcome.Returned : EventOutcome.Failed;
        }
    }

    /// <summary>A wait between half and all of <paramref name="wait"/>, so that two instances do
    /// not connect at the same time.</summary>
    private static TimeSpan Jitter(TimeSpan wait) => wait * (0.5 + Random.Shared.NextDouble() / 2);

    private sealed record Message(string RoutingKey, string Id, byte[] Body);

    private sealed record CloudEvent(
        string Specversion,
        string Id,
        string Source,
        string Type,
        DateTimeOffset Time,
        string Datacontenttype,
        string? Traceparent,
        object Data
    );

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to the event broker")]
    private static partial void LogConnected(ILogger logger);

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
        Message = "The event broker did not accept the event {RoutingKey} (returned: {Returned})"
    )]
    private static partial void LogNotAccepted(ILogger logger, string routingKey, bool returned);
}

/// <summary>The members of the data of each event: who can read it, and the message.</summary>
internal abstract record EventData(EventPolicy Policy, EventMessage Message);

/// <summary>The access policy of an event in Discord terms. IDs are strings.</summary>
internal sealed record EventPolicy(string Guild, IReadOnlyList<string> Roles)
{
    public int Version => 1;
}

internal sealed record EventMessage(string Text, string? Reference);

internal enum EventOutcome
{
    Published,
    Returned,
    Dropped,
    Failed,
}

internal static class EventsSetup
{
    /// <summary>Registers the publisher when the configuration has the connection string of the
    /// broker. Without it, the server publishes no events.</summary>
    public static IHostApplicationBuilder AddCustosEvents(this IHostApplicationBuilder builder)
    {
        if (
            builder.Configuration.GetConnectionString(EventPublisher.ConnectionName)
            is not { Length: > 0 } connection
        )
        {
            return builder;
        }

        // The publisher connects again itself, so the client does not recover a connection.
        var factory = new ConnectionFactory
        {
            Uri = new Uri(connection),
            AutomaticRecoveryEnabled = false,
            TopologyRecoveryEnabled = false,
            ClientProvidedName = "custos-server",
        };
        builder.Services.AddSingleton(services =>
            ActivatorUtilities.CreateInstance<EventPublisher>(services, factory)
        );
        builder.Services.AddHostedService(services =>
            services.GetRequiredService<EventPublisher>()
        );
        builder.Services.AddScoped<StockpileEvents>();
        return builder;
    }
}
