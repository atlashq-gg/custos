using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Custos.Server.Tests.Infrastructure;

namespace Custos.Server.Tests;

/// <summary>
/// The login of an MCP harness against Keycloak with the development realm. The login of Custos
/// is in the tests of Optio, which serves its Client ID Metadata Document. Needs Docker.
/// </summary>
[Category("Integration")]
public sealed class KeycloakLoginTests
{
    private KeycloakRealm _realm = null!;

    [OneTimeSetUp]
    public async Task StartAsync() => _realm = await KeycloakRealm.GetAsync();

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
