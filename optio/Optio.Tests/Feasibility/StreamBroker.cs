using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using RabbitMQ.Client;

namespace Optio.Tests.Feasibility;

/// <summary>
/// A RabbitMQ container for the feasibility tests M37 of issue #48. It loads a copy of the
/// development definitions file with these additions:
/// <list type="bullet">
/// <item>The stream <see cref="Stream"/>, bound to <c>events.custos</c> with <c>#</c>.</item>
/// <item>A read permission of the user <c>optio</c> on the stream.</item>
/// <item>The user <c>optio-file</c>, with the permissions of <c>optio</c> in the file.</item>
/// <item>The user <c>admin</c>, with all permissions, to make the streams of a test.</item>
/// </list>
/// The password of each user is its name. The host port does not change at a restart.
/// </summary>
internal sealed class StreamBroker : IAsyncDisposable
{
    public const string Stream = "optio.events";
    public const string Exchange = "events.custos";
    public const string MaxAge = "15m";
    public const int SegmentBytes = 10_000_000;

    private readonly IContainer _container;
    private readonly int _port;

    private StreamBroker(IContainer container, int port)
    {
        _container = container;
        _port = port;
    }

    public static async Task<StreamBroker> StartAsync()
    {
        var port = FreePort();
        var container = new ContainerBuilder("rabbitmq:4.3-alpine")
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(await DefinitionsAsync()),
                FilePath.Of("/etc/rabbitmq/definitions.json")
            )
            .WithResourceMapping(
                "load_definitions = /etc/rabbitmq/definitions.json\n"u8.ToArray(),
                FilePath.Of("/etc/rabbitmq/conf.d/20-definitions.conf")
            )
            .WithPortBinding(port, 5672)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilMessageIsLogged("Server startup complete")
            )
            .Build();
        await container.StartAsync();
        return new StreamBroker(container, port);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>Stops and starts the same container. The data of the streams stays.</summary>
    public async Task RestartAsync()
    {
        await _container.StopAsync();
        await _container.StartAsync();
    }

    /// <summary>Runs <c>rabbitmqctl</c> in the container and gives its standard output.</summary>
    public Task<string> CtlAsync(params string[] arguments) =>
        ExecAsync(["rabbitmqctl", .. arguments]);

    /// <summary>Runs a command in the container and gives its standard output.</summary>
    public async Task<string> ExecAsync(params string[] command)
    {
        var result = await _container.ExecAsync(command);
        Assert.That(result.ExitCode, Is.Zero, result.Stderr);
        return result.Stdout;
    }

    public async Task<IConnection> ConnectAsync(string user, bool recovery = false) =>
        await new ConnectionFactory
        {
            HostName = "localhost",
            Port = _port,
            UserName = user,
            Password = user,
            AutomaticRecoveryEnabled = recovery,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(1),
        }.CreateConnectionAsync();

    /// <summary>
    /// Makes a stream as <c>admin</c> and binds it to <see cref="Exchange"/> with
    /// <paramref name="bindingKey"/>.
    /// </summary>
    public async Task DeclareStreamAsync(
        string name,
        string bindingKey,
        string maxAge = MaxAge,
        int segmentBytes = SegmentBytes
    )
    {
        await using var admin = await ConnectAsync("admin");
        await using var channel = await admin.CreateChannelAsync();
        await channel.QueueDeclareAsync(
            name,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "stream",
                ["x-max-age"] = maxAge,
                ["x-stream-max-segment-size-bytes"] = segmentBytes,
            }
        );
        await channel.QueueBindAsync(name, Exchange, bindingKey);
    }

    public async Task DeleteStreamAsync(string name)
    {
        await using var admin = await ConnectAsync("admin");
        await using var channel = await admin.CreateChannelAsync();
        await channel.QueueDeleteAsync(name);
    }

    /// <summary>
    /// Publishes <paramref name="count"/> messages as <c>custos</c>, fire and forget. The body
    /// of each message is its sequence number.
    /// </summary>
    public async Task PublishAsync(
        string routingKey,
        int count,
        int first = 0,
        IDictionary<string, object?>? headers = null
    )
    {
        await using var custos = await ConnectAsync("custos");
        await using var channel = await custos.CreateChannelAsync();
        for (var i = first; i < first + count; i++)
            await PublishOneAsync(channel, routingKey, i, headers);
        // The publish ends when the frame is on the socket. The wait lets the broker write it.
        await Task.Delay(300);
    }

    public static ValueTask PublishOneAsync(
        IChannel channel,
        string routingKey,
        int sequence,
        IDictionary<string, object?>? headers = null,
        bool mandatory = false
    ) =>
        channel.BasicPublishAsync(
            Exchange,
            routingKey,
            mandatory,
            new BasicProperties { Headers = headers },
            Encoding.UTF8.GetBytes(sequence.ToString())
        );

    /// <summary>The names and sizes of the segment files of a stream.</summary>
    public async Task<string> SegmentsAsync(string stream) =>
        string.Join(
            " ",
            (
                await ExecAsync(
                    "sh",
                    "-c",
                    $"find /var/lib/rabbitmq -path '*{stream}*' -name '*.segment' "
                        + "-exec ls -l {} +"
                )
            )
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Select(f => $"{Path.GetFileName(f[^1])}:{f[4]}")
                .Order()
        );

    /// <summary>The offset of the first message that a consumer at <c>first</c> gets.</summary>
    public async Task<long?> FirstOffsetAsync(string stream)
    {
        await using var admin = await ConnectAsync("admin");
        await using var reader = await StreamReader.OpenAsync(admin, stream, "first", 1);
        var deliveries = await reader.ReadAsync(1, TimeSpan.FromSeconds(2));
        return deliveries.Count == 0 ? null : deliveries[0].Offset;
    }

    private static async Task<string> DefinitionsAsync()
    {
        var definitions = JsonNode.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "rabbitmq", "definitions.json")
            )
        )!;
        var users = definitions["users"]!.AsArray();
        users.Add(User("optio-file"));
        users.Add(User("admin"));

        var permissions = definitions["permissions"]!.AsArray();
        var optio = permissions.Single(p => (string)p!["user"]! == "optio")!;
        var file = optio.DeepClone();
        file["user"] = "optio-file";
        permissions.Add(file);
        // The change that lets Optio read the stream.
        optio["read"] = @"^(amq\.gen-.*|events\..+|optio\.events)$";
        permissions.Add(
            new JsonObject
            {
                ["user"] = "admin",
                ["vhost"] = "/",
                ["configure"] = ".*",
                ["write"] = ".*",
                ["read"] = ".*",
            }
        );

        definitions["queues"] = new JsonArray(
            new JsonObject
            {
                ["name"] = Stream,
                ["vhost"] = "/",
                ["durable"] = true,
                ["auto_delete"] = false,
                ["arguments"] = new JsonObject
                {
                    ["x-queue-type"] = "stream",
                    ["x-max-age"] = MaxAge,
                    ["x-stream-max-segment-size-bytes"] = SegmentBytes,
                },
            }
        );
        definitions["bindings"] = new JsonArray(
            new JsonObject
            {
                ["source"] = Exchange,
                ["vhost"] = "/",
                ["destination"] = Stream,
                ["destination_type"] = "queue",
                ["routing_key"] = "#",
                ["arguments"] = new JsonObject(),
            }
        );
        definitions["exchanges"]!
            .AsArray()
            .Add(
                new JsonObject
                {
                    ["name"] = "events.unbound",
                    ["vhost"] = "/",
                    ["type"] = "topic",
                    ["durable"] = true,
                    ["auto_delete"] = false,
                    ["internal"] = false,
                    ["arguments"] = new JsonObject(),
                }
            );
        return definitions.ToJsonString();
    }

    /// <summary>A user with the password of its name (rabbit_password_hashing_sha256).</summary>
    private static JsonObject User(string name)
    {
        var salt = RandomNumberGenerator.GetBytes(4);
        byte[] hash = [.. salt, .. SHA256.HashData([.. salt, .. Encoding.UTF8.GetBytes(name)])];
        return new JsonObject
        {
            ["name"] = name,
            ["password_hash"] = Convert.ToBase64String(hash),
            ["hashing_algorithm"] = "rabbit_password_hashing_sha256",
            ["tags"] = new JsonArray(),
        };
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>One message that a stream consumer got.</summary>
internal sealed record Delivery(long Offset, string RoutingKey, int Sequence, string? Filter);

/// <summary>
/// A stream consumer on its own channel. It acknowledges each message, because the broker
/// sends no more than the prefetch count of messages that wait for an acknowledgement.
/// </summary>
internal sealed class StreamReader : AsyncDefaultBasicConsumer, IAsyncDisposable
{
    private readonly Channel<Delivery> _deliveries =
        System.Threading.Channels.Channel.CreateUnbounded<Delivery>();
    private readonly bool _acknowledge;

    private StreamReader(IChannel channel, bool acknowledge)
        : base(channel)
    {
        _acknowledge = acknowledge;
    }

    /// <summary>True when the broker cancelled the consumer (basic.cancel).</summary>
    public bool CancelledByBroker { get; private set; }

    public static async Task<StreamReader> OpenAsync(
        IConnection connection,
        string stream,
        object? offset,
        ushort prefetch = 1000,
        IDictionary<string, object?>? arguments = null,
        bool acknowledge = true
    )
    {
        var channel = await connection.CreateChannelAsync();
        await channel.BasicQosAsync(0, prefetch, false);
        var reader = new StreamReader(channel, acknowledge);
        var all = new Dictionary<string, object?>(arguments ?? new Dictionary<string, object?>());
        if (offset is not null)
            all["x-stream-offset"] = offset;
        await channel.BasicConsumeAsync(stream, false, "", false, false, all, reader);
        return reader;
    }

    /// <summary>
    /// Reads until <paramref name="count"/> messages arrived, or until no message arrives for
    /// <paramref name="quiet"/>.
    /// </summary>
    public async Task<List<Delivery>> ReadAsync(int count, TimeSpan quiet)
    {
        var result = new List<Delivery>();
        while (result.Count < count)
        {
            using var timeout = new CancellationTokenSource(quiet);
            try
            {
                result.Add(await _deliveries.Reader.ReadAsync(timeout.Token));
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (Channel.IsOpen)
            await Channel.CloseAsync();
        Channel.Dispose();
    }

    public override async Task HandleBasicDeliverAsync(
        string consumerTag,
        ulong deliveryTag,
        bool redelivered,
        string exchange,
        string routingKey,
        IReadOnlyBasicProperties properties,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default
    )
    {
        var headers = properties.Headers;
        var offset =
            headers is not null && headers.TryGetValue("x-stream-offset", out var o)
                ? Convert.ToInt64(o)
                : -1;
        var filter =
            headers is not null && headers.TryGetValue("x-stream-filter-value", out var f)
                ? Encoding.UTF8.GetString((byte[])f!)
                : null;
        await _deliveries.Writer.WriteAsync(
            new Delivery(offset, routingKey, int.Parse(Encoding.UTF8.GetString(body.Span)), filter),
            cancellationToken
        );
        if (_acknowledge)
            await Channel.BasicAckAsync(deliveryTag, false, cancellationToken);
    }

    public override Task HandleBasicCancelAsync(
        string consumerTag,
        CancellationToken cancellationToken = default
    )
    {
        CancelledByBroker = true;
        return base.HandleBasicCancelAsync(consumerTag, cancellationToken);
    }
}
