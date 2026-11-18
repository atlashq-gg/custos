using System.Globalization;
using System.Security.Claims;

namespace Custos.Server;

/// <summary>
/// The guild that a request belongs to. The access token names it in the claim <c>active_guild</c>, as a
/// Discord guild ID. A custom authenticator of the authorization server sets the claim.
/// </summary>
internal static class ActiveGuild
{
    public const string ClaimType = "active_guild";

    /// <summary>
    /// The guild ID from the token. Without the claim, <see cref="CustosOptions.DevelopmentGuildId"/> applies,
    /// but only in the environment Development. <c>null</c> means that the request has no valid guild.
    /// </summary>
    public static long? Resolve(ClaimsPrincipal user, CustosOptions options, IHostEnvironment environment)
    {
        var claim = user.FindFirst(ClaimType)?.Value;
        if (claim is null) return environment.IsDevelopment() ? options.DevelopmentGuildId : null;

        return long.TryParse(claim, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }
}
