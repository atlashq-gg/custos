using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Optio.Tests;

/// <summary>
/// The users, the permissions, and the stream of the development definitions file
/// (optio/docs/producers.md). A producer publishes only to its own exchange, and the broker keeps
/// each event in the stream of the producer. Optio only reads the stream. Needs Docker.
/// </summary>
public sealed class BrokerDefinitionsTests
{
    private const string Exchange = "events.custos";
    private const string Stream = "events.custos.stream";
    private const string RoutingKey = "300000000000000001.stockpile.changed";

    private IContainer _broker = null!;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        // The definitions of the repository, and one more exchange: the exchange of another
        // producer, which the producer custos must not write to.
        var definitions = JsonNode.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "rabbitmq", "definitions.json")
            )
        )!;
        definitions["exchanges"]!
            .AsArray()
            .Add(
                new JsonObject
                {
                    ["name"] = "events.other",
                    ["vhost"] = "/",
                    ["type"] = "topic",
                    ["durable"] = true,
                    ["auto_delete"] = false,
                    ["internal"] = false,
                    ["arguments"] = new JsonObject(),
                }
            );

        _broker = new ContainerBuilder("rabbitmq:4.3-alpine")
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(definitions.ToJsonString()),
                FilePath.Of("/etc/rabbitmq/definitions.json")
            )
            .WithResourceMapping(
                "load_definitions = /etc/rabbitmq/definitions.json\n"u8.ToArray(),
                FilePath.Of("/etc/rabbitmq/conf.d/20-definitions.conf")
            )
            .WithPortBinding(5672, assignRandomHostPort: true)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilMessageIsLogged("Server startup complete")
            )
            .Build();
        await _broker.StartAsync();
    }

    [OneTimeTearDown]
    public async Task StopAsync() => await _broker.DisposeAsync();

    [Test]
    public async Task Optio_reads_the_event_that_the_producer_publishes_with_mandatory()
    {
        var id = Guid.NewGuid().ToString();
        await using var custos = await ConnectAsync("custos");
        await using var writer = await ConfirmedChannelAsync(custos);
        var returned = 0;
        writer.BasicReturnAsync += (_, _) =>
        {
            Interlocked.Increment(ref returned);
            return Task.CompletedTask;
        };
        await PublishAsync(writer, Exchange, RoutingKey, "{}"u8.ToArray(), id, mandatory: true);

        // A stream consumer needs a prefetch, manual acknowledgements, and an offset.
        await using var optio = await ConnectAsync("optio");
        await using var reader = await optio.CreateChannelAsync();
        await reader.BasicQosAsync(0, 100, false);
        var received = Channel.CreateUnbounded<(string? Id, string RoutingKey)>();
        var consumer = new AsyncEventingBasicConsumer(reader);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            await received.Writer.WriteAsync(
                (delivery.BasicProperties.MessageId, delivery.RoutingKey)
            );
            await reader.BasicAckAsync(delivery.DeliveryTag, false);
        };
        await reader.BasicConsumeAsync(
            Stream,
            autoAck: false,
            consumerTag: "",
            noLocal: false,
            exclusive: false,
            arguments: new Dictionary<string, object?> { ["x-stream-offset"] = "first" },
            consumer
        );

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        (string? Id, string RoutingKey) delivery;
        do
        {
            delivery = await received.Reader.ReadAsync(timeout.Token);
        } while (delivery.Id != id);
        Assert.That(delivery.RoutingKey, Is.EqualTo(RoutingKey));
        Assert.That(returned, Is.Zero);
    }

    [Test]
    public async Task Producer_cannot_write_to_the_exchange_of_another_producer()
    {
        await using var custos = await ConnectAsync("custos");
        await using var channel = await ConfirmedChannelAsync(custos);

        await AssertRefusedAsync(channel, () =>
            PublishAsync(channel, "events.other", RoutingKey, [])
        );
    }

    [Test]
    public async Task Producer_cannot_read_the_stream()
    {
        await using var custos = await ConnectAsync("custos");
        await using var channel = await custos.CreateChannelAsync();
        await channel.BasicQosAsync(0, 100, false);

        await AssertRefusedAsync(channel, () =>
            channel.BasicConsumeAsync(Stream, false, new AsyncEventingBasicConsumer(channel))
        );
    }

    [Test]
    public async Task Producer_cannot_make_a_queue()
    {
        await using var custos = await ConnectAsync("custos");
        await using var channel = await custos.CreateChannelAsync();

        await AssertRefusedAsync(channel, () => channel.QueueDeclareAsync());
    }

    [Test]
    public async Task Optio_cannot_publish_to_the_exchange()
    {
        await using var optio = await ConnectAsync("optio");
        await using var channel = await ConfirmedChannelAsync(optio);

        await AssertRefusedAsync(channel, () => PublishAsync(channel, Exchange, RoutingKey, []));
    }

    [Test]
    public async Task Optio_cannot_publish_to_the_stream()
    {
        await using var optio = await ConnectAsync("optio");
        await using var channel = await ConfirmedChannelAsync(optio);

        await AssertRefusedAsync(channel, () => PublishAsync(channel, "", Stream, []));
    }

    [Test]
    public async Task Optio_cannot_make_a_queue()
    {
        await using var optio = await ConnectAsync("optio");
        await using var channel = await optio.CreateChannelAsync();

        await AssertRefusedAsync(channel, () => channel.QueueDeclareAsync());
    }

    [Test]
    public async Task Optio_cannot_delete_the_stream()
    {
        await using var optio = await ConnectAsync("optio");
        await using var channel = await optio.CreateChannelAsync();

        await AssertRefusedAsync(channel, () => channel.QueueDeleteAsync(Stream));
    }

    private async Task<IConnection> ConnectAsync(string user) =>
        await new ConnectionFactory
        {
            HostName = _broker.Hostname,
            Port = _broker.GetMappedPublicPort(5672),
            UserName = user,
            Password = user,
        }.CreateConnectionAsync();

    private static Task PublishAsync(
        IChannel channel,
        string exchange,
        string routingKey,
        byte[] body,
        string? id = null,
        bool mandatory = false
    ) =>
        channel
            .BasicPublishAsync(
                exchange,
                routingKey,
                mandatory,
                new BasicProperties { MessageId = id },
                body
            )
            .AsTask();

    private static Task<IChannel> ConfirmedChannelAsync(IConnection connection) =>
        connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true
            )
        );

    /// <summary>The broker closes the channel with 403 ACCESS_REFUSED.</summary>
    private static async Task AssertRefusedAsync(IChannel channel, Func<Task> action)
    {
        await Assert.ThatAsync(async () => await action(), Throws.Exception);
        await Task.Delay(100);
        Assert.That(channel.CloseReason?.ReplyCode, Is.EqualTo(403), $"{channel.CloseReason}");
    }
}
