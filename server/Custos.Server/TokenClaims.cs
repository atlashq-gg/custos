using System.Globalization;
using System.Security.Claims;

namespace Custos.Server;

/// <summary>
/// The Discord IDs in the access token of Custos: the guild in the claim <c>active_guild</c>, and
/// the user in the claim that <see cref="CustosOptions.DiscordIdClaim"/> names. A custom
/// authenticator of the authorization server sets <c>active_guild</c>.
/// </summary>
internal static class TokenClaims
{
    public const string ActiveGuild = "active_guild";

    /// <summary>
    /// The guild ID from the token. Without the claim, <see
    /// cref="CustosOptions.DevelopmentGuildId"/> applies, but only in the environment Development.
    /// <c>null</c> means that the request has no valid guild.
    /// </summary>
    public static long? GuildId(
        ClaimsPrincipal user,
        CustosOptions options,
        IHostEnvironment environment
    ) => Resolve(user, ActiveGuild, options.DevelopmentGuildId, environment);

    /// <summary>
    /// The Discord user ID from the token. Without the claim, <see
    /// cref="CustosOptions.DevelopmentDiscordUserId"/> applies, but only in the environment
    /// Development. <c>null</c> means that the request has no valid user ID.
    /// </summary>
    public static long? DiscordUserId(
        ClaimsPrincipal user,
        CustosOptions options,
        IHostEnvironment environment
    ) => Resolve(user, options.DiscordIdClaim, options.DevelopmentDiscordUserId, environment);

    private static long? Resolve(
        ClaimsPrincipal user,
        string claimType,
        long? development,
        IHostEnvironment environment
    )
    {
        var claim = user.FindFirst(claimType)?.Value;
        if (claim is null)
            return environment.IsDevelopment() ? development : null;

        return
            long.TryParse(claim, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && id > 0
            ? id
            : null;
    }
}
