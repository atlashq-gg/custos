using Microsoft.Extensions.Options;

namespace Optio.Endpoints;

/// <summary>
/// The OAuth Client ID Metadata Document of Custos. Its URL is the client ID, and the authorization
/// server gets the registration of the public client from it.
/// </summary>
public sealed record ClientMetadataDocument(
    string ClientId,
    string ClientName,
    string[] RedirectUris,
    string[] GrantTypes,
    string[] ResponseTypes,
    string TokenEndpointAuthMethod
);

internal static class ClientMetadata
{
    public const string Path = "/.well-known/custos-client";

    /// <summary>The redirect URI of Custos (spec/02-authentication.md).</summary>
    public const string RedirectUri = "custos://oauth2callback";

    public static IEndpointRouteBuilder MapClientMetadata(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, (IOptions<OptioOptions> options) =>
                new ClientMetadataDocument(
                    ClientId: options.Value.ResolvedCustosClientId,
                    ClientName: "Custos",
                    RedirectUris: [RedirectUri],
                    GrantTypes: ["authorization_code", "refresh_token"],
                    ResponseTypes: ["code"],
                    TokenEndpointAuthMethod: "none"
                )
            )
            .AllowAnonymous();

        return app;
    }
}
