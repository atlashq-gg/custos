using Microsoft.Extensions.Options;

namespace Custos.Server.Endpoints;

/// <summary>
/// The OAuth Client ID Metadata Document of Custos. Its URL is the client ID, and the authorization server
/// gets the registration of the public client from it.
/// </summary>
public sealed record ClientMetadataDocument(
    string ClientId,
    string ClientName,
    string[] RedirectUris,
    string[] GrantTypes,
    string[] ResponseTypes,
    string TokenEndpointAuthMethod);

internal static class ClientMetadata
{
    public const string Path = "/custos/client-metadata.json";

    /// <summary>The redirect URI of Custos (spec/authentication.md).</summary>
    public const string RedirectUri = "custos://oauth2callback";

    public static IEndpointRouteBuilder MapClientMetadata(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, (IOptions<CustosOptions> options) => new ClientMetadataDocument(
                ClientId: options.Value.ResolvedClientId,
                ClientName: "Custos",
                RedirectUris: [RedirectUri],
                GrantTypes: ["authorization_code", "refresh_token"],
                ResponseTypes: ["code"],
                TokenEndpointAuthMethod: "none"))
            .WithName("GetClientMetadata")
            .WithTags("Discovery")
            .WithSummary("The Client ID Metadata Document of Custos.")
            .AllowAnonymous();

        return app;
    }
}
