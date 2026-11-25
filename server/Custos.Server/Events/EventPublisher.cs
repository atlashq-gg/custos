using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Custos.Server.Telemetry;
using NATS.Client.Core;
using NATS.Client.JetStream;

namespace Custos.Server.Events;

/// <summary>
/// Publishes the events of the server to the broker, as optio/docs/producers.md describes. A
/// request gives its events to a bounded queue in memory and does not wait for the broker. When
/// the queue is full, the oldest event goes. Delivery is at-most-once: the publisher does not
/// publish an event again. The acknowledgement of the broker is only for the counter.
/// </summary>
internal sealed partial class EventPublisher : BackgroundService
{
    /// <summary>The name of the connection string of the broker.</summary>
    public const string ConnectionName = "events";
    public const string Source = "custos";
    public const string ContentType = "application/cloudevents+json";
    public const int Capacity = 1000;

    /// <summary>The wait for the acknowledgement of one event, also while the client has no
    /// connection. It is shorter than the request timeout of the client (5 s by default), so that
    /// only a missing stream gives <see cref="NatsJSPublishNoResponseException"/>.</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(3);

    // The attribute names of CloudEvents are lowercase words with no separator, and the members of
    // the data are snake_case.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly NatsHeaders Headers = new() { ["Content-Type"] = ContentType };

    private readonly INatsJSContext _js;
    private readonly TimeProvider _time;
    private readonly ServerMetrics _metrics;
    private readonly ILogger<EventPublisher> _logger;
    private readonly Channel<Message> _queue;

    public EventPublisher(
        INatsJSContext js,
        TimeProvider time,
        ServerMetrics metrics,
        ILogger<EventPublisher> logger
    )
    {
        _js = js;
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

    /// <summary>The subject of an event: the source, the guild of the policy, and the
    /// type.</summary>
    public static string Subject(string guild, string type) => $"events.{Source}.{guild}.{type}";

    /// <summary>Gives one event to the queue.</summary>
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
        _queue.Writer.TryWrite(new Message(Subject(data.Policy.Guild, type), id, body));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(stoppingToken))
                _metrics.Event(await PublishAsync(message, stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>Publishes one event and waits for the acknowledgement. The client of the broker
    /// connects again by itself. While it has no connection, the publish waits until
    /// <see cref="AckTimeout"/>.</summary>
    private async Task<EventOutcome> PublishAsync(Message message, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(AckTimeout);
        try
        {
            var ack = await _js.PublishAsync(
                message.Subject,
                message.Body,
                // RetryAttempts is the number of sends, 1 by default: no second send.
                opts: new NatsJSPubOpts { MsgId = message.Id },
                headers: Headers,
                cancellationToken: timeout.Token
            );
            if (ack.Error is null)
                return EventOutcome.Published;
            LogNotAccepted(_logger, message.Subject, ack.Error.Description ?? "");
            return EventOutcome.Failed;
        }
        // At-most-once: in each case below, the event is lost.
        catch (NatsJSPublishNoResponseException)
        {
            // The broker answered that no stream has the subject.
            LogNoStream(_logger, message.Subject);
            return EventOutcome.NoStream;
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            LogBrokerFailed(_logger, e, message.Subject);
            return EventOutcome.Failed;
        }
    }

    private sealed record Message(string Subject, string Id, byte[] Body);

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

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The event broker has no stream for the event {Subject}"
    )]
    private static partial void LogNoStream(ILogger logger, string subject);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The event broker did not acknowledge the event {Subject}"
    )]
    private static partial void LogBrokerFailed(
        ILogger logger,
        Exception exception,
        string subject
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The event broker did not accept the event {Subject}: {Error}"
    )]
    private static partial void LogNotAccepted(ILogger logger, string subject, string error);
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
    NoStream,
    Dropped,
    Failed,
}

internal static class EventsSetup
{
    /// <summary>Registers the publisher when the configuration has the connection string of the
    /// broker. Without it, the server publishes no events. The connection does not wait for the
    /// broker: the client connects at the first publish, and again after each failure.</summary>
    public static IHostApplicationBuilder AddCustosEvents(this IHostApplicationBuilder builder)
    {
        if (
            builder.Configuration.GetConnectionString(EventPublisher.ConnectionName)
            is not { Length: > 0 } url
        )
        {
            return builder;
        }

        builder.Services.AddSingleton(services => new NatsConnection(
            new NatsOpts
            {
                Url = url,
                Name = "custos-server",
                InboxPrefix = InboxPrefix(url),
                RetryOnInitialConnect = true,
                LoggerFactory = services.GetRequiredService<ILoggerFactory>(),
            }
        ));
        builder.Services.AddSingleton<INatsJSContext>(services => new NatsJSContext(
            services.GetRequiredService<NatsConnection>()
        ));
        builder.Services.AddSingleton<EventPublisher>();
        builder.Services.AddHostedService(services =>
            services.GetRequiredService<EventPublisher>()
        );
        builder.Services.AddScoped<StockpileEvents>();
        return builder;
    }

    /// <summary>The reply subjects of the broker user in the URL, so that no other user reads the
    /// acknowledgements.</summary>
    private static string InboxPrefix(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.UserInfo.Split(':')[0] is { Length: > 0 } user
            ? $"_INBOX.{Uri.UnescapeDataString(user)}"
            : "_INBOX";
}
