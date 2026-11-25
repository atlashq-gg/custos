using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Custos.Server.Storage;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Custos.Server.Tests.Infrastructure;

/// <summary>
/// One Keycloak container with the development realm, and one server on Kestrel, for the test
/// run. The browser part of a login is done with plain HTTP requests. Testcontainers removes the
/// container after the run.
/// </summary>
internal sealed partial class KeycloakRealm
{
    public const string Realm = "custos";
    public const string ContainerHost = "host.testcontainers.internal";
    public const string DevDiscordId = "123456789012345678";
    public const string OptioSecret = "optio-test-secret";

    /// <summary>
    /// A public client that stands in for Custos. optio serves the Client ID Metadata Document of
    /// Custos, and the tests of optio log in through it.
    /// </summary>
    public const string CustosClient = "custos-test";

    private const string CustosRedirectUri = "http://localhost:33419/callback";

    private static readonly Lazy<Task<KeycloakRealm>> Shared = new(StartAsync);

    private readonly IContainer _keycloak;

    private KeycloakRealm(int serverPort, IContainer keycloak)
    {
        ServerPort = serverPort;
        _keycloak = keycloak;
    }

    public int ServerPort { get; }

    public string ServerOrigin => $"http://localhost:{ServerPort}";

    public string McpResource => $"{ServerOrigin}/mcp";

    public string Origin => $"http://localhost:{_keycloak.GetMappedPublicPort(8080)}";

    public string TokenEndpoint => $"{Origin}/realms/{Realm}/protocol/openid-connect/token";

    /// <summary>The realm of the test run. It starts on first use.</summary>
    public static Task<KeycloakRealm> GetAsync() => Shared.Value;

