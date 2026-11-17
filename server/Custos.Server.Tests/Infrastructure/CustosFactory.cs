using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Custos.Server.Tests.Infrastructure;

/// <summary>
/// The server with a static authorization server: tokens are signed with a test key, and the Foxhole war API
/// is a fake.
/// </summary>
internal sealed class CustosFactory(int uploadMinIntervalSeconds = 0) : WebApplicationFactory<Program>
{
    public const string PublicBaseUrl = "http://custos.test";
    public const string Issuer = "http://idp.test/realms/custos";
    public const string ClientId = PublicBaseUrl + "/custos/client-metadata.json";
    public const int UploadMaxBytes = 1024;

    private static readonly SymmetricSecurityKey SigningKey = new(new byte[32]) { KeyId = "test" };

    public FakeWarApi WarApi { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Custos:PublicBaseUrl", PublicBaseUrl);
        builder.UseSetting("Custos:Authority", Issuer);
        builder.UseSetting("Custos:Scopes:0", "openid");
        builder.UseSetting("Custos:UploadMaxBytes", UploadMaxBytes.ToString());
        builder.UseSetting("Custos:UploadMinIntervalSeconds", uploadMinIntervalSeconds.ToString());

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<WarApiClient>().ConfigurePrimaryHttpMessageHandler(() => WarApi);

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
            {
                var metadata = new OpenIdConnectConfiguration
                {
                    Issuer = Issuer,
                    AuthorizationEndpoint = Issuer + "/protocol/openid-connect/auth",
                    TokenEndpoint = Issuer + "/protocol/openid-connect/token",
                };
                metadata.SigningKeys.Add(SigningKey);

                jwt.Configuration = metadata;
                jwt.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            });
        });
    }

    /// <summary>A client with a bearer token for the user <c>dev</c>, issued to <paramref name="azp"/>.</summary>
    public HttpClient CreateAuthorizedClient(string azp = ClientId, string subject = "dev")
    {
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Subject = new ClaimsIdentity([new Claim("sub", subject), new Claim("azp", azp)]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

/// <summary>The Foxhole war API. It answers with war 142, or fails when <see cref="Fail"/> is set.</summary>
internal sealed class FakeWarApi : HttpMessageHandler
{
    public bool Fail { get; set; }
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Fail
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{ "warId": "1bb12ec2-e62d-4e77-a218-5c729e5b608e", "warNumber": 142, "winner": "NONE" }""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
