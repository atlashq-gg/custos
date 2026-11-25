using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Optio.Tests.Feasibility;

/// <summary>
/// A NATS container for the feasibility tests M38 of issue #48. It loads a copy of
/// <c>optio/nats/nats.conf</c> with one more user, <c>admin</c>, that has all permissions. The
/// password of each user is its name. The host port does not change at a restart.
/// </summary>
internal sealed class NatsBroker : IAsyncDisposable
{
    public const string Image = "nats:2.15.3-alpine";
    public const string Stream = "OPTIO";
    private const string ConfigPath = "/etc/nats/nats.conf";

    private readonly IContainer _container;

    private NatsBroker(IContainer container, int port)
    {
        _container = container;
        Port = port;
    }

    public int Port { get; }

    /// <summary>Starts a broker on <paramref name="port"/>, or on a free port.</summary>
    public static async Task<NatsBroker> StartAsync(int? port = null)
    {
        var hostPort = port ?? FreePort();
        var container = new ContainerBuilder(Image)
            .WithResourceMapping(Encoding.UTF8.GetBytes(Config()), FilePath.Of(ConfigPath))
            .WithEnvironment("CUSTOS_PASSWORD", "custos")
            .WithEnvironment("OPTIO_PASSWORD", "optio")
            .WithCommand("-c", ConfigPath)
            .WithPortBinding(hostPort, 4222)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server is ready"))
            .Build();
        await container.StartAsync();
        return new NatsBroker(container, hostPort);
    }

    /// <summary>The configuration file of the repository, with the user <c>admin</c>.
    /// <paramref name="change"/> can change the text first.</summary>
    public static string Config(Func<string, string>? change = null)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "nats", "nats.conf"));
        text = change?.Invoke(text) ?? text;
        return text.Replace(
            "users = [",
            "users = [\n    { user: admin, password: admin }",
            StringComparison.Ordinal
        );
    }

    public static string Url(string user, int port) => $"nats://{user}:{user}@localhost:{port}";

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>A connection as <paramref name="user"/>, with the reply subjects of the user, as
    /// the services use it. Requests wait 2 seconds.</summary>
    public NatsConnection Connect(string user, Func<NatsOpts, NatsOpts>? change = null)
    {
        var options = new NatsOpts
        {
            Url = Url(user, Port),
            Name = user,
            InboxPrefix = user == "admin" ? "_INBOX" : $"_INBOX.{user}",
            RequestTimeout = TimeSpan.FromSeconds(2),
        };
        return new NatsConnection(change?.Invoke(options) ?? options);
    }

    public async Task<INatsJSStream> CreateStreamAsync(StreamConfig config)
    {
        await using var admin = Connect("admin");
        return await new NatsJSContext(admin).CreateStreamAsync(config);
    }

    public async Task<StreamInfo> InfoAsync(string stream = Stream)
    {
        await using var admin = Connect("admin");
        return (await new NatsJSContext(admin).GetStreamAsync(stream)).Info;
    }

    /// <summary>The stream of the design: the events now, the query messages later.</summary>
    public static StreamConfig StreamConfig(TimeSpan? maxAge = null, string name = Stream)
    {
        var age = maxAge ?? TimeSpan.FromMinutes(15);
        return new StreamConfig(name, ["events.>", "queries.>"])
        {
            Retention = StreamConfigRetention.Limits,
            Storage = StreamConfigStorage.File,
            Discard = StreamConfigDiscard.Old,
            MaxAge = age,
            MaxBytes = 100_000_000,
            DuplicateWindow = age < TimeSpan.FromMinutes(2) ? age : TimeSpan.FromMinutes(2),
            AllowRollupHdrs = false,
            DenyDelete = true,
            DenyPurge = true,
        };
    }

    /// <summary>Writes a new configuration and makes the broker read it again.</summary>
    public async Task ReloadAsync(string config)
    {
        await _container.CopyAsync(Encoding.UTF8.GetBytes(config), ConfigPath);
        var result = await _container.ExecAsync(["kill", "-HUP", "1"]);
        Assert.That(result.ExitCode, Is.Zero, result.Stderr);
        await Task.Delay(500);
    }

    /// <summary>Stops and starts the same container. The data of the streams stays.</summary>
    public async Task RestartAsync()
    {
        await _container.StopAsync();
        await _container.StartAsync();
    }

    /// <summary>Kills the server with SIGKILL, then starts the same container.</summary>
    public async Task KillAndStartAsync()
    {
        await DockerAsync("kill", "--signal", "KILL", _container.Id);
        await _container.StartAsync();
    }

    public Task PauseAsync() => DockerAsync("pause", _container.Id);

    public Task UnpauseAsync() => DockerAsync("unpause", _container.Id);

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    private static async Task DockerAsync(params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("docker", arguments))!;
        await process.WaitForExitAsync();
        Assert.That(process.ExitCode, Is.Zero, $"docker {string.Join(' ', arguments)}");
    }
}
