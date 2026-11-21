using System.Net;
using System.Text.Json;
using Custos.Server.Discord;
using Custos.Server.Mcp;
using Custos.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using static Custos.Server.Tests.Mcp.McpClients;

namespace Custos.Server.Tests.Mcp;

public sealed class McpEndpointTests
{
    private const ulong Guild = 300000000000000001;
    private const ulong OtherGuild = 300000000000000002;
    private const ulong NoMemberGuild = 300000000000000003;
    private const ulong User = 400000000000000001;
    private const ulong ReadRole = 100000000000000002;
    private const string Metadata = "/.well-known/oauth-protected-resource";

    private FakeDiscordDirectory _discord = null!;
    private CustosFactory _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _discord = new FakeDiscordDirectory()
            .Add(Guild, User, roleIds: ReadRole)
            .Add(OtherGuild, User, administrator: true);
        _discord.Guilds.Add(NoMemberGuild);
        _factory = new CustosFactory(discord: _discord, mcp: true);
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Request_without_a_token_gets_the_challenge_with_the_metadata()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync("/mcp", Json("{}"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(
                response.Headers.WwwAuthenticate.ToString(),
                Is.EqualTo(
                    $"Bearer resource_metadata=\"http://localhost{Metadata}\", "
                        + "scope=\"custos:read custos:write\""
                )
            );
        }
    }

    [Test]
    public async Task Token_of_custos_has_no_audience_and_is_refused()
    {
        using var client = _factory.CreateAuthorizedClient(discordId: User.ToString());

        using var response = await client.PostAsync("/mcp", Json("{}"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(
                response.Headers.WwwAuthenticate.ToString(),
                Does.Contain("error=\"invalid_token\"")
            );
        }
    }

    [Test]
    public async Task Mcp_token_is_refused_by_the_upload()
    {
        using var client = _factory.CreateMcpClient(User.ToString());

        using var response = await client.PostAsync(
            UploadRequests.UploadPath,
            UploadRequests.Body(UploadRequests.SaveFile)
        );

        Assert.That(
            response.StatusCode,
            Is.AnyOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden)
        );
    }

    [Test]
    public async Task Token_without_the_discord_claim_is_403()
    {
        using var client = _factory.CreateMcpClient(discordId: null);

        using var response = await client.PostAsync("/mcp", Json("{}"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Metadata_document_names_the_resource_the_issuer_and_the_scopes()
    {
        using var client = _factory.CreateClient();

        var document = JsonDocument.Parse(await client.GetStringAsync(Metadata)).RootElement;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                document.GetProperty("resource").GetString(),
                Is.EqualTo(CustosFactory.McpResource)
            );
            Assert.That(
                document.GetProperty("authorization_servers")[0].GetString(),
                Is.EqualTo(CustosFactory.Issuer)
            );
            Assert.That(
                document
                    .GetProperty("scopes_supported")
                    .EnumerateArray()
                    .Select(s => s.GetString()),
                Is.EqualTo(new[] { "custos:read", "custos:write" })
            );
        }
    }

    [Test]
    public async Task Metadata_path_with_the_suffix_redirects_to_the_document()
    {
        using var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            }
        );

