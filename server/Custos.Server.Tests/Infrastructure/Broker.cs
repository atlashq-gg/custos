using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Custos.Server.Tests.Infrastructure;

/// <summary>
/// RabbitMQ with the definitions file of development (optio/rabbitmq/definitions.json): the
/// exchange <c>events.custos</c>, its stream, the producer user <c>custos</c>, and the reader user
/// <c>optio</c>. The password of each user is its name.
/// </summary>
internal sealed class Broker : IAsyncDisposable
{
    public const string Stream = "events.custos.stream";

    private readonly IContainer _container;

    private Broker(IContainer container) => _container = container;

    /// <summary>The connection string of the server.</summary>
    public string ConnectionString =>
        $"amqp://custos:custos@{_container.Hostname}:{_container.GetMappedPublicPort(5672)}";

    /// <summary>Starts the broker. Without <paramref name="bound"/>, the definitions have no
    /// binding, so that the broker returns each event of the server.</summary>
    public static async Task<Broker> StartAsync(bool bound = true)
    {
        var definitions = JsonNode
            .Parse(
                await File.ReadAllTextAsync(
                    Path.Combine(AppContext.BaseDirectory, "rabbitmq", "definitions.json")
                )
            )!
            .AsObject();
        if (!bound)
            definitions.Remove("bindings");

        var container = new ContainerBuilder("rabbitmq:4.3-alpine")
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(definitions.ToJsonString()),
                FilePath.Of("/etc/rabbitmq/definitions.json")
            )
            .WithResourceMapping(
                Encoding.UTF8.GetBytes("load_definitions = /etc/rabbitmq/definitions.json\n"),
                FilePath.Of("/etc/rabbitmq/conf.d/20-definitions.conf")
            )
            .WithPortBinding(5672, assignRandomHostPort: true)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilMessageIsLogged("Server startup complete")
            )
            .Build();
        await container.StartAsync();
        return new Broker(container);
    }

    /// <summary>Reads the stream as the user <c>optio</c>, from the next event on.</summary>
    public async Task<StreamReader> ReadAsync()
    {
        var connection = await new ConnectionFactory
        {
            HostName = _container.Hostname,
            Port = _container.GetMappedPublicPort(5672),
            UserName = "optio",
            Password = "optio",
        }.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        // A stream consumer needs a prefetch and manual acknowledgements.
        await channel.BasicQosAsync(0, 100, false);
        var reader = new StreamReader(connection);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            await reader.Received.Writer.WriteAsync(
                new Received(
                    delivery.RoutingKey,
                    delivery.BasicProperties.ContentType,
                    JsonDocument.Parse(delivery.Body).RootElement.Clone()
                )
            );
            await channel.BasicAckAsync(delivery.DeliveryTag, false);
        };
        await channel.BasicConsumeAsync(
            Stream,
            autoAck: false,
            consumerTag: "",
            noLocal: false,
            exclusive: false,
            arguments: new Dictionary<string, object?> { ["x-stream-offset"] = "next" },
            consumer
        );
        return reader;
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    internal sealed record Received(string RoutingKey, string? ContentType, JsonElement Body);

    internal sealed class StreamReader(IConnection connection) : IAsyncDisposable
    {
        public Channel<Received> Received { get; } = Channel.CreateUnbounded<Received>();

        public async Task<Received> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await Received.Reader.ReadAsync(timeout.Token);
        }

        public async Task<bool> AnyWithinAsync(TimeSpan wait)
        {
            using var timeout = new CancellationTokenSource(wait);
            try
            {
                await Received.Reader.ReadAsync(timeout.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
