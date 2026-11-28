using Microsoft.Extensions.DependencyInjection;
using Optio.Queries;
using Optio.Storage;
using Optio.Tests.Infrastructure;
using static Optio.Tests.Infrastructure.FakeMembers;

namespace Optio.Tests;

/// <summary>The access and the token limits of a query, with the usage rows in PostgreSQL. Needs
/// Docker.</summary>
public sealed class QueryLimitTests
{
    private const ulong Asker = 400000000000000003;

    [Test]
    public async Task Member_without_a_query_role_is_refused()
    {
        using var factory = Factory();
        factory.Members.Members[(Guild, Asker)] = [OtherRole];

        var outcome = await CheckAsync(factory, Asker);

        AssertRefused(outcome, 403, "You have no role that may ask Optio in this guild.");
    }

    [Test]
    public async Task Only_an_administrator_asks_when_the_guild_has_no_query_role()
    {
        using var factory = Factory();
        await SettingsAsync(factory, s => s with { Access = new() });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CheckAsync(factory, Owner), Is.Null);
            AssertRefused(await CheckAsync(factory, Reader), 403);
        }
    }

    [Test]
    public async Task Limit_of_a_user_gives_no_access()
    {
        using var factory = Factory();
        factory.Members.Members[(Guild, Asker)] = [OtherRole];
        await SettingsAsync(factory, s =>
            s with
            {
                Limits = new() { Users = new Dictionary<ulong, long> { [Asker] = 1000 } },
            }
        );

        AssertRefused(await CheckAsync(factory, Asker), 403);
    }

    [Test]
    public async Task Discord_state_of_the_user_decides_first()
    {
        using var factory = Factory();

        var notMember = await CheckAsync(factory, Asker);
        factory.Members.BotGuilds.Clear();
        var noBot = await CheckAsync(factory, Reader);
        factory.Members.Unavailable = true;
        var unavailable = await CheckAsync(factory, Reader);

        using (Assert.EnterMultipleScope())
        {
            AssertRefused(notMember, 403, "You are not a member of this guild.");
            AssertRefused(noBot, 403, "Optio is not in this guild.");
            AssertRefused(unavailable, 503);
        }
    }

    [Test]
    public async Task Role_limit_refuses_until_the_use_falls_below_it()
    {
        using var factory = Factory();
        await SettingsAsync(factory, s => s with { Limits = RoleLimits((ReadRole, 100)) });
        var now = factory.Time.GetUtcNow();
        await RowsAsync(
            factory,
            Row(now.AddMinutes(-50), Reader, Guild, 60),
            Row(now.AddMinutes(-20), Reader, Guild, 60)
        );

        var outcome = await CheckAsync(factory, Reader);

        // Without the oldest row the use is 60, below 100. That row leaves in 10 minutes.
        AssertRefused(outcome, 429, "Your hourly quota is used. Try again in 10 min.");
        Assert.That(
            outcome!.RetryAfter,
            Is.EqualTo(TimeSpan.FromMinutes(10)).Within(TimeSpan.FromSeconds(1))
        );
    }

    [Test]
    public async Task Limit_of_a_user_replaces_the_largest_role_limit()
    {
        using var factory = Factory();
        factory.Members.Members[(Guild, Reader)] = [ReadRole, OtherRole];
        await SettingsAsync(factory, s =>
            s with
            {
                Limits = RoleLimits((ReadRole, 100), (OtherRole, 1000)),
            }
        );
        await RowsAsync(factory, Row(factory.Time.GetUtcNow(), Reader, Guild, 500));

        var withRoles = await CheckAsync(factory, Reader);
        await SettingsAsync(factory, s =>
            s with
            {
                Limits = s.Limits with { Users = new Dictionary<ulong, long> { [Reader] = 200 } },
            }
        );
        var withOwnLimit = await CheckAsync(factory, Reader);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withRoles, Is.Null);
            AssertRefused(withOwnLimit, 429);
        }
    }

    [Test]
    public async Task Administrator_passes_the_guild_limits_but_not_the_pool()
    {
        using var factory = Factory(new() { ["Optio:GuildTokensPerHour"] = "400" });
        await SettingsAsync(factory, s =>
            s with
            {
                Limits = new() { Users = new Dictionary<ulong, long> { [Owner] = 100 } },
            }
        );
        await RowsAsync(factory, Row(factory.Time.GetUtcNow(), Owner, Guild, 300));

        var belowPool = await CheckAsync(factory, Owner);
        await RowsAsync(factory, Row(factory.Time.GetUtcNow(), Reader, Guild, 100));
        var atPool = await CheckAsync(factory, Owner);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(belowPool, Is.Null);
            AssertRefused(atPool, 429, "The guild quota is used. Try again in 60 min.");
        }
    }

    [Test]
    public async Task Cap_of_a_user_counts_the_use_in_all_guilds()
    {
        using var factory = Factory(new() { ["Optio:UserTokensPerHour"] = "200" });
        await RowsAsync(factory, Row(factory.Time.GetUtcNow(), Reader, OtherGuild, 300));

        AssertRefused(
            await CheckAsync(factory, Reader),
            429,
            "Your hourly quota is used. Try again in 60 min."
        );
    }

    [Test]
    public async Task Operator_limit_replaces_the_default_of_the_configuration()
    {
        using var factory = Factory(new() { ["Optio:GuildTokensPerHour"] = "100" });
        await RowsAsync(factory, Row(factory.Time.GetUtcNow(), Reader, Guild, 150));

        var byDefault = await CheckAsync(factory, Reader);
        await OperatorLimitAsync(factory, 1000);
        var byOperator = await CheckAsync(factory, Reader);
        await OperatorLimitAsync(factory, null);
        var byDefaultAgain = await CheckAsync(factory, Reader);

        using (Assert.EnterMultipleScope())
        {
            AssertRefused(byDefault, 429);
            Assert.That(byOperator, Is.Null);
            AssertRefused(byDefaultAgain, 429);
        }
    }

    [Test]
    public async Task Cache_reads_and_rows_older_than_one_hour_do_not_count()
    {
        using var factory = Factory();
        await SettingsAsync(factory, s => s with { Limits = RoleLimits((ReadRole, 100)) });
        var now = factory.Time.GetUtcNow();
        await RowsAsync(
            factory,
            Row(now, Reader, Guild, 10, cacheRead: 1000),
            Row(now.AddMinutes(-61), Reader, Guild, 1000)
        );
        var withoutWrites = await CheckAsync(factory, Reader);
        await RowsAsync(factory, Row(now, Reader, Guild, 10, output: 40, cacheWrite: 40));
        var withWrites = await CheckAsync(factory, Reader);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withoutWrites, Is.Null);
            AssertRefused(withWrites, 429);
        }
    }

    private static OptioFactory Factory(Dictionary<string, string>? settings = null) =>
        new(stream: true, query: true, settings: settings);

    private static Task<QueryOutcome?> CheckAsync(OptioFactory factory, ulong user) =>
        factory
            .Services.GetRequiredService<QueryLimits>()
            .CheckAsync(new QueryClient(QueryClient.KeyOf("session-1"), Guild, user), default);

    private static void AssertRefused(QueryOutcome? outcome, int status, string? problem = null)
    {
        Assert.That(outcome?.Status, Is.EqualTo(status));
        if (problem is not null)
            Assert.That(outcome?.Problem, Is.EqualTo(problem));
    }

    private static LimitSettings RoleLimits(params (ulong Role, long Tokens)[] limits) =>
        new() { Roles = limits.ToDictionary(l => l.Role, l => l.Tokens) };

    private static async Task SettingsAsync(
        OptioFactory factory,
        Func<GuildSettings, GuildSettings> change
    )
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var result = await scope
            .ServiceProvider.GetRequiredService<GuildSettingsStore>()
            .UpdateAsync(Guild, change, Owner, default);
        Assert.That(result.Settings, Is.Not.Null, result.Refusal);
    }

    private static async Task OperatorLimitAsync(OptioFactory factory, long? tokens)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<OperatorLimitStore>()
            .SetAsync(OperatorScope.Guild, Guild, tokens, Owner, default);
    }

    private static async Task RowsAsync(OptioFactory factory, params QueryUsageRow[] rows)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptioDbContext>();
        db.QueryUsage.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static QueryUsageRow Row(
        DateTimeOffset at,
        ulong user,
        ulong guild,
        long input,
        long output = 0,
        long cacheRead = 0,
        long cacheWrite = 0
    ) =>
        new()
        {
            At = at,
            GuildId = (long)guild,
            UserId = (long)user,
            ClientKey = "0123456789abcdef0123456789abcdef",
            QueryReference = "query-0001",
            Model = "model-1",
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
        };
}
