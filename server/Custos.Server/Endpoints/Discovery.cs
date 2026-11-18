using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Custos.Server.Endpoints;

/// <summary>The discovery document (spec/01-discovery.md).</summary>
public sealed record DiscoveryDocument(
    string[] CustosVersionsSupported,
    string ServiceName,
    string UploadEndpoint,
    int UploadMaxBytes,
    int UploadMinIntervalSeconds,
    string OpenidConfiguration,
    string ClientId,
    string[] Scopes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ServiceDocumentation
);

internal static class Discovery
{
    public const string Path = "/.well-known/custos-configuration";

    /// <summary>The contract versions of spec/ that this server implements.</summary>
    public static readonly string[] SupportedVersions = ["0"];

    public static IEndpointRouteBuilder MapDiscovery(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, (IOptions<CustosOptions> options) =>
            {
                var o = options.Value;
                return new DiscoveryDocument(
                    CustosVersionsSupported: SupportedVersions,
                    ServiceName: o.ServiceName,
                    UploadEndpoint: CustosOptions.Combine(o.PublicBaseUrl, Uploads.Path),
                    UploadMaxBytes: o.UploadMaxBytes,
                    UploadMinIntervalSeconds: o.UploadMinIntervalSeconds,
                    OpenidConfiguration: o.ResolvedOpenIdConfiguration,
                    ClientId: o.ResolvedClientId,
                    Scopes: o.Scopes,
                    ServiceDocumentation: CustosOptions.Combine(
                        o.PublicBaseUrl,
                        OpenApiSetup.ScalarPath
                    )
                );
            })
            .WithName("GetDiscoveryDocument")
            .WithTags("Discovery")
            .WithSummary("The Custos discovery document.")
            .AllowAnonymous();

        return app;
    }
}
