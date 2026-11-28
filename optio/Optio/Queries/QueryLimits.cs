using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Optio.Discord;
using Optio.Storage;

namespace Optio.Queries;

/// <summary>
/// The access and the token limits of a query: the query roles of the guild, the limit of the
/// member, the cap of the user in all guilds, and the pool of the guild, each in a sliding hour.
/// The use comes from the usage rows: input, output, and cache write tokens. Cache reads are
/// free. Optio knows the tokens of a query only after the model answers, so it admits a query
/// while the use is below each limit, and one query can go over a limit.
/// </summary>
internal sealed partial class QueryLimits(
    IMemberDirectory members,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<QueryLimits> logger
)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private const string UserText = "Your hourly quota is used. Try again in {0} min.";
    private const string GuildText = "The guild quota is used. Try again in {0} min.";

    /// <summary>Null when the user may ask now, else the refusal.</summary>
    public async Task<QueryOutcome?> CheckAsync(
        QueryClient client,
        CancellationToken cancellationToken
    )
    {
        var member = await members.AccessAsync(client.Guild, client.User, cancellationToken);
        switch (member.State)
        {
            case MemberState.NotMember:
                return Refuse(
                    StatusCodes.Status403Forbidden,
                    "You are not a member of this guild."
                );
            case MemberState.NoBot:
                return Refuse(StatusCodes.Status403Forbidden, "Optio is not in this guild.");
            case MemberState.Unavailable:
                return Refuse(
                    StatusCodes.Status503ServiceUnavailable,
                    "Discord did not answer. Ask again later."
                );
        }

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        try
        {
            var settings = await services
                .GetRequiredService<GuildSettingsStore>()
                .GetAsync(client.Guild, cancellationToken);
            if (
                !member.IsAdministrator
                && !member.RoleIds.Any(settings.Access.QueryRoleIds.Contains)
            )
            {
                return Refuse(
                    StatusCodes.Status403Forbidden,
                    "You have no role that may ask Optio in this guild."
                );
            }

            var operatorLimits = await services
                .GetRequiredService<OperatorLimitStore>()
                .GetAsync(client.Guild, client.User, cancellationToken);
            var since = time.GetUtcNow() - Window;
            var uses = services
                .GetRequiredService<OptioDbContext>()
                .QueryUsage.AsNoTracking()
                .Where(u => u.At > since);
            (long? Limit, IQueryable<QueryUsageRow> Rows, string Text)[] limits =
            [
                (
                    MemberLimit(settings, member, client.User),
                    uses.Where(u =>
                        u.GuildId == (long)client.Guild && u.UserId == (long)client.User
                    ),
                    UserText
                ),
                (operatorLimits.UserCap, uses.Where(u => u.UserId == (long)client.User), UserText),
                (
                    operatorLimits.GuildPool,
                    uses.Where(u => u.GuildId == (long)client.Guild),
                    GuildText
                ),
            ];

            (TimeSpan Wait, string Text)? longest = null;
            foreach (var (limit, rows, text) in limits)
            {
                if (
                    limit is { } tokens
                    && await WaitAsync(rows, tokens, cancellationToken) is { } wait
                    && (longest is null || wait > longest.Value.Wait)
                )
                {
                    longest = (wait, text);
                }
            }
            return longest is { } refusal
                ? new QueryOutcome(
                    StatusCodes.Status429TooManyRequests,
                    Problem: string.Format(
                        CultureInfo.InvariantCulture,
                        refusal.Text,
                        Math.Max(1, (int)Math.Ceiling(refusal.Wait.TotalMinutes))
                    ),
                    RetryAfter: refusal.Wait
                )
                : null;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            LogNoLimits(logger, e, client.Guild);
            return Refuse(
                StatusCodes.Status503ServiceUnavailable,
                "The settings of your guild are not available. Ask again later."
            );
        }
    }

    /// <summary>The limit of a member in the guild: none for an administrator, else the override
    /// of the user, else the largest limit of the roles of the member, else none.</summary>
    private static long? MemberLimit(GuildSettings settings, MemberAccess member, ulong user)
    {
        if (member.IsAdministrator)
            return null;
        if (settings.Limits.Users.TryGetValue(user, out var own))
            return own;
        return member
            .RoleIds.Where(settings.Limits.Roles.ContainsKey)
            .Select(role => (long?)settings.Limits.Roles[role])
            .Max();
    }

    /// <summary>The wait until the use of <paramref name="rows"/> is below
    /// <paramref name="limit"/>, or null when it is below now. The use falls when the oldest rows
    /// leave the window.</summary>
    private async Task<TimeSpan?> WaitAsync(
        IQueryable<QueryUsageRow> rows,
        long limit,
        CancellationToken cancellationToken
    )
    {
        var uses = rows.Select(u => new Use
        {
            At = u.At,
            Tokens = u.InputTokens + u.OutputTokens + u.CacheWriteTokens,
        });
        var used = await uses.SumAsync(u => u.Tokens, cancellationToken);
        if (used < limit)
            return null;

        var now = time.GetUtcNow();
        await foreach (
            var use in uses.OrderBy(u => u.At)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
        )
        {
            used -= use.Tokens;
            if (used < limit)
                return use.At + Window - now;
        }
        // Rows left the window after the sum.
        return null;
    }

    private static QueryOutcome Refuse(int status, string problem) => new(status, Problem: problem);

    private sealed class Use
    {
        public DateTimeOffset At { get; init; }
        public long Tokens { get; init; }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "The limits of a query in guild {GuildId} are not available"
    )]
    private static partial void LogNoLimits(ILogger logger, Exception e, ulong guildId);
}
