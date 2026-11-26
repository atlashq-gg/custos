using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Optio.Events;
using Optio.Queries;

namespace Optio.Endpoints;

/// <summary>The discovery document (spec/01-discovery.md).</summary>
public sealed record DiscoveryDocument(
    string[] CustosVersionsSupported,
    string ServiceName,
    string[] FeaturesSupported,
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
                // The stream and the query only when they are configured (StreamSetup, QuerySetup).
                var stream = http.RequestServices.GetService<EventStream>() is not null;
                var query = http.RequestServices.GetService<QueryService>() is not null;
                return new DiscoveryDocument(
                    CustosVersionsSupported: SupportedVersions,
                    ServiceName: o.ServiceName,
                    FeaturesSupported: Features(stream, query),
                    UploadEndpoint: OptioOptions.Combine(o.PublicBaseUrl, Uploads.Path),
                    UploadMaxBytes: o.UploadMaxBytes,
                    UploadMinIntervalSeconds: o.UploadMinIntervalSeconds,
                    OpenidConfiguration: o.ResolvedOpenIdConfiguration,
                    ClientId: o.ResolvedCustosClientId,
                    Scopes: o.Scopes,
                    ServiceDocumentation: o.ServiceDocumentation,
                    StreamEndpoint: stream
                        ? OptioOptions.Combine(o.PublicBaseUrl, StreamEndpoint.Path)
                        : null,
                    QueryEndpoint: stream && query
                        ? OptioOptions.Combine(o.PublicBaseUrl, Endpoints.QueryEndpoint.Path)
                        : null
                );
            })
            .AllowAnonymous();

        return app;
    }

    /// <summary>
    /// The features of spec/01-discovery.md that Optio has turned on. The upload is always on. The
    /// query needs the stream.
    /// </summary>
    public static string[] Features(bool stream, bool query) =>
        [
            "upload",
            .. stream ? ["stream"] : Array.Empty<string>(),
            .. stream && query ? ["query"] : Array.Empty<string>(),
        ];
}
