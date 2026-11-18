using System.ComponentModel.DataAnnotations;

namespace Custos.Server;

/// <summary>The configuration section <c>Custos</c>.</summary>
public sealed class CustosOptions
{
    public const string SectionName = "Custos";

    /// <summary>The origin of this server as Custos sees it. The discovery document builds its URLs from it.</summary>
    [Required, Url]
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>The issuer URL of the authorization server, for example a Keycloak realm.</summary>
    [Required, Url]
    public string Authority { get; set; } = "";

    /// <summary>
    /// The client ID that Custos uses. Empty means the URL of the client metadata document of this server.
    /// Set it when the authorization server reaches this server under a different host than Custos.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// The URL of the metadata document of the authorization server. Empty means
    /// <c>{Authority}/.well-known/openid-configuration</c>.
    /// </summary>
    public string? OpenIdConfiguration { get; set; }

    public string ServiceName { get; set; } = "Custos reference server";

    /// <summary>The scopes that Custos requests. Set them in configuration: a binding appends to a default array.</summary>
    public string[] Scopes { get; set; } = [];

    [Range(1, int.MaxValue)]
    public int UploadMaxBytes { get; set; } = 4 * 1024 * 1024;

    [Range(0, int.MaxValue)]
    public int UploadMinIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// The origin of the Foxhole war API. Each shard has its own host, for example war-service-live-2 for
    /// shard 2.
    /// </summary>
    [Required, Url]
    public string WarApiBaseUrl { get; set; } = "https://war-service-live.foxholeservices.com";

    /// <summary>
    /// A stable label for the shard of <see cref="WarApiBaseUrl"/>. The war API does not label its shard. The
    /// label is a part of the ID of each structure, so do not change it during a war. The server supports one shard.
    /// </summary>
    [Required]
    public string Shard { get; set; } = "live";

    /// <summary>The time between two requests for the current war. <c>0</c> stops the requests.</summary>
    [Range(0, int.MaxValue)]
    public int WarPollIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// The guild of a request whose token has no claim <c>active_guild</c>. The server uses it only in the
    /// environment Development, and ignores it in all other environments.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long? DevelopmentGuildId { get; set; }

    public string ResolvedOpenIdConfiguration =>
        string.IsNullOrEmpty(OpenIdConfiguration) ? Combine(Authority, "/.well-known/openid-configuration") : OpenIdConfiguration;

    public string ResolvedClientId =>
        string.IsNullOrEmpty(ClientId) ? Combine(PublicBaseUrl, Endpoints.ClientMetadata.Path) : ClientId;

    public static string Combine(string baseUrl, string path) => baseUrl.TrimEnd('/') + path;
}
