using NetCord;
using NetCord.Gateway;
using Optio.Streams;

namespace Optio.Discord;

/// <summary>
/// The members of the guilds, from the Discord gateway of optio. The guilds, their owners,
/// and the permissions of their roles come from the cache of NetCord, which <c>GUILD_CREATE</c>
/// fills. A separate dictionary holds only the users with an open stream: optio requests a user
/// when a stream opens, and the member events keep the roles current. optio ignores the other
/// members that NetCord caches.
/// </summary>
internal sealed partial class DiscordMembers : IMemberDirectory
{
    /// <summary>How long a stream waits for Discord when it opens.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private readonly Dictionary<(ulong Guild, ulong User), Follow> _follows = [];
    private readonly TaskCompletionSource _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly GatewayClient _client;
    private readonly ILogger<DiscordMembers> _logger;
    private HashSet<ulong>? _guildIds;

    public DiscordMembers(DiscordGateway gateway, ILogger<DiscordMembers> logger)
    {
        _logger = logger;
        _client = gateway.Client;
        _client.Ready += OnReadyAsync;
        _client.GuildCreate += args => WhenCachedAsync(args.GuildId);
        _client.GuildUserChunk += OnChunk;
        _client.GuildUserAdd += OnUser;
        _client.GuildUserUpdate += OnUser;
        _client.GuildUserRemove += args =>
        {
            Change(args.GuildId, args.User.Id, follow => follow.RoleIds = null);
            return default;
        };
    }

    public event Action<ulong>? GuildReady;

    public async Task<MemberState> ConnectAsync(
        ulong guild,
        ulong user,
        CancellationToken cancellationToken
    )
    {
        Task<bool> member;
        var send = false;
        lock (_lock)
        {
            if (!_follows.TryGetValue((guild, user), out var follow))
                _follows[(guild, user)] = follow = new Follow();
            follow.Streams++;
            if (follow.RoleIds is not null)
            {
                member = Task.FromResult(true);
            }
            else
            {
                send = follow.Request is null;
                follow.Request ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                member = follow.Request.Task;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await _ready.Task.WaitAsync(timeout.Token);
            if (!HasGuild(guild))
                return MemberState.NoBot;
            if (send)
                await RequestAsync(guild, [user], timeout.Token);
            if (!await member.WaitAsync(timeout.Token))
                return MemberState.NotMember;
            while (!_client.Cache.Guilds.ContainsKey(guild))
                await Task.Delay(50, timeout.Token);
            return MemberState.Member;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            if (e is not OperationCanceledException)
                LogRequestFailed(_logger, e, guild);
            // The next stream sends the request again.
            lock (_lock)
            {
                if (_follows.TryGetValue((guild, user), out var follow))
                    follow.Request = null;
            }
            return MemberState.Unavailable;
        }
    }

    public void Disconnect(ulong guild, ulong user)
    {
        lock (_lock)
        {
            if (_follows.TryGetValue((guild, user), out var follow) && --follow.Streams <= 0)
                _follows.Remove((guild, user));
        }
    }

    public bool HasGuild(ulong guild)
    {
        lock (_lock)
            return _guildIds?.Contains(guild) ?? true;
    }

    public bool? CanRead(EventPolicy policy, ulong guild, ulong user)
    {
        IReadOnlyCollection<ulong>? roles;
        lock (_lock)
            roles = _follows.GetValueOrDefault((guild, user))?.RoleIds;
        if (!_client.Cache.Guilds.TryGetValue(guild, out var cached))
            return null;
        if (roles is null)
            return false;

        var facts = new GuildFacts(
            cached.Id,
            cached.OwnerId,
            cached.Roles.ToDictionary(r => r.Key, r => r.Value.Permissions)
        );
        return AccessPolicy.CanRead(policy, facts, new MemberFacts(user, roles));
    }

    /// <summary>A new session replaces the cache of NetCord, so optio requests the followed users
    /// again.</summary>
    private async ValueTask OnReadyAsync(ReadyEventArgs args)
    {
        List<IGrouping<ulong, ulong>> follows;
        lock (_lock)
        {
            _guildIds = [.. args.GuildIds];
            follows = [.. _follows.Keys.GroupBy(k => k.Guild, k => k.User)];
        }
        _ready.TrySetResult();
        foreach (var guild in follows)
        {
            try
            {
                await RequestAsync(guild.Key, [.. guild], CancellationToken.None);
            }
            catch (Exception e)
            {
                LogRequestFailed(_logger, e, guild.Key);
            }
        }
    }

    /// <summary>NetCord runs the handler of <c>GUILD_CREATE</c> before it puts the guild in its
    /// cache. Thus the hub gets the guild only when the cache holds it.</summary>
    private async ValueTask WhenCachedAsync(ulong guild)
    {
        lock (_lock)
            _guildIds?.Add(guild);
        for (var i = 0; i < 200 && !_client.Cache.Guilds.ContainsKey(guild); i++)
            await Task.Delay(10);
        GuildReady?.Invoke(guild);
    }

    private ValueTask OnChunk(GuildUserChunkEventArgs args)
    {
        foreach (var user in args.Users)
            Change(args.GuildId, user.Id, follow => follow.RoleIds = [.. user.RoleIds]);
        foreach (var user in args.NotFound ?? [])
            Change(args.GuildId, user, follow => follow.Request?.TrySetResult(false));
        return default;
    }

    private ValueTask OnUser(GuildUser user)
    {
        Change(user.GuildId, user.Id, follow => follow.RoleIds = [.. user.RoleIds]);
        return default;
    }

    /// <summary>Changes a followed user. A user with roles completes its request.</summary>
    private void Change(ulong guild, ulong user, Action<Follow> change)
    {
        lock (_lock)
        {
            if (!_follows.TryGetValue((guild, user), out var follow))
                return;
            change(follow);
            if (follow.RoleIds is not null)
                follow.Request?.TrySetResult(true);
            if (follow.Request?.Task.IsCompleted == true)
                follow.Request = null;
        }
    }

    // Discord takes at most 100 user IDs in one request.
    private async Task RequestAsync(
        ulong guild,
        IReadOnlyList<ulong> users,
        CancellationToken cancellationToken
    )
    {
        foreach (var chunk in users.Chunk(100))
        {
            await _client.RequestGuildUsersAsync(
                new GuildUsersRequestProperties(guild) { UserIds = chunk },
                cancellationToken: cancellationToken
            );
        }
    }

    /// <summary>A followed user: the open streams, and the role IDs. No role IDs: not known yet,
    /// or not a member.</summary>
    private sealed class Follow
    {
        public int Streams { get; set; }

        public IReadOnlyCollection<ulong>? RoleIds { get; set; }

        public TaskCompletionSource<bool>? Request { get; set; }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The request for the members of guild {GuildId} failed"
    )]
    private static partial void LogRequestFailed(
        ILogger logger,
        Exception exception,
        ulong guildId
    );
}
