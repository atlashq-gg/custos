using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Custos.Server.Tests.Infrastructure;

/// <summary>
/// NATS with the configuration of development (deploy/nats/nats.conf): the producer user
/// <c>custos</c> and the reader user <c>optio</c>. The password of each user is its name. The
/// stream is the one that Optio makes, with the settings of optio/docs/producers.md.
/// </summary>
internal sealed class Broker : IAsyncDisposable
{
    public const string Stream = "OPTIO";

    private readonly IContainer _container;

    private Broker(IContainer container) => _container = container;

    /// <summary>The connection string of the server.</summary>
    public string ConnectionString => Url("custos");

    /// <summary>Starts the broker. With <paramref name="stream"/>, it makes the stream as
    /// <c>optio</c>.</summary>
    public static async Task<Broker> StartAsync(bool stream = true)
    {
        var config = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "nats", "nats.conf")
        );
        var container = new ContainerBuilder("nats:2.15.3-alpine")
            .WithResourceMapping(Encoding.UTF8.GetBytes(config), FilePath.Of("/etc/nats/nats.conf"))
            .WithEnvironment("CUSTOS_PASSWORD", "custos")
            .WithEnvironment("OPTIO_PASSWORD", "optio")
            .WithCommand("-c", "/etc/nats/nats.conf")
            .WithPortBinding(4222, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server is ready"))
            .Build();
        await container.StartAsync();
        var broker = new Broker(container);
        if (stream)
        {
            await using var optio = broker.Connect("optio");
            await new NatsJSContext(optio).CreateStreamAsync(
                new StreamConfig(Stream, ["events.>"])
                {
                    MaxAge = TimeSpan.FromMinutes(15),
                    DuplicateWindow = TimeSpan.FromMinutes(2),
                }
            );
        }
        return broker;
    }

    /// <summary>Reads the stream as the user <c>optio</c>, from the next event on.</summary>
    public async Task<StreamReader> ReadAsync()
    {
        var connection = Connect("optio");
        var js = new NatsJSContext(connection);
        var last = (await js.GetStreamAsync(Stream)).Info.State.LastSeq;
        var consumer = await js.CreateOrderedConsumerAsync(
            Stream,
            new NatsJSOrderedConsumerOpts
            {
                DeliverPolicy = ConsumerConfigDeliverPolicy.ByStartSequence,
                OptStartSeq = last + 1,
            }
        );
        var reader = new StreamReader(connection);
        reader.Start(async cancellationToken =>
        {
            await foreach (
                var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: cancellationToken)
            )
            {
                await reader.Received.Writer.WriteAsync(
                    new Received(
                        msg.Subject,
                        msg.Headers?["Content-Type"].ToString(),
                        msg.Headers?["Nats-Msg-Id"].ToString(),
                        JsonDocument.Parse(msg.Data).RootElement.Clone()
                    ),
                    cancellationToken
                );
            }
        });
        return reader;
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    private string Url(string user) =>
        $"nats://{user}:{user}@{_container.Hostname}:{_container.GetMappedPublicPort(4222)}";

    private NatsConnection Connect(string user) =>
        new(new NatsOpts { Url = Url(user), InboxPrefix = $"_INBOX.{user}" });

    internal sealed record Received(
        string Subject,
        string? ContentType,
        string? MessageId,
        JsonElement Body
    );

    internal sealed class StreamReader(NatsConnection connection) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Task _reading = Task.CompletedTask;

        public Channel<Received> Received { get; } = Channel.CreateUnbounded<Received>();

        public void Start(Func<CancellationToken, Task> read) => _reading = read(_stop.Token);

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

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try
            {
                await _reading;
            }
            catch (OperationCanceledException) { }
            _stop.Dispose();
            await connection.DisposeAsync();
        }
    }
}
