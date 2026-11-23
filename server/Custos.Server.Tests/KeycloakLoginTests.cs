using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Custos.Server.Tests.Infrastructure;

namespace Custos.Server.Tests;

/// <summary>
/// The login of spec/02-authentication.md against Keycloak with the development realm: Keycloak
/// gets the Client ID Metadata Document from the server, and the token is used for an upload.
/// Needs Docker.
/// </summary>
[Category("Integration")]
public sealed class KeycloakLoginTests
{
    private KeycloakRealm _realm = null!;

    [OneTimeSetUp]
    public async Task StartAsync() => _realm = await KeycloakRealm.GetAsync();

    [Test]
    public async Task Login_through_the_metadata_document_gives_a_token_that_the_server_accepts()
    {
        using var http = new HttpClient(new KeycloakRealm.CookieJar());
        var discovery = await http.GetFromJsonAsync<JsonElement>(
            $"{_realm.ServerOrigin}/.well-known/custos-configuration"
        );
        var openid = await KeycloakRealm.OpenIdAsync(
            http,
            discovery.GetProperty("openid_configuration").GetString()!
        );
        string Member(string name) =>
            (
                discovery.TryGetProperty(name, out var value) ? value : openid.GetProperty(name)
            ).GetString()!;

        var token = await _realm.LoginAsync(
            http,
            openid,
            Member("client_id"),
            "custos://oauth2callback",
            "openid",
            resource: null
        );
        Assert.That(token.TryGetProperty("refresh_token", out _), Is.True, token.ToString());

        // The default client scopes of the realm give the CIMD client the claims of the server.
        var claims = KeycloakRealm.Payload(token.GetProperty("access_token").GetString()!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(claims.TryGetProperty("sub", out _), Is.True, claims.ToString());
            Assert.That(claims.TryGetProperty("discord_id", out var discordId), Is.True);
            Assert.That(discordId.GetString(), Is.EqualTo(KeycloakRealm.DevDiscordId));
            Assert.That(claims.TryGetProperty("aud", out _), Is.False, claims.ToString());
        }

        // The body is not a save file. 422 shows that the server accepted the token and read the
        // body.
        var upload = new ByteArrayContent([.. "GVAS"u8, 0, 0, 0, 0]);
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Post, Member("upload_endpoint"))
        {
            Content = upload,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token.GetProperty("access_token").GetString()
        );
        using var response = await http.SendAsync(request);

        Assert.That(
            response.StatusCode,
            Is.EqualTo(HttpStatusCode.UnprocessableEntity),
            await response.Content.ReadAsStringAsync()
        );
    }

    /// <summary>
    /// A harness reads the metadata of the MCP endpoint, registers its own client, and logs in
    /// with the two scopes. The token has the audience of the endpoint and the claim discord_id,
    /// and the endpoint accepts it.
    /// </summary>
    [Test]
    public async Task Harness_registers_a_client_and_calls_the_mcp_endpoint()
    {
        using var http = new HttpClient(new KeycloakRealm.CookieJar());
        var metadata = await http.GetFromJsonAsync<JsonElement>(
            $"{_realm.ServerOrigin}/.well-known/oauth-protected-resource"
        );
        var openid = await KeycloakRealm.OpenIdAsync(
            http,
            metadata.GetProperty("authorization_servers")[0].GetString()
                + "/.well-known/openid-configuration"
        );
        var scopes = string.Join(
            ' ',
            metadata.GetProperty("scopes_supported").EnumerateArray().Select(s => s.GetString())
        );

        const string redirectUri = "http://localhost:33418/callback";
        using var registration = await http.PostAsJsonAsync(
            openid.GetProperty("registration_endpoint").GetString(),
            new Dictionary<string, object>
            {
                ["client_name"] = "Test harness",
                ["redirect_uris"] = new[] { redirectUri },
                ["token_endpoint_auth_method"] = "none",
                ["grant_types"] = new[] { "authorization_code", "refresh_token" },
                ["response_types"] = new[] { "code" },
                ["scope"] = scopes,
            }
        );
        Assert.That(
            registration.StatusCode,
            Is.EqualTo(HttpStatusCode.Created),
            await registration.Content.ReadAsStringAsync()
        );
        var clientId = (await registration.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("client_id")
            .GetString()!;

        var token = await _realm.LoginAsync(
            http,
            openid,
            clientId,
            redirectUri,
            $"openid {scopes}",
            _realm.McpResource
        );
        var accessToken = token.GetProperty("access_token").GetString()!;
        var claims = KeycloakRealm.Payload(accessToken);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(claims.GetProperty("aud").ToString(), Does.Contain(_realm.McpResource));
            Assert.That(claims.GetProperty("scope").GetString(), Does.Contain("custos:read"));
            Assert.That(claims.GetProperty("scope").GetString(), Does.Contain("custos:write"));
            Assert.That(
                claims.GetProperty("discord_id").GetString(),
                Is.EqualTo(KeycloakRealm.DevDiscordId)
            );
        }

        using var mcp = new HttpClient { BaseAddress = new Uri(_realm.ServerOrigin) };
        mcp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            accessToken
        );
        await using var client = await Mcp.McpClients.ConnectAsync(mcp);
        var result = await client.CallToolAsync("list_guilds", new Dictionary<string, object?>());

        Assert.That(
            Mcp.McpClients.Text(result),
            Does.Contain($"\"guild_id\":\"{KeycloakRealm.KestrelFactory.Guild}\"")
        );
    }
}
