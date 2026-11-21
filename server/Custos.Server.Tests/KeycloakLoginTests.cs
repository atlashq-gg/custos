using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Custos.Server.Storage;
using Custos.Server.Tests.Infrastructure;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Custos.Server.Tests;

/// <summary>
/// The login of spec/02-authentication.md against Keycloak with the development realm: Keycloak
/// gets the Client ID Metadata Document from the server, the browser part is done with plain HTTP
/// requests, and the token is used for an upload. Needs Docker.
/// </summary>
[Category("Integration")]
public sealed partial class KeycloakLoginTests
{
    private const string Realm = "custos";
    private const string ContainerHost = "host.testcontainers.internal";
    private const string DevDiscordId = "123456789012345678";

    private int _serverPort;
    private string McpResource => $"http://localhost:{_serverPort}/mcp";
    private IContainer _keycloak = null!;
    private KestrelFactory _server = null!;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        _serverPort = FreePort();
        await TestcontainersSettings.ExposeHostPortsAsync((ushort)_serverPort);

        _keycloak = new ContainerBuilder("quay.io/keycloak/keycloak:26.8.2")
            .WithCommand("start-dev", "--features=cimd,resource-indicators", "--import-realm")
            .WithResourceMapping(
                FilePath.Of(
                    Path.Combine(AppContext.BaseDirectory, "keycloak", "custos-realm.json")
                ),
                DirectoryPath.Of("/opt/keycloak/data/import/")
            )
            .WithEnvironment("CUSTOS_CIMD_HOST", ContainerHost)
            .WithEnvironment("CUSTOS_MCP_RESOURCE", McpResource)
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "admin")
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath($"/realms/{Realm}"))
            )
            .Build();
        await _keycloak.StartAsync();
        await SetDiscordIdAsync("dev", DevDiscordId);

        _server = new KestrelFactory(
            discordId: ulong.Parse(DevDiscordId),
            port: _serverPort,
            authority: $"http://localhost:{_keycloak.GetMappedPublicPort(8080)}/realms/{Realm}",
            clientId: $"http://{ContainerHost}:{_serverPort}/.well-known/custos-client"
        );
        _server.UseKestrel(_serverPort);
        _server.StartServer();
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        await _server.DisposeAsync();
        await _keycloak.DisposeAsync();
    }

    [Test]
    public async Task Login_through_the_metadata_document_gives_a_token_that_the_server_accepts()
    {
        using var http = new HttpClient(new CookieJar());
        var discovery = await http.GetFromJsonAsync<JsonElement>(
            $"http://localhost:{_serverPort}/.well-known/custos-configuration"
        );
        var openid = await OpenIdAsync(
            http,
            discovery.GetProperty("openid_configuration").GetString()!
        );
        string Member(string name) =>
            (
                discovery.TryGetProperty(name, out var value) ? value : openid.GetProperty(name)
            ).GetString()!;

        var token = await LoginAsync(
            http,
            openid,
            Member("client_id"),
            "custos://oauth2callback",
            "openid",
            resource: null
        );
        Assert.That(token.TryGetProperty("refresh_token", out _), Is.True, token.ToString());

        // The default client scopes of the realm give the CIMD client the claims of the server.
        var claims = Payload(token.GetProperty("access_token").GetString()!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(claims.TryGetProperty("sub", out _), Is.True, claims.ToString());
            Assert.That(claims.TryGetProperty("discord_id", out var discordId), Is.True);
            Assert.That(discordId.GetString(), Is.EqualTo(DevDiscordId));
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
        using var http = new HttpClient(new CookieJar());
        var metadata = await http.GetFromJsonAsync<JsonElement>(
            $"http://localhost:{_serverPort}/.well-known/oauth-protected-resource"
        );
        var openid = await OpenIdAsync(
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

        var token = await LoginAsync(
            http,
            openid,
            clientId,
            redirectUri,
            $"openid {scopes}",
            McpResource
        );
        var accessToken = token.GetProperty("access_token").GetString()!;
        var claims = Payload(accessToken);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(claims.GetProperty("aud").ToString(), Does.Contain(McpResource));
            Assert.That(claims.GetProperty("scope").GetString(), Does.Contain("custos:read"));
            Assert.That(claims.GetProperty("scope").GetString(), Does.Contain("custos:write"));
            Assert.That(claims.GetProperty("discord_id").GetString(), Is.EqualTo(DevDiscordId));
        }

        using var mcp = new HttpClient { BaseAddress = new Uri($"http://localhost:{_serverPort}") };
        mcp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            accessToken
        );
        await using var client = await Mcp.McpClients.ConnectAsync(mcp);
        var result = await client.CallToolAsync("list_guilds", new Dictionary<string, object?>());

        Assert.That(
            Mcp.McpClients.Text(result),
            Does.Contain($"\"guild_id\":\"{KestrelFactory.Guild}\"")
        );
    }

    private static async Task<JsonElement> OpenIdAsync(HttpClient http, string url) =>
        await http.GetFromJsonAsync<JsonElement>(url);

    /// <summary>
    /// The login of a browser and the code exchange, with PKCE: the login page, the form, the
    /// consent page, and the token endpoint. Gives the answer of the token endpoint.
    /// </summary>
    private async Task<JsonElement> LoginAsync(
        HttpClient http,
        JsonElement openid,
        string clientId,
        string redirectUri,
        string scope,
        string? resource
    )
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var resourceParameter = resource is null
            ? ""
            : $"&resource={Uri.EscapeDataString(resource)}";

        var authorize =
            $"{openid.GetProperty("authorization_endpoint").GetString()}?response_type=code"
            + $"&client_id={Uri.EscapeDataString(clientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + $"&scope={Uri.EscapeDataString(scope)}&state=s1"
            + $"&code_challenge={challenge}&code_challenge_method=S256{resourceParameter}";
        var login = await http.GetStringAsync(authorize);
        var location = await PostFormAsync(
            http,
            FormAction(login),
            new() { ["username"] = "dev", ["password"] = "dev" }
        );

        if (!location.StartsWith(redirectUri, StringComparison.Ordinal))
        {
            var consent = await http.GetStringAsync(location);
            location = await PostFormAsync(
                http,
                FormAction(consent),
                new() { ["accept"] = "Yes", ["code"] = HiddenCode().Match(consent).Groups[1].Value }
            );
        }

        Assert.That(location, Does.StartWith(redirectUri));
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(location).Query);
        Assert.That(query["state"], Is.EqualTo("s1"));

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code"] = query["code"]!,
            ["code_verifier"] = verifier,
        };
        if (resource is not null)
            form["resource"] = resource;
        using var response = await http.PostAsync(
            openid.GetProperty("token_endpoint").GetString(),
            new FormUrlEncodedContent(form)
        );
        var token = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(token.TryGetProperty("access_token", out _), Is.True, token.ToString());
        return token;
    }

    /// <summary>
    /// Sets the user attribute <c>discord_id</c> through the admin API. Keycloak keeps the
    /// attribute only because the user profile of the realm declares it.
    /// </summary>
    private async Task SetDiscordIdAsync(string username, string discordId)
    {
        var origin = $"http://localhost:{_keycloak.GetMappedPublicPort(8080)}";
        using var http = new HttpClient();
        using var login = await http.PostAsync(
            $"{origin}/realms/master/protocol/openid-connect/token",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "password",
                    ["client_id"] = "admin-cli",
                    ["username"] = "admin",
                    ["password"] = "admin",
                }
            )
        );
        var admin = await login.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            admin.GetProperty("access_token").GetString()
        );

        var users = $"{origin}/admin/realms/{Realm}/users";
        var user = (
            await http.GetFromJsonAsync<JsonElement[]>($"{users}?username={username}&exact=true")
        )!.Single();
        var changed = System.Text.Json.Nodes.JsonNode.Parse(user.GetRawText())!;
        changed["attributes"] = new System.Text.Json.Nodes.JsonObject
        {
            ["discord_id"] = new System.Text.Json.Nodes.JsonArray(discordId),
        };
        using var update = await http.PutAsJsonAsync(
            $"{users}/{user.GetProperty("id").GetString()}",
            changed
        );
        Assert.That(update.IsSuccessStatusCode, Is.True, await update.Content.ReadAsStringAsync());
    }

    private static JsonElement Payload(string jwt) =>
        JsonDocument
            .Parse(
                Convert.FromBase64String(
                    jwt.Split('.')[1]
                        .Replace('-', '+')
                        .Replace('_', '/')
                        .PadRight((jwt.Split('.')[1].Length + 3) / 4 * 4, '=')
                )
            )
            .RootElement;

    private static async Task<string> PostFormAsync(
        HttpClient http,
        string action,
        Dictionary<string, string> form
    )
    {
        using var response = await http.PostAsync(action, new FormUrlEncodedContent(form));
        Assert.That(
            response.StatusCode,
            Is.EqualTo(HttpStatusCode.Found),
            await response.Content.ReadAsStringAsync()
        );
        return response.Headers.Location!.ToString();
    }

    private string FormAction(string html)
    {
        var action = WebUtility.HtmlDecode(FormActionPattern().Match(html).Groups[1].Value);
        return action.StartsWith('/')
            ? $"http://localhost:{_keycloak.GetMappedPublicPort(8080)}{action}"
            : action;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [GeneratedRegex("<form[^>]*action=\"([^\"]+)\"")]
    private static partial Regex FormActionPattern();

    [GeneratedRegex("name=\"code\" value=\"([^\"]+)\"")]
    private static partial Regex HiddenCode();

    /// <summary>
    /// Keeps cookies as a browser does on localhost. Keycloak marks its cookies Secure, and a
    /// browser sends them to http://localhost, but <see cref="CookieContainer"/> does not.
    /// </summary>
    private sealed class CookieJar()
        : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        private readonly Dictionary<string, string> _cookies = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (_cookies.Count > 0)
                request.Headers.Add(
                    "Cookie",
                    string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}"))
                );

            var response = await base.SendAsync(request, cancellationToken);
            foreach (
                var header in response.Headers.TryGetValues("Set-Cookie", out var values)
                    ? values
                    : []
            )
            {
                var pair = header.Split(';', 2)[0].Split('=', 2);
                _cookies[pair[0]] = pair[1];
            }

            return response;
        }
    }

    private sealed class KestrelFactory(
        ulong discordId,
        int port,
        string authority,
        string clientId
    ) : WebApplicationFactory<Program>
    {
        public const ulong Guild = 300000000000000001;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Development: the realm sets no claim active_guild, so the server uses
            // DevelopmentGuildId. The fake Discord has the user dev in that guild.
            builder.UseEnvironment("Development");
            builder.UseSetting("Custos:PublicBaseUrl", $"http://localhost:{port}");
            builder.UseSetting("Custos:Authority", authority);
            builder.UseSetting("Custos:ClientId", clientId);
            builder.UseSetting("Custos:Scopes:0", "openid");
            builder.UseSetting("Custos:UploadMinIntervalSeconds", "0");
            builder.UseSetting("Custos:WarPollIntervalSeconds", "0");
            builder.UseSetting("Custos:DevelopmentGuildId", Guild.ToString());
            builder.UseSetting("Mcp:Enabled", "true");
            builder.UseSetting("Mcp:Resource", $"http://localhost:{port}/mcp");
            builder.UseSetting(
                $"ConnectionStrings:{CustosDbContext.ConnectionName}",
                Postgres.NewDatabase()
            );
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<Custos.Server.Discord.IDiscordDirectory>(
                    new FakeDiscordDirectory().Add(Guild, discordId, administrator: true)
                );
                services.AddScoped<Custos.Server.Discord.GuildAccessEvaluator>();
            });
        }
    }
}
