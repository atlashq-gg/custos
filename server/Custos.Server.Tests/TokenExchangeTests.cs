using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Custos.Server.Tests.Infrastructure;

namespace Custos.Server.Tests;

/// <summary>
/// The feasibility test M25 of issue #48: Optio exchanges the token of Custos (RFC 8693)
/// for a token of the MCP endpoint of the server. Needs Docker.
/// </summary>
[Category("Integration")]
public sealed class TokenExchangeTests
{
    private KeycloakRealm _realm = null!;

    [OneTimeSetUp]
    public async Task StartAsync() => _realm = await KeycloakRealm.GetAsync();

    [Test]
    public async Task Token_of_custos_with_the_scope_custos_client_has_the_audience_optio()
    {
        var token = await _realm.CustosTokenAsync("openid custos:client");

        var claims = KeycloakRealm.Payload(token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Audiences(claims), Is.EquivalentTo(new[] { "optio" }), claims.ToString());
            Assert.That(
                claims.GetProperty("azp").GetString(),
                Is.EqualTo(KeycloakRealm.CustosClient)
            );
        }
    }

    /// <summary>
    /// The feasibility test T3 of M37: Optio reads the Discord user from the token of Custos.
    /// The realm has no mapper for <c>active_guild</c> yet.
    /// </summary>
    [Test]
    public async Task Token_of_custos_for_optio_has_discord_id_and_no_active_guild()
    {
        var token = await _realm.CustosTokenAsync("openid custos:client");

        var claims = KeycloakRealm.Payload(token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                claims.GetProperty("discord_id").GetString(),
                Is.EqualTo(KeycloakRealm.DevDiscordId),
                claims.ToString()
            );
            Assert.That(claims.TryGetProperty("active_guild", out _), Is.False);
        }
    }

    [Test]
    public async Task Exchanged_token_is_only_for_the_mcp_endpoint_and_the_endpoint_accepts_it()
    {
        var subject = await _realm.CustosTokenAsync("openid custos:client");

        var (status, body) = await _realm.ExchangeAsync(subject, "custos:read");

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body.ToString());
        var token = body.GetProperty("access_token").GetString()!;
        var claims = KeycloakRealm.Payload(token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Audiences(claims), Is.EquivalentTo(new[] { _realm.McpResource }));
            Assert.That(claims.GetProperty("azp").GetString(), Is.EqualTo("optio"));
            Assert.That(claims.GetProperty("scope").GetString(), Is.EqualTo("custos:read"));
            Assert.That(
                claims.GetProperty("discord_id").GetString(),
                Is.EqualTo(KeycloakRealm.DevDiscordId)
            );
            Assert.That(
                claims.GetProperty("exp").GetInt64(),
                Is.LessThanOrEqualTo(KeycloakRealm.Payload(subject).GetProperty("exp").GetInt64())
            );
            Assert.That(body.TryGetProperty("refresh_token", out _), Is.False, body.ToString());
        }

        using var mcp = new HttpClient { BaseAddress = new Uri(_realm.ServerOrigin) };
        mcp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await using var client = await Mcp.McpClients.ConnectAsync(mcp);
        var result = await client.CallToolAsync("list_guilds", new Dictionary<string, object?>());

        Assert.That(
            Mcp.McpClients.Text(result),
            Does.Contain($"\"guild_id\":\"{KeycloakRealm.KestrelFactory.Guild}\"")
        );
    }

    [Test]
    public async Task Exchange_of_a_token_without_the_audience_optio_is_refused()
    {
        var subject = await _realm.CustosTokenAsync("openid");

        var (status, body) = await _realm.ExchangeAsync(subject, "custos:read");

        Assert.That((int)status, Is.InRange(400, 499), body.ToString());
    }

    /// <summary>
    /// The exchanged token gets its claims from the client scopes of <c>optio</c>, not from the
    /// scopes of Custos. A claim from a user session note stays.
    /// </summary>
    [Test]
    public async Task Exchanged_token_gets_the_claims_of_the_scopes_of_optio()
    {
        using var admin = await _realm.AdminAsync();
        using var created = await admin.PostAsJsonAsync(
            _realm.AdminUrl("client-scopes"),
            new
            {
                name = $"custos-guild-{Guid.NewGuid():N}",
                protocol = "openid-connect",
                attributes = new Dictionary<string, string>
                {
                    ["include.in.token.scope"] = "false",
                },
                protocolMappers = new[]
                {
                    new
                    {
                        name = "active_guild",
                        protocol = "openid-connect",
                        protocolMapper = "oidc-hardcoded-claim-mapper",
                        config = new Dictionary<string, string>
                        {
                            ["claim.name"] = "active_guild",
                            ["claim.value"] = "300000000000000002",
                            ["jsonType.label"] = "String",
                            ["access.token.claim"] = "true",
                        },
                    },
                },
            }
        );
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var scope = created.Headers.Location!.Segments[^1];

        var custos = await _realm.ClientUuidAsync(admin, KeycloakRealm.CustosClient);
        var optio = await _realm.ClientUuidAsync(admin, "optio");
        string ScopeOf(string client) =>
            _realm.AdminUrl($"clients/{client}/default-client-scopes/{scope}");

        try
        {
            (await admin.PutAsync(ScopeOf(custos), null)).EnsureSuccessStatusCode();
            var subject = await _realm.CustosTokenAsync("openid custos:client");
            var exchanged = await ExchangedClaimsAsync(subject);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(
                    KeycloakRealm.Payload(subject).GetProperty("active_guild").GetString(),
                    Is.EqualTo("300000000000000002")
                );
                Assert.That(exchanged.TryGetProperty("active_guild", out _), Is.False);
                // auth_time comes from the user session note AUTH_TIME (scope basic).
                Assert.That(exchanged.TryGetProperty("auth_time", out _), Is.True, $"{exchanged}");
            }

            (await admin.PutAsync(ScopeOf(optio), null)).EnsureSuccessStatusCode();
            exchanged = await ExchangedClaimsAsync(subject);
            Assert.That(
                exchanged.GetProperty("active_guild").GetString(),
                Is.EqualTo("300000000000000002")
            );
        }
        finally
        {
            await admin.DeleteAsync(ScopeOf(optio));
            await admin.DeleteAsync(ScopeOf(custos));
            await admin.DeleteAsync(_realm.AdminUrl($"client-scopes/{scope}"));
        }
    }

    private async Task<JsonElement> ExchangedClaimsAsync(string subject)
    {
        var (status, body) = await _realm.ExchangeAsync(subject, "custos:read");
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body.ToString());
        return KeycloakRealm.Payload(body.GetProperty("access_token").GetString()!);
    }

    private static string[] Audiences(JsonElement claims) =>
        !claims.TryGetProperty("aud", out var aud) ? []
        : aud.ValueKind == JsonValueKind.Array
            ? [.. aud.EnumerateArray().Select(a => a.GetString()!)]
        : [aud.GetString()!];
}
