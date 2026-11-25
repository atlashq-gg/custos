using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Optio.Events;
using Optio.Queries;

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
        string? ServiceDocumentation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StreamEndpoint,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? QueryEndpoint
);

internal static class Discovery
{
    public const string Path = "/.well-known/custos-configuration";

    /// <summary>The contract versions of spec/ that Optio implements.</summary>
    public static readonly string[] SupportedVersions = ["0"];

    public static IEndpointRouteBuilder MapDiscovery(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, (HttpContext http, IOptions<OptioOptions> options) =>
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
                    ServiceDocumentation: o.ServiceDocumentation,
                    // Only when the stream is configured (StreamSetup).
                    StreamEndpoint: http.RequestServices.GetService<EventStream>() is null
                        ? null
                        : OptioOptions.Combine(o.PublicBaseUrl, StreamEndpoint.Path),
                    // Only when the query is configured (QuerySetup).
                    QueryEndpoint: http.RequestServices.GetService<QueryService>() is null
                        ? null
                        : OptioOptions.Combine(o.PublicBaseUrl, Endpoints.QueryEndpoint.Path)
                );
            })
            .AllowAnonymous();

        return app;
    }
}