    private static async Task<KeycloakRealm> StartAsync()
    {
        var serverPort = FreePort();
        await TestcontainersSettings.ExposeHostPortsAsync((ushort)serverPort);

        var keycloak = new ContainerBuilder("quay.io/keycloak/keycloak:26.8.2")
            .WithCommand("start-dev", "--features=cimd,resource-indicators", "--import-realm")
            .WithResourceMapping(
                FilePath.Of(
                    Path.Combine(AppContext.BaseDirectory, "keycloak", "custos-realm.json")
                ),
                DirectoryPath.Of("/opt/keycloak/data/import/")
            )
            .WithEnvironment("CUSTOS_CIMD_HOST", ContainerHost)
            .WithEnvironment("CUSTOS_MCP_RESOURCE", $"http://localhost:{serverPort}/mcp")
            .WithEnvironment("OPTIO_CLIENT_SECRET", OptioSecret)
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "admin")
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath($"/realms/{Realm}"))
            )
            .Build();
        await keycloak.StartAsync();

        var realm = new KeycloakRealm(serverPort, keycloak);
        await realm.SetDiscordIdAsync("dev", DevDiscordId);
        await realm.AddCustosClientAsync();

        var server = new KestrelFactory(
            discordId: ulong.Parse(DevDiscordId),
            port: serverPort,
            authority: $"{realm.Origin}/realms/{Realm}"
        );
        server.UseKestrel(serverPort);
        server.StartServer();
        return realm;
    }

    public static async Task<JsonElement> OpenIdAsync(HttpClient http, string url) =>
        await http.GetFromJsonAsync<JsonElement>(url);

    /// <summary>
    /// The login of the user <c>dev</c> through <see cref="CustosClient"/> with
    /// <paramref name="scope"/>. Gives the access token.
    /// </summary>
    public async Task<string> CustosTokenAsync(string scope) =>
        (await CustosLoginAsync(scope)).GetProperty("access_token").GetString()!;

    /// <summary>
    /// The login of the user <c>dev</c> through <see cref="CustosClient"/> with
    /// <paramref name="scope"/>. Gives the answer of the token endpoint.
    /// </summary>
    public async Task<JsonElement> CustosLoginAsync(string scope)
    {
        using var http = new HttpClient(new CookieJar());
        var openid = await OpenIdAsync(
            http,
            $"{Origin}/realms/{Realm}/.well-known/openid-configuration"
        );
        return await LoginAsync(http, openid, CustosClient, CustosRedirectUri, scope, null);
    }

    /// <summary>A refresh of a token of <see cref="CustosClient"/>. Gives the answer of the token
    /// endpoint.</summary>
    public async Task<JsonElement> RefreshAsync(string refreshToken)
    {
        using var http = new HttpClient();
        using var response = await http.PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = CustosClient,
                    ["refresh_token"] = refreshToken,
                }
            )
        );
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// The login of a browser and the code exchange, with PKCE: the login page, the form, the
    /// consent page, and the token endpoint. Gives the answer of the token endpoint.
    /// </summary>
    public async Task<JsonElement> LoginAsync(
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
    /// The standard token exchange (RFC 8693) as the client <c>optio</c>. Gives the answer of
    /// the token endpoint, also when it is an error.
    /// </summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> ExchangeAsync(
        string subjectToken,
        string scope
    )
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
                    ["subject_token"] = subjectToken,
                    ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
                    ["scope"] = scope,
                }
            ),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"optio:{OptioSecret}"))
        );
        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    /// <summary>An HTTP client with a token of the administrator of the master realm.</summary>
    public async Task<HttpClient> AdminAsync()
    {
        var http = new HttpClient();
        using var login = await http.PostAsync(
            $"{Origin}/realms/master/protocol/openid-connect/token",
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
        return http;
    }

    /// <summary>The admin API URL of the realm.</summary>
    public string AdminUrl(string path) => $"{Origin}/admin/realms/{Realm}/{path}";

    /// <summary>The internal ID of a client, for the admin API.</summary>
    public async Task<string> ClientUuidAsync(HttpClient admin, string clientId) =>
        (
            await admin.GetFromJsonAsync<JsonElement[]>(
                AdminUrl($"clients?clientId={Uri.EscapeDataString(clientId)}")
            )
        )!
            .Single()
            .GetProperty("id")
            .GetString()!;

    public static JsonElement Payload(string jwt)
    {
        var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        return JsonDocument
            .Parse(Convert.FromBase64String(part.PadRight((part.Length + 3) / 4 * 4, '=')))
            .RootElement;
    }

    /// <summary>
    /// The public client <see cref="CustosClient"/>, with the scopes of the realm.
    /// </summary>
    private async Task AddCustosClientAsync()
    {
        using var http = await AdminAsync();
        using var response = await http.PostAsJsonAsync(
            AdminUrl("clients"),
            new
            {
                clientId = CustosClient,
                publicClient = true,
                standardFlowEnabled = true,
                directAccessGrantsEnabled = false,
                consentRequired = false,
                redirectUris = new[] { CustosRedirectUri },
            }
        );
        Assert.That(
            response.IsSuccessStatusCode,
            Is.True,
            await response.Content.ReadAsStringAsync()
        );
    }

    /// <summary>
    /// Sets the user attribute <c>discord_id</c> through the admin API. Keycloak keeps the
    /// attribute only because the user profile of the realm declares it.
    /// </summary>
    private async Task SetDiscordIdAsync(string username, string discordId)
    {
        using var http = await AdminAsync();
        var users = AdminUrl("users");
        var user = (
            await http.GetFromJsonAsync<JsonElement[]>($"{users}?username={username}&exact=true")
        )!.Single();
        var changed = JsonNode.Parse(user.GetRawText())!;
        changed["attributes"] = new JsonObject { ["discord_id"] = new JsonArray(discordId) };
        using var update = await http.PutAsJsonAsync(
            $"{users}/{user.GetProperty("id").GetString()}",
            changed
        );
        Assert.That(update.IsSuccessStatusCode, Is.True, await update.Content.ReadAsStringAsync());
    }

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
        return action.StartsWith('/') ? $"{Origin}{action}" : action;
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
    public sealed class CookieJar()
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

    public sealed class KestrelFactory(ulong discordId, int port, string authority)
        : WebApplicationFactory<Program>
    {
        public const ulong Guild = 300000000000000001;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Development: the realm sets no claim active_guild, so the server uses
            // DevelopmentGuildId. The fake Discord has the user dev in that guild.
            builder.UseEnvironment("Development");
            builder.UseSetting("Custos:Authority", authority);
            builder.UseSetting("Custos:Resource", $"http://localhost:{port}/mcp");
            builder.UseSetting("Custos:UploadMinIntervalSeconds", "0");
            builder.UseSetting("Custos:WarPollIntervalSeconds", "0");
            builder.UseSetting("Custos:DevelopmentGuildId", Guild.ToString());
            builder.UseSetting("Mcp:Enabled", "true");
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
