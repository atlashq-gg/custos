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
using Microsoft.Extensions.Http.Resilience;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Optio.Endpoints;
using Optio.Tokens;

namespace Optio.Tests.Infrastructure;

/// <summary>
/// optio in the test host, with a fake authorization server for the token exchange and a fake
/// upload API of the server. The tokens of Custos are signed with a test key.
/// </summary>
internal sealed class OptioFactory : WebApplicationFactory<Program>
{
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
        string audience = "optio"
    )
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CustosToken(subject, azp, audience)
        );
        return client;
    }

    public static string CustosToken(string subject, string azp = ClientId, string aud = "optio") =>
        new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = aud,
                Subject = new ClaimsIdentity([new Claim("sub", subject), new Claim("azp", azp)]),
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
