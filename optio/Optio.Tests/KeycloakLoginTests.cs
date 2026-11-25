using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Optio.Endpoints;
using Optio.Tests.Infrastructure;

namespace Optio.Tests;

/// <summary>
/// The login of spec/02-authentication.md against Keycloak with the development realm of the
/// server: Keycloak gets the Client ID Metadata Document of Custos from Optio. Optio then
/// exchanges the token of Custos for a token of the upload API. The browser part is done with
/// plain HTTP requests. Needs Docker.
/// </summary>
[Category("Integration")]
public sealed partial class KeycloakLoginTests
{
    private const string Realm = "custos";
    private const string ContainerHost = "host.testcontainers.internal";
    private const string ServerResource = "http://server.test/mcp";
    private const string DevDiscordId = "123456789012345678";

    private int _port;
    private IContainer _keycloak = null!;
    private KestrelFactory _optio = null!;

    private string KeycloakOrigin => $"http://localhost:{_keycloak.GetMappedPublicPort(8080)}";

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        _port = FreePort();
        await TestcontainersSettings.ExposeHostPortsAsync((ushort)_port);

        _keycloak = new ContainerBuilder("quay.io/keycloak/keycloak:26.8.2")
            .WithCommand("start-dev", "--features=cimd,resource-indicators", "--import-realm")
            .WithResourceMapping(
                FilePath.Of(
                    Path.Combine(AppContext.BaseDirectory, "keycloak", "custos-realm.json")
                ),
                DirectoryPath.Of("/opt/keycloak/data/import/")
            )
            .WithEnvironment("CUSTOS_CIMD_HOST", ContainerHost)
            .WithEnvironment("CUSTOS_MCP_RESOURCE", ServerResource)
            .WithEnvironment("OPTIO_CLIENT_SECRET", OptioFactory.Secret)
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "admin")
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath($"/realms/{Realm}"))
            )
            .Build();
        await _keycloak.StartAsync();
        await SetDiscordIdAsync();

        _optio = new KestrelFactory(_port, $"{KeycloakOrigin}/realms/{Realm}");
        _optio.UseKestrel(_port);
        _optio.StartServer();
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        await _optio.DisposeAsync();
        await _keycloak.DisposeAsync();
    }

    [Test]
    public async Task Custos_logs_in_through_optio_and_the_upload_has_a_token_of_the_server()
    {
        using var http = new HttpClient(new CookieJar());
        var discovery = await http.GetFromJsonAsync<JsonElement>(
            $"http://localhost:{_port}/.well-known/custos-configuration"
        );
        var openid = await http.GetFromJsonAsync<JsonElement>(
            discovery.GetProperty("openid_configuration").GetString()!
        );
        var scopes = string.Join(
            ' ',
            discovery.GetProperty("scopes").EnumerateArray().Select(s => s.GetString())
        );

        var token = await LoginAsync(
            http,
            openid,
            discovery.GetProperty("client_id").GetString()!,
            scopes
        );
        Assert.That(Payload(token).GetProperty("aud").ToString(), Does.Contain("optio"));

        var body = new ByteArrayContent([.. "GVAS"u8]);
        body.Headers.ContentType = new MediaTypeHeaderValue(Uploads.OctetStream);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            discovery.GetProperty("upload_endpoint").GetString()
        )
        {
            Content = body,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);

        Assert.That(
            response.StatusCode,
            Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync()
        );
        var relayed = _optio.UploadApi.Requests.Single().Authorization!;
        var claims = Payload(relayed["Bearer ".Length..]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(claims.GetProperty("aud").GetString(), Is.EqualTo(ServerResource));
            Assert.That(claims.GetProperty("azp").GetString(), Is.EqualTo("optio"));
            Assert.That(claims.GetProperty("scope").GetString(), Is.EqualTo("custos:write"));
            Assert.That(claims.GetProperty("discord_id").GetString(), Is.EqualTo(DevDiscordId));
        }
    }

    /// <summary>
    /// The login of a browser and the code exchange, with PKCE. Gives the access token.
    /// </summary>
    private async Task<string> LoginAsync(
        HttpClient http,
        JsonElement openid,
        string clientId,
        string scope
    )
    {
        const string redirectUri = "custos://oauth2callback";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var login = await http.GetStringAsync(
            $"{openid.GetProperty("authorization_endpoint").GetString()}?response_type=code"
                + $"&client_id={Uri.EscapeDataString(clientId)}"
                + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
                + $"&scope={Uri.EscapeDataString(scope)}&state=s1"
                + $"&code_challenge={challenge}&code_challenge_method=S256"
        );
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

        var code = System.Web.HttpUtility.ParseQueryString(new Uri(location).Query)["code"]!;
        using var response = await http.PostAsync(
            openid.GetProperty("token_endpoint").GetString(),
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = clientId,
                    ["redirect_uri"] = redirectUri,
                    ["code"] = code,
                    ["code_verifier"] = verifier,
                }
            )
        );
        var token = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(token.TryGetProperty("access_token", out var access), Is.True, $"{token}");
        return access.GetString()!;
    }

    /// <summary>Sets the user attribute <c>discord_id</c> of the user dev.</summary>
    private async Task SetDiscordIdAsync()
    {
        using var http = new HttpClient();
        using var login = await http.PostAsync(
            $"{KeycloakOrigin}/realms/master/protocol/openid-connect/token",
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
        var users = $"{KeycloakOrigin}/admin/realms/{Realm}/users";
        var user = (
            await http.GetFromJsonAsync<JsonElement[]>($"{users}?username=dev&exact=true")
        )!.Single();
        var changed = System.Text.Json.Nodes.JsonNode.Parse(user.GetRawText())!;
        changed["attributes"] = new System.Text.Json.Nodes.JsonObject
        {
            ["discord_id"] = new System.Text.Json.Nodes.JsonArray(DevDiscordId),
        };
        using var update = await http.PutAsJsonAsync(
            $"{users}/{user.GetProperty("id").GetString()}",
            changed
        );
        Assert.That(update.IsSuccessStatusCode, Is.True);
    }

    private static JsonElement Payload(string jwt)
    {
        var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        return JsonDocument
            .Parse(Convert.FromBase64String(part.PadRight((part.Length + 3) / 4 * 4, '=')))
            .RootElement;
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
        return action.StartsWith('/') ? $"{KeycloakOrigin}{action}" : action;
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

    /// <summary>Optio on Kestrel with the real realm, and a fake upload API.</summary>
    private sealed class KestrelFactory(int port, string authority) : WebApplicationFactory<Program>
    {
        public FakeUploadApi UploadApi { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Optio:PublicBaseUrl", $"http://localhost:{port}");
            builder.UseSetting(
                "Optio:CustosClientId",
                $"http://{ContainerHost}:{port}/.well-known/custos-client"
            );
            builder.UseSetting("Optio:Authority", authority);
            builder.UseSetting("Optio:Scopes:0", "openid");
            builder.UseSetting("Optio:Scopes:1", "custos:client");
            builder.UseSetting("Optio:OptioClientSecret", OptioFactory.Secret);
            builder.UseSetting("Optio:UploadApiUrl", "http://server.test/api/v1/uploads");
            builder.ConfigureTestServices(services =>
                services
                    .AddHttpClient<UploadRelay>()
                    .ConfigurePrimaryHttpMessageHandler(() => UploadApi)
            );
        }
    }
}