        using var response = await client.GetAsync(Metadata + "/mcp");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.PermanentRedirect));
            Assert.That(response.Headers.Location?.ToString(), Is.EqualTo(Metadata));
        }
    }

    [Test]
    public async Task List_guilds_gives_each_guild_where_the_caller_is_a_member()
    {
        await SaveSharingAsync(OtherGuild, Guild);
        await SaveReadRoleAsync();
        using var http = _factory.CreateMcpClient(User.ToString(), "custos:read");
        await using var client = await ConnectAsync(http);

        var result = await client.CallToolAsync("list_guilds", new Dictionary<string, object?>());

        Assert.That(result.IsError, Is.Not.True, Text(result));
        var json = JsonDocument.Parse(Text(result)).RootElement;
        var items = json.GetProperty("items").EnumerateArray().ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json.GetProperty("total").GetInt32(), Is.EqualTo(2));
            Assert.That(
                items.Select(i => i.GetProperty("guild_id").GetString()),
                Is.EqualTo(new[] { Guild.ToString(), OtherGuild.ToString() })
            );
            Assert.That(
                items[0].GetProperty("access").EnumerateArray().Select(a => a.GetString()),
                Is.EqualTo(new[] { "read", "report" })
            );
            Assert.That(
                items[1].GetProperty("access").EnumerateArray().Select(a => a.GetString()),
                Is.EqualTo(new[] { "admin", "label", "read", "report" })
            );
            Assert.That(
                items[0].GetProperty("shared_by")[0].GetProperty("guild_id").GetString(),
                Is.EqualTo(OtherGuild.ToString())
            );
            Assert.That(items[0].TryGetProperty("current_war", out _), Is.False);
        }
    }

    [Test]
    public async Task Token_without_the_read_scope_sees_no_tool_and_gets_the_scope_challenge()
    {
        using var http = _factory.CreateMcpClient(User.ToString(), "openid");
        await using var client = await ConnectAsync(http);

        var tools = await client.ListToolsAsync();

        Assert.That(tools, Is.Empty);
    }

    [Test]
    public async Task Call_without_the_scope_is_an_http_403_with_insufficient_scope()
    {
        using var http = _factory.CreateMcpClient(User.ToString(), "openid");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """
                {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_guilds",
                "arguments":{},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28",
                "io.modelcontextprotocol/clientCapabilities":{}}}}
                """,
                System.Text.Encoding.UTF8,
                "application/json"
            ),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "tools/call");
        request.Headers.Add("Mcp-Name", "list_guilds");

        using var response = await http.SendAsync(request);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                response.StatusCode,
                Is.EqualTo(HttpStatusCode.Forbidden),
                await response.Content.ReadAsStringAsync()
            );
            Assert.That(
                response.Headers.WwwAuthenticate.ToString(),
                Does.Contain("error=\"insufficient_scope\", scope=\"custos:read\"")
            );
            Assert.That(
                await response.Content.ReadAsStringAsync(),
                Does.Contain(McpProblem.InsufficientScopeType)
            );
        }
    }

    [TestCase(Guild, GuildAccess.Read, null)]
    [TestCase(Guild, GuildAccess.Label, McpProblem.ForbiddenType)]
    [TestCase(NoMemberGuild, GuildAccess.Read, McpProblem.NotVisibleType)]
    [TestCase(999999999999999999UL, GuildAccess.Read, McpProblem.NotVisibleType)]
    public async Task Guard_examines_the_access_in_the_guild_of_the_argument(
        ulong guildId,
        GuildAccess access,
        string? refusal
    )
    {
        await SaveReadRoleAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var problem = await EvaluateAsync(scope.ServiceProvider, guildId.ToString(), access);

        Assert.That(problem?.Type, Is.EqualTo(refusal));
    }

    [Test]
    public async Task Guard_without_a_guild_argument_and_discord_outage_are_refusals()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var missing = await EvaluateAsync(scope.ServiceProvider, null, GuildAccess.Read);
        _discord.Unavailable = true;
        var outage = await EvaluateAsync(scope.ServiceProvider, Guild.ToString(), GuildAccess.Read);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(missing?.Type, Is.EqualTo(McpProblem.NotVisibleType));
            Assert.That(outage?.Type, Is.EqualTo(McpProblem.DiscordUnavailableType));
        }
    }

    [Test]
    public async Task Tool_without_a_guard_is_refused()
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        var problem = await McpAccessFilter.EvaluateAsync(
            null,
            Principal("custos:read"),
            null,
            scope.ServiceProvider,
            new McpCallContext(),
            default
        );

        Assert.That(problem?.Type, Is.EqualTo(McpProblem.ForbiddenType));
    }

    [Test]
    public async Task Each_caller_has_a_rate_limit()
    {
        using var limited = _factory.WithWebHostBuilder(b =>
            b.UseSetting("Mcp:RequestsPerMinute", "1")
        );
        using var http = limited.CreateClient();
        http.DefaultRequestHeaders.Authorization = _factory
            .CreateMcpClient(User.ToString())
            .DefaultRequestHeaders.Authorization;

        using var first = await http.PostAsync("/mcp", Json("{}"));
        using var second = await http.PostAsync("/mcp", Json("{}"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.StatusCode, Is.Not.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        }
    }

    [Test]
    public async Task Tool_call_is_measured_with_the_tool_the_outcome_and_the_access()
    {
        var meter = _factory.Services.GetRequiredService<System.Diagnostics.Metrics.Meter>();
        using var calls =
            new Microsoft.Extensions.Diagnostics.Metrics.Testing.MetricCollector<long>(
                meter,
                Custos.Server.Telemetry.ServerMetrics.McpToolCalls
            );
        using var http = _factory.CreateMcpClient(User.ToString());
        await using var client = await ConnectAsync(http);

        await client.CallToolAsync("list_guilds", new Dictionary<string, object?>());

        var tags = calls.GetMeasurementSnapshot().Single().Tags;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tags["tool"], Is.EqualTo("list_guilds"));
            Assert.That(tags["outcome"], Is.EqualTo("ok"));
            Assert.That(tags["access"], Is.EqualTo("none"));
        }
    }

    [Test]
    public async Task Without_discord_the_endpoint_is_not_mapped()
    {
        using var factory = new CustosFactory(mcp: true);
        using var client = factory.CreateMcpClient(User.ToString());

        using var response = await client.PostAsync("/mcp", Json("{}"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    private static StringContent Json(string body) =>
        new(body, System.Text.Encoding.UTF8, "application/json");

    private static Task<McpProblem?> EvaluateAsync(
        IServiceProvider services,
        string? guildId,
        GuildAccess access
    ) =>
        McpAccessFilter.EvaluateAsync(
            new McpGuardAttribute(McpScope.Read, access),
            Principal("custos:read"),
            guildId is null
                ? new Dictionary<string, JsonElement>()
                : new Dictionary<string, JsonElement>
                {
                    ["guild_id"] = JsonSerializer.SerializeToElement(guildId),
                },
            services,
            new McpCallContext(),
            default
        );

    private static System.Security.Claims.ClaimsPrincipal Principal(string scopes) =>
        new(
            new System.Security.Claims.ClaimsIdentity(
                [new("scope", scopes), new("discord_id", User.ToString())],
                "test"
            )
        );

    private async Task SaveReadRoleAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<GuildSettingsStore>()
            .SaveAsync(
                Guild,
                GuildSettings.Default with
                {
                    Access = new AccessSettings { ReadRoleIds = [ReadRole] },
                },
                User,
                "test",
                default
            );
    }

    private async Task SaveSharingAsync(ulong owner, ulong reader)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<GuildSettingsStore>()
            .SaveAsync(
                owner,
                GuildSettings.Default with
                {
                    Sharing = new SharingSettings { ReadGuildIds = [reader] },
                },
                User,
                "test",
                default
            );
    }
}
