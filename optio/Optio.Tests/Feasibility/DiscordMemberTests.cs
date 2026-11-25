using System.Diagnostics;
using System.Net;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

namespace Optio.Tests.Feasibility;

/// <summary>
/// The feasibility tests T2 of M37 (issue #48): the member data of Optio from Discord, with the
/// privileged intent <c>GUILD_MEMBERS</c>. The tests connect to Discord with the bot token in
/// the environment variable <c>Discord__Token</c>, and examine the user in
/// <c>Discord__TestUserId</c>. Without both, they skip. CI does not run them.
/// The tests send no message and change no guild. They change only the roles of the test user,
/// with a role whose name holds "test", and they remove each role that they add.
/// </summary>
[Category("Discord")]
public sealed class DiscordMemberTests
{
    /// <summary>The Discord user that the tests examine, from the environment variable
    /// <c>Discord__TestUserId</c>.</summary>
    private static ulong TestUser;

    private readonly List<RestGuild> _guilds = [];
    private readonly Dictionary<ulong, GuildUser> _testMember = [];
    private string _token = null!;
    private RestClient _rest = null!;
    private (RestGuild Guild, Role Role)? _testRole;

    private IEnumerable<ulong> GuildIds => _guilds.Select(g => g.Id);

    [OneTimeSetUp]
    public async Task DiscoverAsync()
    {
        var token = Environment.GetEnvironmentVariable("Discord__Token");
        if (string.IsNullOrEmpty(token))
            Assert.Ignore("The environment variable Discord__Token is not set.");
        if (
            !ulong.TryParse(Environment.GetEnvironmentVariable("Discord__TestUserId"), out TestUser)
        )
        {
            Assert.Ignore("The environment variable Discord__TestUserId is not set.");
        }
        _token = token;
        _rest = new RestClient(new BotToken(token));
        var botId = new BotToken(token).Id;

        await foreach (var partial in _rest.GetCurrentUserGuildsAsync())
        {
            var guild = await _rest.GetGuildAsync(partial.Id);
            _guilds.Add(guild);
            var bot = await _rest.GetGuildUserAsync(guild.Id, botId);
            var botPermissions = bot.GetPermissions(guild);
            var canManage =
                botPermissions.HasFlag(Permissions.ManageRoles)
                || botPermissions.HasFlag(Permissions.Administrator);
            var botTop = bot
                .RoleIds.Select(r => guild.Roles[r].RawPosition)
                .DefaultIfEmpty(0)
                .Max();
            var member = await TryGetMemberAsync(guild.Id, TestUser);
            if (member is not null)
                _testMember[guild.Id] = member;

            Discovered(
                $"guild {guild.Id} \"{guild.Name}\": {guild.Roles.Count} roles, test user is a "
                    + $"member: {member is not null}, test user is the owner: "
                    + $"{guild.OwnerId == TestUser}, bot can manage roles: {canManage}, "
                    + $"highest role of the bot: {botTop}"
            );
            if (member is not null)
                Discovered($"  roles of the test user: {string.Join(",", member.RoleIds)}");
            foreach (
                var role in guild.Roles.Values.Where(r =>
                    r.Name.Contains("test", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                var usable =
                    member is not null
                    && canManage
                    && !role.Managed
                    && role.Id != guild.Id
                    && role.RawPosition < botTop
                    && !member.RoleIds.Contains(role.Id);
                Discovered(
                    $"  test role {role.Id} \"{role.Name}\": position {role.RawPosition}, managed "
                        + $"{role.Managed}, "
                        + $"usable: {usable}"
                );
                if (usable && _testRole is null)
                    _testRole = (guild, role);
            }
        }
        Assert.That(_guilds, Is.Not.Empty);
    }

    [OneTimeTearDown]
    public void Stop() => _rest?.Dispose();

    /// <summary>T2.1: GUILD_CREATE with no member request.</summary>
    [Test]
    public async Task Guild_create_gives_the_roles_with_their_permissions_and_the_owner()
    {
        await using var gateway = await DiscordGateway.StartAsync(_token, GuildIds);

        foreach (var guild in _guilds)
        {
            var cached = gateway.Client.Cache.Guilds[guild.Id];
            var created = gateway.GuildCreates.First(c => c.GuildId == guild.Id);
            Log(
                $"T2.1 guild {guild.Id}: GUILD_CREATE after {created.At.TotalMilliseconds:F0} ms, "
                    + $"{created.Users} members in it, {cached.UserCount} in the guild, "
                    + $"{cached.Roles.Count} roles, test user in the cache: "
                    + $"{cached.Users.ContainsKey(TestUser)}, guild in the cache when the event "
                    + $"handler ran: {created.InCacheAtEvent}"
            );
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cached.OwnerId, Is.EqualTo(guild.OwnerId));
                Assert.That(
                    cached.Roles.Values.Select(r => (r.Id, r.Permissions)),
                    Is.EquivalentTo(guild.Roles.Values.Select(r => (r.Id, r.Permissions)))
                );
            }
        }
    }

