using System.Globalization;
using System.Security.Claims;

namespace Optio;

/// <summary>
/// The Discord IDs in the access token of Custos: the guild in the claim <c>active_guild</c>, and
/// the user in the claim <c>discord_id</c>. Without a claim, the development value of
/// <see cref="OptioOptions"/> applies, but only in the environment Development, as in the server.
/// </summary>
internal static class TokenClaims
{
    public const string ActiveGuild = "active_guild";
    public const string DiscordId = "discord_id";

    /// <summary>The guild ID, or null when the request has no valid guild.</summary>
    public static ulong? GuildId(
        ClaimsPrincipal user,
        OptioOptions options,
        IHostEnvironment environment
    ) => Resolve(user, ActiveGuild, options.DevelopmentGuildId, environment);

    /// <summary>The Discord user ID, or null when the request has no valid user.</summary>
    public static ulong? DiscordUserId(
        ClaimsPrincipal user,
        OptioOptions options,
        IHostEnvironment environment
    ) => Resolve(user, DiscordId, options.DevelopmentDiscordUserId, environment);

    private static ulong? Resolve(
        ClaimsPrincipal user,
        string claimType,
        long? development,
        IHostEnvironment environment
    )
    {
        var claim = user.FindFirst(claimType)?.Value;
        if (claim is null)
            return environment.IsDevelopment() && development is > 0 and var d ? (ulong)d : null;

        return
            ulong.TryParse(claim, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && id > 0
            ? id
            : null;
    }
}
