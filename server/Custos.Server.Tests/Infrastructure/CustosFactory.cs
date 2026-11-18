using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Custos.Server.Storage;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Custos.Server.Tests.Infrastructure;

/// <summary>
/// The server with a static authorization server: tokens are signed with a test key, and the Foxhole war API
/// is a fake. Each factory has its own database in the shared PostgreSQL container. The war monitor is off.
/// </summary>
internal sealed class CustosFactory(int uploadMinIntervalSeconds = 0, string environment = "Testing") : WebApplicationFactory<Program>
{
    public const string PublicBaseUrl = "http://custos.test";
    public const string Issuer = "http://idp.test/realms/custos";
    public const string ClientId = PublicBaseUrl + "/custos/client-metadata.json";
    public const int UploadMaxBytes = 16 * 1024;
    public const long GuildId = 1234;
    public const long DevelopmentGuildId = 99;

    private static readonly SymmetricSecurityKey SigningKey = new(new byte[32]) { KeyId = "test" };

    private readonly string _connectionString = Postgres.NewDatabase();

    public FakeWarApi WarApi { get; } = new();

    /// <summary>A database context outside a request, to check what the server stored.</summary>
    public CustosDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<CustosDbContext>().UseNpgsql(_connectionString);
        CustosDbContext.Configure(options);
        return new CustosDbContext(options.Options);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("Custos:PublicBaseUrl", PublicBaseUrl);
        builder.UseSetting("Custos:Authority", Issuer);
        builder.UseSetting("Custos:Scopes:0", "openid");
        builder.UseSetting("Custos:UploadMaxBytes", UploadMaxBytes.ToString());
        builder.UseSetting("Custos:UploadMinIntervalSeconds", uploadMinIntervalSeconds.ToString());
        builder.UseSetting("Custos:DevelopmentGuildId", DevelopmentGuildId.ToString());
        builder.UseSetting("Custos:WarPollIntervalSeconds", "0");
        builder.UseSetting($"ConnectionStrings:{CustosDbContext.ConnectionName}", _connectionString);

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

    /// <summary>
    /// A client with a bearer token for the user <c>dev</c>, issued to <paramref name="azp"/>. The claim
    /// <c>active_guild</c> is <paramref name="guild"/>, or absent when it is <c>null</c>.
    /// </summary>
    public HttpClient CreateAuthorizedClient(string azp = ClientId, string subject = "dev", string? guild = "1234")
    {
        List<Claim> claims = [new("sub", subject), new("azp", azp)];
        if (guild is not null) claims.Add(new Claim("active_guild", guild));

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

/// <summary>The Foxhole war API. It answers with <see cref="War"/>, or fails when <see cref="Fail"/> is set.</summary>
internal sealed class FakeWarApi : HttpMessageHandler
{
    public static readonly CurrentWar War142 = new(
        Guid.Parse("1bb12ec2-e62d-4e77-a218-5c729e5b608e"), 142, new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    public CurrentWar War { get; set; } = War142;
    public bool Fail { get; set; }
    public int Calls { get; private set; }
    public Uri? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        LastRequest = request.RequestUri;
        return Task.FromResult(Fail
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{ "warId": "{{War.WarId}}", "warNumber": {{War.WarNumber}}, "winner": "NONE", "conquestStartTime": {{War.StartedAt?.ToUnixTimeMilliseconds().ToString() ?? "null"}} }""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
