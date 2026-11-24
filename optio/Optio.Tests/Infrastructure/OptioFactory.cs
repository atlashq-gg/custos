using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Optio.Discord;
using Optio.Endpoints;
using Optio.Events;
using Optio.Harness;
using Optio.Storage;
using Optio.Streams;
using Optio.Tokens;

namespace Optio.Tests.Infrastructure;

/// <summary>
/// optio in the test host, with a fake authorization server for the token exchange and a fake
/// upload API of the server. The tokens of Custos are signed with a test key. With
/// <paramref name="stream"/>, the stream is on, with <see cref="Members"/> as Discord and
/// <see cref="Time"/> as the clock. With <paramref name="broker"/>, optio reads the events from
/// that broker. Without it, a test gives the events to the hub itself.
/// </summary>
/// With <paramref name="query"/>, the stream, a database, and the query are on, with
/// <see cref="Model"/> and <see cref="Tools"/>.
internal sealed class OptioFactory(bool stream = false, string? broker = null, bool query = false)
    : WebApplicationFactory<Program>
{
    public const string SystemPrompt = "Answer the questions of the guild.";

    public const string PublicBaseUrl = "http://optio.test";
    public const string Issuer = "http://idp.test/realms/custos";
    public const string TokenEndpoint = Issuer + "/protocol/openid-connect/token";
    public const string ClientId = PublicBaseUrl + "/.well-known/custos-client";
    public const string Secret = "optio-secret";
    public const string UploadApiUrl = "http://server.test/api/v1/uploads";
    public const int UploadMaxBytes = 16 * 1024;

    private static readonly SymmetricSecurityKey SigningKey = new(new byte[32]) { KeyId = "test" };

    public FakeAuthorizationServer AuthorizationServer { get; } = new();

    public FakeUploadApi UploadApi { get; } = new();

    public FakeMembers Members { get; } = new();

    public FakeModel Model { get; } = new();

    public FakeTools Tools { get; } = new();

    /// <summary>The handler of the MCP services, for a test of the real tool source. Set it
    /// before the first use of the factory.</summary>
    public HttpMessageHandler? McpServer { get; set; }

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    public StreamHub Hub => Services.GetRequiredService<StreamHub>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Optio:PublicBaseUrl", PublicBaseUrl);
        builder.UseSetting("Optio:Authority", Issuer);
        builder.UseSetting("Optio:Scopes:0", "openid");
        builder.UseSetting("Optio:Scopes:1", "custos:client");
        builder.UseSetting("Optio:OptioClientSecret", Secret);
        builder.UseSetting("Optio:UploadApiUrl", UploadApiUrl);
        builder.UseSetting("Optio:UploadMaxBytes", UploadMaxBytes.ToString());
        stream |= query;
        if (query)
        {
            builder.UseSetting("ConnectionStrings:optio", Postgres.NewDatabase());
            builder.UseSetting("Optio:ModelApiKey", "test");
            builder.UseSetting("Optio:SystemPrompt", SystemPrompt);
            builder.UseSetting("Optio:McpServices:0:Name", "server");
            builder.UseSetting("Optio:McpServices:0:Url", "http://server.test/mcp");
            builder.UseSetting("Optio:McpServices:0:ReadScope", "custos:read");
        }
        if (stream)
        {
            // Nothing listens on port 1.
            builder.UseSetting(
                "ConnectionStrings:events",
                broker ?? "amqp://optio:optio@127.0.0.1:1"
            );
            builder.UseSetting("Optio:Sources:0", "custos");
            builder.UseSetting("Optio:DiscordToken", "test");
        }

        builder.ConfigureTestServices(services =>
        {
            services
                .AddHttpClient<TokenExchange>()
                .ConfigurePrimaryHttpMessageHandler(() => AuthorizationServer);
            services
                .AddHttpClient<UploadRelay>()
                .ConfigurePrimaryHttpMessageHandler(() => UploadApi);
            services.ConfigureAll<HttpStandardResilienceOptions>(resilience =>
                resilience.Retry.Delay = TimeSpan.Zero
            );
            if (stream)
            {
                services.AddSingleton<IMemberDirectory>(Members);
                services.AddSingleton<TimeProvider>(Time);
                // No Discord gateway: the fake directory replaces it.
                services.RemoveAll<IHostedService>();
                if (query)
                    services.AddHostedService<DatabaseMigrator>();
                if (broker is not null)
                    services.AddHostedService<EventConsumer>();
                services.AddSingleton<IModelClient>(Model);
                services.AddScoped<IToolSource>(_ => Tools);
                if (McpServer is not null)
                {
                    services
                        .AddHttpClient(McpToolSource.HttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => McpServer);
                }
            }
            services.PostConfigureAll<JwtBearerOptions>(jwt =>
            {
                var metadata = new OpenIdConnectConfiguration
                {
                    Issuer = Issuer,
                    TokenEndpoint = TokenEndpoint,
                };
                metadata.SigningKeys.Add(SigningKey);
                jwt.Configuration = metadata;
                jwt.ConfigurationManager =
                    new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            });
        });
    }

    /// <summary>
    /// A client with a token of Custos for the user <paramref name="subject"/>: the audience
    /// <paramref name="audience"/> and the client <paramref name="azp"/>.
    /// </summary>
    public HttpClient CreateCustosClient(
        string subject = "dev",
        string azp = ClientId,
        string audience = "optio",
        params Claim[] claims
    )
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CustosToken(subject, azp, audience, claims)
        );
        return client;
    }

    /// <summary>A client of a member of <see cref="FakeMembers.Guild"/> with a token of
    /// Custos.</summary>
    public HttpClient CreateMemberClient(ulong user = FakeMembers.Reader) =>
        CreateCustosClient(
            claims:
            [
                new Claim("active_guild", FakeMembers.Guild.ToString()),
                new Claim("discord_id", user.ToString()),
            ]
        );

    public static string CustosToken(
        string subject,
        string azp = ClientId,
        string aud = "optio",
        params Claim[] claims
    ) =>
        new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = aud,
                Subject = new ClaimsIdentity([
                    new Claim("sub", subject),
                    new Claim("azp", azp),
                    .. claims,
                ]),
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(
                    SigningKey,
                    SecurityAlgorithms.HmacSha256
                ),
            }
        );

    public static StringContent OctetStream(string body)
    {
        var content = new StringContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(Uploads.OctetStream);
        return content;
    }
}

