using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Optio.Endpoints;

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

    /// <summary>The contract versions of spec/ that optio implements.</summary>
    public static readonly string[] SupportedVersions = ["0"];

    public static IEndpointRouteBuilder MapDiscovery(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, (IOptions<OptioOptions> options) =>
            {
                var o = options.Value;
                return new DiscoveryDocument(
                    CustosVersionsSupported: SupportedVersions,
                    ServiceName: o.ServiceName,
                    UploadEndpoint: OptioOptions.Combine(o.PublicBaseUrl, Uploads.Path),
                    UploadMaxBytes: o.UploadMaxBytes,
                    UploadMinIntervalSeconds: o.UploadMinIntervalSeconds,
                    OpenidConfiguration: o.ResolvedOpenIdConfiguration,
                    ClientId: o.ResolvedCustosClientId,
                    Scopes: o.Scopes,
                    ServiceDocumentation: o.ServiceDocumentation
                );
            })
            .AllowAnonymous();

        return app;
    }
}
