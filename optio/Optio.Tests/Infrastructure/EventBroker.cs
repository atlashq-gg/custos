using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using RabbitMQ.Client;

namespace Optio.Tests.Infrastructure;

/// <summary>
/// RabbitMQ with the definitions file of development (optio/rabbitmq/definitions.json). The
/// password of each user is its name. Needs Docker.
/// </summary>
internal sealed class EventBroker : IAsyncDisposable
{
    private readonly IContainer _container;

    private EventBroker(IContainer container) => _container = container;

    /// <summary>The connection string of optio.</summary>
    public string OptioConnectionString =>
        $"amqp://optio:optio@{_container.Hostname}:{_container.GetMappedPublicPort(5672)}";

    public static async Task<EventBroker> StartAsync()
    {
        var container = new ContainerBuilder("rabbitmq:4.3-alpine")
            .WithResourceMapping(
                await File.ReadAllBytesAsync(
                    Path.Combine(AppContext.BaseDirectory, "rabbitmq", "definitions.json")
                ),
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
        await container.StartAsync();
        return new EventBroker(container);
    }

    /// <summary>Publishes an event of the producer <c>custos</c>, as the server does.</summary>
    public async Task PublishAsync(string body, ulong guild)
    {
        await using var connection = await new ConnectionFactory
        {
            HostName = _container.Hostname,
            Port = _container.GetMappedPublicPort(5672),
            UserName = "custos",
            Password = "custos",
        }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true
            )
        );
        await channel.BasicPublishAsync(
            "events.custos",
            $"{guild}.stockpile.changed",
            mandatory: true,
            new BasicProperties(),
            Encoding.UTF8.GetBytes(body)
        );
    }

    /// <summary>A CloudEvents document of the producer <c>custos</c>.</summary>
    public static string Event(string text, ulong guild, ulong role, int version = 1) =>
        JsonSerializer.Serialize(
            new
            {
                specversion = "1.0",
                id = Guid.NewGuid().ToString(),
                source = "custos",
                type = "stockpile.changed",
                time = DateTimeOffset.UtcNow,
                data = new
                {
                    policy = new
                    {
                        version,
                        guild = guild.ToString(),
                        roles = new[] { role.ToString() },
                    },
                    message = new { text },
                },
            }
        );

    /// <summary>Closes each client connection, as a network failure does.</summary>
    public async Task CloseConnectionsAsync()
    {
        var result = await _container.ExecAsync(["rabbitmqctl", "close_all_connections", "test"]);
        Assert.That(result.ExitCode, Is.Zero, result.Stderr);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
