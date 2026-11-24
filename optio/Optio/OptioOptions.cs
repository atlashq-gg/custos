using System.ComponentModel.DataAnnotations;

namespace Optio;

/// <summary>The configuration section <c>Optio</c>.</summary>
public sealed class OptioOptions
{
    public const string SectionName = "Optio";

    /// <summary>The origin of optio as Custos sees it. The discovery document builds its URLs from
    /// it.</summary>
    [Required, Url]
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>The name that Custos shows to the user.</summary>
    [Required]
    public string ServiceName { get; set; } = "optio";

    /// <summary>A page for humans about the service, for the discovery document.</summary>
    [Url]
    public string? ServiceDocumentation { get; set; }

    /// <summary>The scopes that Custos requests. Set them in configuration: a binding appends to a
    /// default array.</summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>The largest upload. It must be equal to the value of the server.</summary>
    [Range(1, int.MaxValue)]
    public int UploadMaxBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>The upload interval of the server, for the discovery document. The server applies
    /// it.</summary>
    [Range(0, int.MaxValue)]
    public int UploadMinIntervalSeconds { get; set; } = 60;

    /// <summary>The issuer URL of the authorization server, for example a Keycloak realm.</summary>
    [Required, Url]
    public string Authority { get; set; } = "";

    /// <summary>
    /// The URL of the metadata document of the authorization server. Empty means
    /// <c>{Authority}/.well-known/openid-configuration</c>.
    /// </summary>
    [Url]
    public string? OpenIdConfiguration { get; set; }

    /// <summary>
    /// The client ID of Custos. Empty means the URL of the client metadata document of optio. Set
    /// it when the authorization server reaches optio under a different host than Custos.
    /// </summary>
    public string? CustosClientId { get; set; }

    /// <summary>The client ID of optio. A token of Custos for optio has it in <c>aud</c>.</summary>
    [Required]
    public string OptioClientId { get; set; } = "optio";

    /// <summary>The secret of the client optio, for the token exchange. Keep it in
    /// <c>/run/secrets/Optio__OptioClientSecret</c>.</summary>
    [Required]
    public string OptioClientSecret { get; set; } = "";

    /// <summary>The upload API of the server, for example
    /// <c>http://server:8080/api/v1/uploads</c>.</summary>
    [Required, Url]
    public string UploadApiUrl { get; set; } = "";

    /// <summary>The scope of the token for the upload API.</summary>
    [Required]
    public string UploadScope { get; set; } = "custos:write";

    public string ResolvedOpenIdConfiguration =>
        string.IsNullOrEmpty(OpenIdConfiguration)
            ? Combine(Authority, "/.well-known/openid-configuration")
            : OpenIdConfiguration;

    public string ResolvedCustosClientId =>
        string.IsNullOrEmpty(CustosClientId)
            ? Combine(PublicBaseUrl, Endpoints.ClientMetadata.Path)
            : CustosClientId;

    public static string Combine(string baseUrl, string path) => baseUrl.TrimEnd('/') + path;
}
