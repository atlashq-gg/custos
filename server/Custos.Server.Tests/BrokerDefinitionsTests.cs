using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Custos.Server.Tests;

/// <summary>
/// The feasibility test M25 of issue #48 for the broker: the users and the permissions of the
/// development definitions file. A producer writes signals to its own exchange only. optio reads
/// them through a queue that the broker names. Needs Docker.
/// </summary>
public sealed class BrokerDefinitionsTests
{
    private const string Signal = "300000000000000001.stockpile.changed";

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
    public async Task Optio_gets_the_signal_that_the_producer_writes_to_its_exchange()
    {
        await using var optio = await ConnectAsync("optio");
        await using var reader = await optio.CreateChannelAsync();
        var queue = await reader.QueueDeclareAsync();
        await reader.QueueBindAsync(queue.QueueName, "events.custos", "300000000000000001.#");
        var received = Channel.CreateUnbounded<string>();
        var consumer = new AsyncEventingBasicConsumer(reader);
        consumer.ReceivedAsync += async (_, delivery) =>
            await received.Writer.WriteAsync(delivery.RoutingKey);
        await reader.BasicConsumeAsync(queue.QueueName, autoAck: true, consumer);

        await using var custos = await ConnectAsync("custos");
        await using var writer = await ConfirmedChannelAsync(custos);
        await PublishAsync(writer, "events.custos", "300000000000000002.stockpile.changed", []);
        await PublishAsync(writer, "events.custos", Signal, "{}"u8.ToArray());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.That(await received.Reader.ReadAsync(timeout.Token), Is.EqualTo(Signal));
    }

    [Test]
    public async Task Producer_cannot_write_to_the_exchange_of_another_producer()
    {
        await using var custos = await ConnectAsync("custos");
        await using var channel = await ConfirmedChannelAsync(custos);

        await AssertRefusedAsync(channel, () => PublishAsync(channel, "events.other", Signal, []));
    }

    [Test]
    public async Task Producer_cannot_make_a_queue_and_thus_cannot_read()
    {
        await using var custos = await ConnectAsync("custos");
        await using var channel = await custos.CreateChannelAsync();

        await AssertRefusedAsync(channel, () => channel.QueueDeclareAsync());
    }

    [Test]
    public async Task Optio_cannot_write_a_signal()
    {
        await using var optio = await ConnectAsync("optio");
        await using var channel = await ConfirmedChannelAsync(optio);

        await AssertRefusedAsync(channel, () => PublishAsync(channel, "events.custos", Signal, []));
    }

    [Test]
    public async Task Optio_cannot_make_a_queue_with_a_name_of_its_own()
    {
        await using var optio = await ConnectAsync("optio");
        await using var channel = await optio.CreateChannelAsync();

        await AssertRefusedAsync(channel, () => channel.QueueDeclareAsync("signals"));
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
        byte[] body
    ) =>
        channel
            .BasicPublishAsync(exchange, routingKey, false, new BasicProperties(), body)
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
