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

    private int _serverPort;
    private IContainer _keycloak = null!;
    private KestrelFactory _server = null!;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        _serverPort = FreePort();
        await TestcontainersSettings.ExposeHostPortsAsync((ushort)_serverPort);

        _keycloak = new ContainerBuilder("quay.io/keycloak/keycloak:26.8.0")
            .WithCommand("start-dev", "--features=cimd", "--import-realm")
            .WithResourceMapping(
                new FileInfo(
                    Path.Combine(AppContext.BaseDirectory, "keycloak", "custos-realm.json")
                ),
                "/opt/keycloak/data/import/"
            )
            .WithEnvironment("CUSTOS_CIMD_HOST", ContainerHost)
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath($"/realms/{Realm}"))
            )
            .Build();
        await _keycloak.StartAsync();

        _server = new KestrelFactory(
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
        var openid = await http.GetFromJsonAsync<JsonElement>(
            discovery.GetProperty("openid_configuration").GetString()
        );
        string Member(string name) =>
            (
                discovery.TryGetProperty(name, out var value) ? value : openid.GetProperty(name)
            ).GetString()!;

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        const string redirectUri = "custos://oauth2callback";

        // The browser: the login page, the form, and the consent page of the realm.
        var authorize =
            $"{Member("authorization_endpoint")}?response_type=code"
            + $"&client_id={Uri.EscapeDataString(Member("client_id"))}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope=openid&state=s1"
            + $"&code_challenge={challenge}&code_challenge_method=S256";
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

        // Custos: the code exchange and the upload.
        using var tokenResponse = await http.PostAsync(
            Member("token_endpoint"),
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = Member("client_id"),
                    ["redirect_uri"] = redirectUri,
                    ["code"] = query["code"]!,
                    ["code_verifier"] = verifier,
                }
            )
        );
        var token = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(token.TryGetProperty("refresh_token", out _), Is.True, token.ToString());

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

    private sealed class KestrelFactory(int port, string authority, string clientId)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Development: the realm sets no claim active_guild, so the server uses
            // DevelopmentGuildId.
            builder.UseEnvironment("Development");
            builder.UseSetting("Custos:PublicBaseUrl", $"http://localhost:{port}");
            builder.UseSetting("Custos:Authority", authority);
            builder.UseSetting("Custos:ClientId", clientId);
            builder.UseSetting("Custos:Scopes:0", "openid");
            builder.UseSetting("Custos:UploadMinIntervalSeconds", "0");
            builder.UseSetting("Custos:WarPollIntervalSeconds", "0");
            builder.UseSetting(
                $"ConnectionStrings:{CustosDbContext.ConnectionName}",
                Postgres.NewDatabase()
            );
        }
    }
}