    /// <summary>T2.1 (a): the REST call for one member.</summary>
    [Test]
    public async Task Rest_member_lookup_is_fast_and_does_not_change_the_cache()
    {
        var guild = MemberGuild();
        await using var gateway = await DiscordGateway.StartAsync(_token, GuildIds);

        var times = new List<double>();
        var limited = new List<int>();
        for (var i = 0; i < 10; i++)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                var member = await _rest.GetGuildUserAsync(
                    guild.Id,
                    TestUser,
                    new RestRequestProperties { RateLimitHandling = RestRateLimitHandling.NoRetry }
                );
                Assert.That(member.RoleIds, Is.EquivalentTo(_testMember[guild.Id].RoleIds));
            }
            catch (RestRateLimitedException e)
            {
                limited.Add(e.ResetAfter);
            }
            times.Add(clock.Elapsed.TotalMilliseconds);
        }

        var inCache = gateway.Client.Cache.Guilds[guild.Id].Users.ContainsKey(TestUser);
        Log(
            $"T2.1 REST: 10 calls in a row, times {string.Join(",", times.Select(t => $"{t:F0}"))}"
                + $" ms, rate limited {limited.Count} times (reset after "
                + $"{string.Join(",", limited)} ms), member in the cache: {inCache}"
        );
        Assert.That(inCache, Is.False);
    }

    /// <summary>T2.1 (b): the gateway request with the user ID.</summary>
    [Test]
    public async Task Gateway_request_with_the_user_id_puts_the_member_in_the_cache()
    {
        var guild = MemberGuild();
        await using var gateway = await DiscordGateway.StartAsync(_token, GuildIds);

        var result = await gateway.RequestUsersAsync(guild.Id, [TestUser]);
        var cached = gateway.Client.Cache.Guilds[guild.Id].Users.GetValueOrDefault(TestUser);
        Log(
            $"T2.1 gateway: answer after {result.Elapsed.TotalMilliseconds:F0} ms, "
                + $"{result.Chunks.Count} chunks, rate limited: {result.RateLimited is not null}, "
                + $"member in the cache: {cached is not null}, in the cache when the event "
                + $"handler ran: {result.InCacheAtEvent}"
        );
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RateLimited, Is.Null);
            Assert.That(result.Chunks.Single().Users.Single().Id, Is.EqualTo(TestUser));
            Assert.That(cached?.RoleIds, Is.EquivalentTo(_testMember[guild.Id].RoleIds));
        }

        // Five more requests at the same time.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => gateway.RequestUsersAsync(guild.Id, [TestUser]))
        );
        var times = string.Join(",", results.Select(r => $"{r.Elapsed.TotalMilliseconds:F0}"));
        var limitedCount = results.Count(r => r.RateLimited is not null);
        Log(
            $"T2.1 gateway: 5 requests at the same time, times {times} ms, rate limited "
                + $"{limitedCount}"
                + string.Concat(
                    results
                        .Where(r => r.RateLimited is not null)
                        .Take(1)
                        .Select(r => $", retry after {r.RateLimited!.RetryAfter} s")
                )
        );
        Assert.That(results.All(r => r.Chunks.Count > 0 || r.RateLimited is not null), Is.True);
    }

    /// <summary>
    /// T2.2 and T2.9: the bot adds a test role to the test user and removes it. The bot did not
    /// request the member before.
    /// </summary>
    [Test]
    public async Task Role_change_of_a_member_that_the_bot_did_not_request_reaches_the_cache()
    {
        if (_testRole is not { } testRole)
        {
            Assert.Ignore(
                "No usable test role: a role with \"test\" in its name, below the highest role of "
                    + "the bot, that the test user does not have, and the permission Manage Roles."
            );
            return;
        }
        var (guild, role) = testRole;
        var original = _testMember[guild.Id].RoleIds;
        await using var gateway = await DiscordGateway.StartAsync(_token, GuildIds);
        Users(gateway, guild.Id).TryGetValue(TestUser, out var before);
        Log($"T2.9 test user in the cache before the change: {before is not null}");

        try
        {
            var clock = Stopwatch.StartNew();
            await _rest.AddGuildUserRoleAsync(guild.Id, TestUser, role.Id);
            var rest = clock.Elapsed;
            var addEvent = await gateway.WaitForUpdateAsync(u =>
                u.Id == TestUser && u.RoleIds.Contains(role.Id)
            );
            var update = clock.Elapsed;
            // The event handler can run before NetCord changes the cache.
            await Task.Delay(100);
            var added = Users(gateway, guild.Id).GetValueOrDefault(TestUser);
            Log(
                $"T2.2 add role {role.Id} in guild {guild.Id}: REST answer after "
                    + $"{rest.TotalMilliseconds:F0} ms, GUILD_MEMBER_UPDATE after "
                    + $"{update.TotalMilliseconds:F0} ms. T2.9: member in the cache after the "
                    + $"event: {added is not null}, with the role: "
                    + $"{added?.RoleIds.Contains(role.Id)}"
            );

            clock.Restart();
            await _rest.RemoveGuildUserRoleAsync(guild.Id, TestUser, role.Id);
            rest = clock.Elapsed;
            var removeEvent = await gateway.WaitForUpdateAsync(u =>
                u.Id == TestUser && !u.RoleIds.Contains(role.Id)
            );
            update = clock.Elapsed;
            await Task.Delay(100);
            var removed = Users(gateway, guild.Id).GetValueOrDefault(TestUser);
            Log(
                $"T2.2 remove role {role.Id}: REST answer after {rest.TotalMilliseconds:F0} ms, "
                    + $"GUILD_MEMBER_UPDATE after {update.TotalMilliseconds:F0} ms, the cache "
                    + $"has the role: {removed?.RoleIds.Contains(role.Id)}"
            );

            using (Assert.EnterMultipleScope())
            {
                Assert.That(addEvent.RoleIds, Does.Contain(role.Id));
                Assert.That(removeEvent.RoleIds, Does.Not.Contain(role.Id));
            }
        }
        finally
        {
            var member = await _rest.GetGuildUserAsync(guild.Id, TestUser);
            if (member.RoleIds.Contains(role.Id))
                await _rest.RemoveGuildUserRoleAsync(guild.Id, TestUser, role.Id);
            member = await _rest.GetGuildUserAsync(guild.Id, TestUser);
            Log($"T2.2 roles of the test user after the test: {string.Join(",", member.RoleIds)}");
            Assert.That(member.RoleIds, Is.EquivalentTo(original));
        }
    }

    /// <summary>
    /// T2.9: Optio can remove a member from the concurrent cache of NetCord. The default cache
    /// is immutable: <c>RemoveGuildUser</c> gives a new cache, and the client keeps its own.
    /// </summary>
    [Test]
    public async Task Only_the_concurrent_cache_lets_optio_remove_a_member()
    {
        var guild = MemberGuild();
        var removed = new Dictionary<string, bool>();
        foreach (
            var (name, cache) in new (string, IGatewayClientCacheProvider?)[]
            {
                ("immutable", null),
                ("concurrent", ConcurrentGatewayClientCacheProvider.Empty),
            }
        )
        {
            await using var gateway = await DiscordGateway.StartAsync(_token, GuildIds, cache);
            await gateway.RequestUsersAsync(guild.Id, [TestUser]);
            var added = Users(gateway, guild.Id).ContainsKey(TestUser);
            var copy = gateway.Client.Cache.RemoveGuildUser(guild.Id, TestUser);
            removed[name] = !Users(gateway, guild.Id).ContainsKey(TestUser);
            Log(
                $"T2.9 {name} cache: member added {added}, RemoveGuildUser removes it from the "
                    + $"cache of the client {removed[name]}, from the result "
                    + $"{!copy.Guilds[guild.Id].Users.ContainsKey(TestUser)}"
            );
            Assert.That(added, Is.True);
        }

        Assert.That(
            removed,
            Is.EqualTo(
                new Dictionary<string, bool> { ["immutable"] = false, ["concurrent"] = true }
            )
        );
    }

    /// <summary>
    /// T2.3 and T2.8: the owner, ADMINISTRATOR, and the permission helper of NetCord, for each
    /// member of each guild. The rule: the owner and a member with ADMINISTRATOR have access.
    /// </summary>
    [Test]
    public async Task Permission_helper_agrees_with_the_access_rule_for_each_member()
    {
        await using var gateway = await DiscordGateway.StartAsync(_token, GuildIds);

        foreach (var restGuild in _guilds)
        {
            var guild = gateway.Client.Cache.Guilds[restGuild.Id];
            var members = 0;
            var administrators = 0;
            var disagree = 0;
            await foreach (var member in _rest.GetGuildUsersAsync(guild.Id))
            {
                members++;
                var rule =
                    member.Id == guild.OwnerId
                    || RolePermissions(guild, member.RoleIds).HasFlag(Permissions.Administrator);
                var helper = member.GetPermissions(guild).HasFlag(Permissions.Administrator);
                administrators += rule ? 1 : 0;
                disagree += rule == helper ? 0 : 1;
                if (member.Id == TestUser)
                    Log(
                        $"T2.3 guild {guild.Id}: test user: access by the rule {rule}, the "
                            + $"helper gives ADMINISTRATOR {helper}"
                    );
                var roles = RolePermissions(guild, member.RoleIds);
                if (member.Id == guild.OwnerId)
                    Log(
                        $"T2.3 guild {guild.Id}: owner: access by the rule {rule}, the helper "
                            + $"gives ADMINISTRATOR {helper}, the roles give ADMINISTRATOR "
                            + $"{roles.HasFlag(Permissions.Administrator)}"
                    );
            }
            Log(
                $"T2.3 guild {guild.Id}: owner from GUILD_CREATE equals REST: "
                    + $"{guild.OwnerId == restGuild.OwnerId}. {members} members, {administrators} "
                    + $"with access by the rule, {disagree} where the helper disagrees"
            );
            using (Assert.EnterMultipleScope())
            {
                Assert.That(guild.OwnerId, Is.EqualTo(restGuild.OwnerId));
                Assert.That(disagree, Is.Zero);
            }

            // The owner with no role that gives ADMINISTRATOR: a copy of the guild with only the
            // roles that do not give it.
            var copy = await _rest.GetGuildAsync(guild.Id);
            copy.Roles = copy
                .Roles.Values.Where(r => !r.Permissions.HasFlag(Permissions.Administrator))
                .ToDictionary(r => r.Id);
            var owner = await _rest.GetGuildUserAsync(guild.Id, guild.OwnerId);
            var ownerHelper = owner.GetPermissions(copy);
            Log(
                $"T2.8 guild {guild.Id}: owner with no ADMINISTRATOR role: the helper gives "
                    + $"ADMINISTRATOR {ownerHelper.HasFlag(Permissions.Administrator)}, "
                    + $"permissions {(ulong)ownerHelper:X}, roles "
                    + $"{(ulong)RolePermissions(copy, owner.RoleIds):X}"
            );
            Assert.That(ownerHelper.HasFlag(Permissions.Administrator), Is.True);
        }
    }

    /// <summary>
    /// T2.4 and T2.7: a full member request, its chunks, its memory, and a second full request
    /// within 30 seconds.
    /// </summary>
    [Test]
    public async Task Full_member_request_and_a_second_request_within_30_seconds()
    {
        await using var gateway = await DiscordGateway.StartAsync(_token, GuildIds);

        foreach (var guild in _guilds)
        {
            var memory = GC.GetTotalMemory(forceFullCollection: true);
            var result = await gateway.RequestUsersAsync(guild.Id, null);
            var bytes = GC.GetTotalMemory(forceFullCollection: true) - memory;
            var cached = gateway.Client.Cache.Guilds[guild.Id];
            Log(
                $"T2.4 guild {guild.Id}: full list after {result.Elapsed.TotalMilliseconds:F0} ms"
                    + $" ({gateway.Clock.Elapsed.TotalMilliseconds:F0} ms after the connect), "
                    + "chunk indexes "
                    + string.Join(",", result.Chunks.Select(c => $"{c.ChunkIndex}/{c.ChunkCount}"))
                    + ", "
                    + $"{cached.Users.Count} cached of {cached.UserCount}. T2.7: {bytes} bytes, "
                    + $"{bytes / Math.Max(1, cached.Users.Count)} bytes for each member"
            );
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.RateLimited, Is.Null);
                Assert.That(cached.Users, Has.Count.EqualTo(cached.UserCount));
            }
        }

        var first = _guilds[0];
        var second = await gateway.RequestUsersAsync(first.Id, null);
        Log(
            $"T2.4 second full request for guild {first.Id}: "
                + (
                    second.RateLimited is { } limited
                        ? $"RATE_LIMITED, opcode {limited.Opcode}, retry after "
                            + $"{limited.RetryAfter} s"
                        : $"{second.Chunks.Count} chunks"
                )
        );
        Assert.That(second.RateLimited?.Opcode, Is.EqualTo(GatewayOpcode.RequestGuildUsers));
    }

    /// <summary>
    /// T2.5 and T2.6: a resume of the session, as after a network failure, and a new session.
    /// </summary>
    [Test]
    public async Task Resume_keeps_the_cached_members_and_a_new_session_does_not()
    {
        var guild = MemberGuild();
        await using var gateway = await DiscordGateway.StartAsync(_token, GuildIds);
        await gateway.RequestUsersAsync(guild.Id, [TestUser]);
        var roles = gateway.Client.Cache.Guilds[guild.Id].Roles.Count;
        Assert.That(Users(gateway, guild.Id).ContainsKey(TestUser), Is.True);

        // A close with a code other than 1000 or 1001 keeps the session. Then a resume.
        var session = gateway.Client.SessionId!;
        var sequence = gateway.Client.SequenceNumber;
        var creates = gateway.GuildCreates.Count;
        await gateway.Client.CloseAsync((System.Net.WebSockets.WebSocketCloseStatus)4000);
        var clock = Stopwatch.StartNew();
        await gateway.Client.ResumeAsync(session, sequence);
        while (gateway.Resumes == 0 && clock.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(50);
        var afterResume = Users(gateway, guild.Id).ContainsKey(TestUser);
        Log(
            $"T2.5 resume: {gateway.Resumes} RESUMED after {clock.Elapsed.TotalMilliseconds:F0} "
                + $"ms, GUILD_CREATE {gateway.GuildCreates.Count - creates}, member in the cache: "
                + $"{afterResume}"
        );

        // A new session: close with 1000, then start again. Discord sends GUILD_CREATE again.
        await gateway.Client.CloseAsync();
        await gateway.StartAgainAsync(GuildIds);
        var created = gateway.GuildCreates.Last(c => c.GuildId == guild.Id);
        var afterNew = Users(gateway, guild.Id).ContainsKey(TestUser);
        var rolesAfterNew = gateway.Client.Cache.Guilds[guild.Id].Roles.Count;
        Log(
            $"T2.5 new session: {gateway.Readies} READY in total, GUILD_CREATE with "
                + $"{created.Users} members, member in the cache: {afterNew}, roles {roles} "
                + $"before, {rolesAfterNew} after"
        );

        await gateway.RequestUsersAsync(guild.Id, [TestUser]);
        var afterRequest = Users(gateway, guild.Id).GetValueOrDefault(TestUser);
        Log($"T2.6 after a new request: member in the cache: {afterRequest is not null}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(gateway.Resumes, Is.EqualTo(1));
            Assert.That(afterResume, Is.True);
            Assert.That(afterNew, Is.False);
            Assert.That(rolesAfterNew, Is.EqualTo(roles));
            Assert.That(afterRequest?.RoleIds, Is.EquivalentTo(_testMember[guild.Id].RoleIds));
        }
    }

    /// <summary>The first guild that has the test user as a member.</summary>
    private RestGuild MemberGuild()
    {
        var guild = _guilds.FirstOrDefault(g => _testMember.ContainsKey(g.Id));
        if (guild is null)
            Assert.Ignore("The test user is not a member of a guild of the bot.");
        return guild;
    }

    private static IReadOnlyDictionary<ulong, GuildUser> Users(DiscordGateway gateway, ulong id) =>
        gateway.Client.Cache.Guilds[id].Users;

    /// <summary>The permissions of <c>@everyone</c> and of each role of the member.</summary>
    private static Permissions RolePermissions(RestGuild guild, IEnumerable<ulong> roleIds) =>
        roleIds
            .Select(id => guild.Roles.TryGetValue(id, out var role) ? role.Permissions : default)
            .Aggregate(guild.Roles[guild.Id].Permissions, (all, p) => all | p);

    private async Task<GuildUser?> TryGetMemberAsync(ulong guildId, ulong userId)
    {
        try
        {
            return await _rest.GetGuildUserAsync(guildId, userId);
        }
        catch (RestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static void Log(string text) => TestContext.Out.WriteLine(text);

    /// <summary>The output of the one-time setup goes to the progress output.</summary>
    private static void Discovered(string text) => TestContext.Progress.WriteLine($"T2 {text}");
}
