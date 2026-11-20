using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Custos.Server.Discord;
using Custos.Server.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Custos.Server.Tests.Infrastructure;

/// <summary>
/// The server with a static authorization server: tokens are signed with a test key, and the
/// Foxhole war API is a fake. Each factory has its own database in the shared PostgreSQL container.
/// The war monitor is off. With <paramref name="discord"/>, the Discord connection is on, with
/// the fake as Discord.
/// </summary>
internal sealed class CustosFactory(
    int uploadMinIntervalSeconds = 0,
    string environment = "Testing",
    FakeDiscordDirectory? discord = null
) : WebApplicationFactory<Program>
{
    public const string PublicBaseUrl = "http://custos.test";
    public const string Issuer = "http://idp.test/realms/custos";
    public const string ClientId = PublicBaseUrl + "/.well-known/custos-client";
    public const int UploadMaxBytes = 16 * 1024;
    public const long GuildId = 1234;
    public const long DevelopmentGuildId = 99;
    public const long DevelopmentDiscordUserId = 199;

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
        builder.UseSetting("Custos:DevelopmentDiscordUserId", DevelopmentDiscordUserId.ToString());
        builder.UseSetting("Custos:WarPollIntervalSeconds", "0");
        builder.UseSetting(
            "Custos:CatalogPath",
            Path.Combine(AppContext.BaseDirectory, "Catalog", "test-catalog.json")
        );
        builder.UseSetting(
            $"ConnectionStrings:{CustosDbContext.ConnectionName}",
            _connectionString
        );

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<WarApiClient>().ConfigurePrimaryHttpMessageHandler(() => WarApi);
            if (discord is not null)
            {
                services.AddSingleton<IDiscordDirectory>(discord);
                services.AddScoped<GuildAccessEvaluator>();
            }

            // The retries stay, without the wait between them.
            services.ConfigureAll<HttpStandardResilienceOptions>(resilience =>
                resilience.Retry.Delay = TimeSpan.Zero
            );

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
                jwt.ConfigurationManager =
                    new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            });
        });
    }

    /// <summary>
    /// A client with a bearer token for the user <c>dev</c>, issued to <paramref name="azp"/>. The
    /// claim <c>active_guild</c> is <paramref name="guild"/>, and the claim <c>discord_id</c> is
    /// <paramref name="discordId"/>. A claim is absent when its value is <c>null</c>.
    /// </summary>
    public HttpClient CreateAuthorizedClient(
        string azp = ClientId,
        string subject = "dev",
        string? guild = "1234",
        string? discordId = null
    )
    {
        List<Claim> claims = [new("sub", subject), new("azp", azp)];
        if (guild is not null)
            claims.Add(new Claim("active_guild", guild));
        if (discordId is not null)
            claims.Add(new Claim("discord_id", discordId));

        var token = new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(
                    SigningKey,
                    SecurityAlgorithms.HmacSha256
                ),
            }
        );

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

/// <summary>
/// The Foxhole war API. It answers with <see cref="War"/>. It fails when <see cref="Fail"/> is set,
/// and for the next <see cref="Failures"/> requests.
/// </summary>
internal sealed class FakeWarApi : HttpMessageHandler
{
    public static readonly CurrentWar War142 = new(
        Guid.Parse("1bb12ec2-e62d-4e77-a218-5c729e5b608e"),
        142,
        new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero)
    );

    public CurrentWar War { get; set; } = War142;
    public bool Fail { get; set; }
    public int Failures { get; set; }
    public int Calls { get; private set; }
    public Uri? LastRequest { get; private set; }

    /// <summary>The map data of each region: the place names, the map items, and the ETag of the
    /// static data.</summary>
    public Dictionary<string, FakeMap> Maps { get; } = [];

    /// <summary>The requests for static map data that got <c>304</c>.</summary>
    public int NotModified { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.StartsWith(WarApiClient.MapsPath, StringComparison.Ordinal))
            return Task.FromResult(MapAnswer(request, path[WarApiClient.MapsPath.Length..]));

        Calls++;
        LastRequest = request.RequestUri;
        var fail = Fail || Failures-- > 0;
        return Task.FromResult(
            fail
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        new
                        {
                            warId = War.WarId,
                            warNumber = War.WarNumber,
                            winner = "NONE",
                            conquestStartTime = War.StartedAt?.ToUnixTimeMilliseconds(),
                        }
                    ),
                }
        );
    }

    private HttpResponseMessage MapAnswer(HttpRequestMessage request, string path)
    {
        if (path is "" or "/")
            return Json(Maps.Keys.ToArray());

        var parts = path.Trim('/').Split('/');
        if (!Maps.TryGetValue(parts[0], out var map))
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        if (parts[1] == "static")
        {
            if (request.Headers.IfNoneMatch.ToString() == map.ETag)
            {
                NotModified++;
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            var answer = Json(
                new
                {
                    mapTextItems = map.Places.Select(p => new
                    {
                        text = p.Text,
                        x = p.X,
                        y = p.Y,
                        mapMarkerType = p.Major ? "Major" : "Minor",
                    }),
                }
            );
            answer.Headers.TryAddWithoutValidation("ETag", map.ETag);
            return answer;
        }

        return Json(
            new
            {
                mapItems = map.Items.Select(i => new
                {
                    teamId = "NONE",
                    iconType = i.IconType,
                    x = i.X,
                    y = i.Y,
                    flags = 0,
                }),
            }
        );
    }

    private static HttpResponseMessage Json<T>(T value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}

/// <summary>The map data of one region in <see cref="FakeWarApi"/>.</summary>
internal sealed record FakeMap(MapText[] Places, MapIcon[] Items, string ETag = "\"1\"");
