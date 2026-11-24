using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using NetCord;
using NetCord.Gateway;

namespace Optio.Tests.Feasibility;

/// <summary>
/// A gateway connection with the intents <c>Guilds</c> and <c>GuildUsers</c>, for the
/// feasibility tests T2 of M37. It records the events that the tests examine.
/// </summary>
internal sealed class DiscordGateway : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Request> _requests = new();
    private readonly Channel<GuildUser> _updates =
        System.Threading.Channels.Channel.CreateUnbounded<GuildUser>();
    private TaskCompletionSource _guildCreates = new();
    private HashSet<ulong> _waiting = [];
    private ulong[] _guildIds = [];

    private DiscordGateway(string token, IGatewayClientCacheProvider? cache)
    {
        Client = new GatewayClient(
            new BotToken(token),
            new GatewayClientConfiguration
            {
                Intents = GatewayIntents.Guilds | GatewayIntents.GuildUsers,
                CacheProvider = cache,
            }
        );
        Client.GuildCreate += args =>
        {
            GuildCreates.Enqueue(
                new GuildCreated(
                    args.GuildId,
                    Clock.Elapsed,
                    args.Guild?.Users.Count ?? -1,
                    Client!.Cache.Guilds.TryGetValue(args.GuildId, out var cached)
                        && ReferenceEquals(cached, args.Guild)
                )
            );
            lock (_waiting)
            {
                if (_waiting.Remove(args.GuildId) && _waiting.Count == 0)
                    _guildCreates.TrySetResult();
            }
            return default;
        };
        Client.GuildUserChunk += args =>
        {
            if (args.Nonce is { } nonce && _requests.TryGetValue(nonce, out var request))
            {
                request.Chunks.Enqueue(args);
                request.InCacheAtEvent =
                    Client!.Cache.Guilds.TryGetValue(args.GuildId, out var guild)
                    && args.Users.All(u => guild.Users.ContainsKey(u.Id));
                if (args.ChunkIndex == args.ChunkCount - 1)
                    request.Done.TrySetResult(null);
            }
            return default;
        };
        Client.RateLimited += args =>
        {
            RateLimits.Enqueue(args);
            if (
                args.Metadata is RequestGuildUsersRateLimitMetadata { Nonce: { } nonce }
                && _requests.TryGetValue(nonce, out var request)
            )
                request.Done.TrySetResult(args);
            return default;
        };
        Client.GuildUserUpdate += user => _updates.Writer.WriteAsync(user);
        Client.Disconnect += args =>
        {
            Disconnects.Enqueue(args);
            return default;
        };
        Client.Resume += () =>
        {
            Interlocked.Increment(ref _resumes);
            return default;
        };
        Client.Ready += _ =>
        {
            Interlocked.Increment(ref _readies);
            return default;
        };
    }

    private int _resumes;
    private int _readies;

    public GatewayClient Client { get; }

    /// <summary>The time from the start of the connection.</summary>
    public Stopwatch Clock { get; } = new();

    public ConcurrentQueue<GuildCreated> GuildCreates { get; } = new();

    public ConcurrentQueue<RateLimitedEventArgs> RateLimits { get; } = new();

    public ConcurrentQueue<DisconnectEventArgs> Disconnects { get; } = new();

    public int Resumes => _resumes;

    public int Readies => _readies;

    /// <summary>
    /// Connects and waits for <c>GUILD_CREATE</c> of each guild in <paramref name="guilds"/>.
    /// </summary>
    public static async Task<DiscordGateway> StartAsync(
        string token,
        IEnumerable<ulong> guilds,
        IGatewayClientCacheProvider? cache = null
    )
    {
        var gateway = new DiscordGateway(token, cache);
        await gateway.StartAgainAsync(guilds);
        return gateway;
    }

    /// <summary>Starts a new session and waits for <c>GUILD_CREATE</c> of each guild.</summary>
    public async Task StartAgainAsync(IEnumerable<ulong> guilds)
    {
        lock (_waiting)
        {
            _waiting = [.. guilds];
            _guildIds = [.. _waiting];
            _guildCreates = new TaskCompletionSource();
        }
        Clock.Restart();
        await Client.StartAsync();
        await WaitForGuildCreatesAsync();
        // The event handler can run before NetCord puts the guild in the cache.
        while (!_guildIds.All(Client.Cache.Guilds.ContainsKey))
            await Task.Delay(10);
    }

    public async Task WaitForGuildCreatesAsync()
    {
        try
        {
            await _guildCreates.Task.WaitAsync(Timeout);
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                "No GUILD_CREATE for each guild. Disconnects: "
                    + string.Join(", ", Disconnects.Select(d => $"{d.CloseStatus}"))
            );
        }
    }

    /// <summary>
    /// Sends a member request (opcode 8) and waits for its last chunk or its rate limit.
    /// <paramref name="userIds"/> null requests the full list.
    /// </summary>
    public async Task<RequestResult> RequestUsersAsync(ulong guildId, IEnumerable<ulong>? userIds)
    {
        var nonce = Guid.NewGuid().ToString("N")[..16];
        var request = new Request();
        _requests[nonce] = request;
        var clock = Stopwatch.StartNew();
        await Client.RequestGuildUsersAsync(
            userIds is null
                ? new GuildUsersRequestProperties(guildId)
                {
                    Query = "",
                    Limit = 0,
                    Nonce = nonce,
                }
                : new GuildUsersRequestProperties(guildId) { UserIds = userIds, Nonce = nonce }
        );
        var limited = await request.Done.Task.WaitAsync(Timeout);
        var elapsed = clock.Elapsed;
        // The event handler can run before NetCord puts the members in the cache.
        await Task.Delay(100);
        return new RequestResult([.. request.Chunks], limited, elapsed, request.InCacheAtEvent);
    }

    /// <summary>Waits for a <c>GUILD_MEMBER_UPDATE</c> that matches.</summary>
    public async Task<GuildUser> WaitForUpdateAsync(Func<GuildUser, bool> match)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (true)
        {
            var user = await _updates.Reader.ReadAsync(timeout.Token);
            if (match(user))
                return user;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Client.CloseAsync();
        }
        catch (Exception)
        {
            // The connection is already closed.
        }
        Client.Dispose();
    }

    private sealed class Request
    {
        public ConcurrentQueue<GuildUserChunkEventArgs> Chunks { get; } = new();

        /// <summary>True when the cache held the members of the last chunk at the event.</summary>
        public bool InCacheAtEvent { get; set; }

        public TaskCompletionSource<RateLimitedEventArgs?> Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// A <c>GUILD_CREATE</c>: the time after the start, the members in it, and if the cache held
/// the guild of the event when the event handler ran.
/// </summary>
internal sealed record GuildCreated(ulong GuildId, TimeSpan At, int Users, bool InCacheAtEvent);

/// <summary>The chunks of a member request, or the rate limit that Discord sent for it.</summary>
internal sealed record RequestResult(
    IReadOnlyList<GuildUserChunkEventArgs> Chunks,
    RateLimitedEventArgs? RateLimited,
    TimeSpan Elapsed,
    bool InCacheAtEvent
);
