using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Custos.Server.Endpoints;

/// <summary>The discovery document (spec/discovery.md).</summary>
public sealed record DiscoveryDocument(
    string[] CustosVersionsSupported,
    string ServiceName,
    string UploadEndpoint,
    int UploadMaxBytes,
    int UploadMinIntervalSeconds,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RevocationEndpoint,
    string ClientId,
    string[] Scopes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ServiceDocumentation);

internal static class Discovery
{
    public const string Path = "/.well-known/custos-configuration";

    /// <summary>The contract versions of spec/ that this server implements.</summary>
    public static readonly string[] SupportedVersions = ["0"];

    public static IEndpointRouteBuilder MapDiscovery(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, GetAsync)
            .WithName("GetDiscoveryDocument")
            .WithTags("Discovery")
            .WithSummary("The Custos discovery document.")
            .AllowAnonymous();

        return app;
    }

    private static async Task<DiscoveryDocument> GetAsync(
        IOptions<CustosOptions> options,
        IOptionsMonitor<JwtBearerOptions> jwt,
        CancellationToken cancellationToken)
    {
        var o = options.Value;
        var metadata = await GetAuthorizationServerMetadataAsync(
            jwt.Get(JwtBearerDefaults.AuthenticationScheme), cancellationToken);

        return new DiscoveryDocument(
            CustosVersionsSupported: SupportedVersions,
            ServiceName: o.ServiceName,
            UploadEndpoint: CustosOptions.Combine(o.PublicBaseUrl, Uploads.Path),
            UploadMaxBytes: o.UploadMaxBytes,
            UploadMinIntervalSeconds: o.UploadMinIntervalSeconds,
            AuthorizationEndpoint: metadata.AuthorizationEndpoint,
            TokenEndpoint: metadata.TokenEndpoint,
            RevocationEndpoint: string.IsNullOrEmpty(metadata.RevocationEndpoint) ? null : metadata.RevocationEndpoint,
            ClientId: o.ResolvedClientId,
            Scopes: o.Scopes,
            ServiceDocumentation: CustosOptions.Combine(o.PublicBaseUrl, OpenApiSetup.ScalarPath));
    }

    /// <summary>
    /// The metadata of the authorization server. The JWT handler already gets it to validate tokens, so it
    /// is read from the same cache.
    /// </summary>
    private static async Task<OpenIdConnectConfiguration> GetAuthorizationServerMetadataAsync(
        JwtBearerOptions jwt, CancellationToken cancellationToken) =>
        jwt.Configuration
        ?? await (jwt.ConfigurationManager
                  ?? throw new InvalidOperationException("The JWT handler has no configuration manager."))
            .GetConfigurationAsync(cancellationToken);
}
