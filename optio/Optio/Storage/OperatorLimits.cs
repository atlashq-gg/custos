using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Optio.Storage;

/// <summary>The target of an operator limit.</summary>
public enum OperatorScope
{
    /// <summary>The pool of a guild: the tokens of all its members.</summary>
    Guild,

    /// <summary>The cap of a user: the tokens of the user in all guilds.</summary>
    User,
}

/// <summary>A limit that an operator set with <c>/optioctl</c>. It replaces the default of the
/// configuration.</summary>
public sealed class OperatorLimitRow
{
    public required OperatorScope Scope { get; init; }

    /// <summary>The Discord ID of the guild or the user.</summary>
    public required long TargetId { get; init; }

    public required long TokensPerHour { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The Discord user ID of the last change.</summary>
    public required long UpdatedBy { get; init; }
}

/// <summary>The limits of the operator for one query: null is no limit.</summary>
internal sealed record OperatorLimits(long? GuildPool, long? UserCap);

/// <summary>Reads and writes the operator limits.</summary>
public sealed partial class OperatorLimitStore(
    OptioDbContext db,
    IOptions<OptioOptions> options,
    TimeProvider time,
    ILogger<OperatorLimitStore> logger
)
{
    /// <summary>The pool of <paramref name="guild"/> and the cap of <paramref name="user"/>,
    /// from their rows or from the defaults of the configuration.</summary>
    internal async Task<OperatorLimits> GetAsync(
        ulong guild,
        ulong user,
        CancellationToken cancellationToken
    )
    {
        var rows = await db
            .OperatorLimits.AsNoTracking()
            .Where(l =>
                (l.Scope == OperatorScope.Guild && l.TargetId == (long)guild)
                || (l.Scope == OperatorScope.User && l.TargetId == (long)user)
            )
            .ToListAsync(cancellationToken);
        long? Tokens(OperatorScope scope) =>
            rows.SingleOrDefault(l => l.Scope == scope)?.TokensPerHour;
        return new OperatorLimits(
            Tokens(OperatorScope.Guild) ?? options.Value.GuildTokensPerHour,
            Tokens(OperatorScope.User) ?? options.Value.UserTokensPerHour
        );
    }

    /// <summary>Sets the limit of a guild or a user. Null removes it, so that the default of the
    /// configuration applies again.</summary>
    public async Task SetAsync(
        OperatorScope scope,
        ulong targetId,
        long? tokensPerHour,
        ulong userId,
        CancellationToken cancellationToken
    )
    {
        if (tokensPerHour is { } tokens)
        {
            // One statement, so that two changes at the same time do not fail.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO operator_limits
                    (scope, target_id, tokens_per_hour, updated_at, updated_by)
                VALUES ({scope.ToString()}, {(long)targetId}, {tokens}, {time.GetUtcNow()},
                    {(long)userId})
                ON CONFLICT (scope, target_id) DO UPDATE SET
                    tokens_per_hour = EXCLUDED.tokens_per_hour,
                    updated_at = EXCLUDED.updated_at,
                    updated_by = EXCLUDED.updated_by
                """,
                cancellationToken
            );
        }
        else
        {
            await db
                .OperatorLimits.Where(l => l.Scope == scope && l.TargetId == (long)targetId)
                .ExecuteDeleteAsync(cancellationToken);
        }
        LogLimitChanged(logger, scope, targetId, userId);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Audit: the operator limit of {Scope} {TargetId} changed by user {UserId}"
    )]
    private static partial void LogLimitChanged(
        ILogger logger,
        OperatorScope scope,
        ulong targetId,
        ulong userId
    );
}