/// <summary>
/// The token endpoint. It answers each exchange with <c>exchanged-{n}</c>, or with <see
/// cref="Status"/> when it is not 200.
/// </summary>
internal sealed class FakeAuthorizationServer : HttpMessageHandler
{
    public List<(string? Authorization, Dictionary<string, string> Form)> Requests { get; } = [];

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var form = HttpUtility.ParseQueryString(
            await request.Content!.ReadAsStringAsync(cancellationToken)
        );
        lock (Requests)
            Requests.Add(
                (
                    request.Headers.Authorization?.ToString(),
                    form.AllKeys.ToDictionary(k => k!, k => form[k]!)
                )
            );
        return Status != HttpStatusCode.OK
            ? new HttpResponseMessage(Status)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"access_token\":\"exchanged-{Requests.Count}\",\"expires_in\":300}}",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
    }
}

/// <summary>
/// The upload API of the server. It keeps each request and answers with <see cref="Respond"/>.
/// </summary>
internal sealed class FakeUploadApi : HttpMessageHandler
{
    public List<(string? Authorization, string? ContentType, byte[] Body)> Requests { get; } = [];

    public Func<HttpResponseMessage> Respond { get; set; } =
        () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"status\":\"accepted\"}",
                    Encoding.UTF8,
                    "application/json"
                ),
            };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
        Requests.Add(
            (
                request.Headers.Authorization?.ToString(),
                request.Content.Headers.ContentType?.ToString(),
                body
            )
        );
        return Respond();
    }
}
