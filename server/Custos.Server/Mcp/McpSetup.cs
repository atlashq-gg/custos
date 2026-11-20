using Custos.Server.Discord;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Custos.Server.Mcp;

/// <summary>
/// The MCP endpoint <c>/mcp</c> as an OAuth resource server, with stateless HTTP. The server has
/// two kinds of bearer token: the token of Custos (scheme <c>Bearer</c>, no audience) and the MCP
/// token (scheme <see cref="Scheme"/>, audience <see cref="McpOptions.Resource"/>). The endpoint
/// needs the Discord connection.
/// </summary>
internal static partial class McpSetup
{
    public const string Scheme = "mcp";
    public const string Policy = "mcp";
    public const string RatePolicy = "mcp";

    public static IHostApplicationBuilder AddCustosMcp(this IHostApplicationBuilder builder)
    {
        builder
            .Services.AddOptions<McpOptions>()
            .BindConfiguration(McpOptions.SectionName)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<McpOptions>, Validator>();

        var mcp = Options(builder.Configuration);
        if (mcp is null)
            return builder;

        var authority = string.IsNullOrEmpty(mcp.Authority)
            ? builder.Configuration[
                $"{CustosOptions.SectionName}:{nameof(CustosOptions.Authority)}"
            ]!
            : mcp.Authority;
        var resource = mcp.Resource!;
        var challenge =
            $"Bearer resource_metadata=\"{mcp.MetadataUrl}\", "
            + $"scope=\"{mcp.ReadScope} {mcp.WriteScope}\"";

        builder
            .Services.AddAuthentication()
            .AddJwtBearer(Scheme, jwt =>
            {
                jwt.Authority = authority;
                jwt.RequireHttpsMetadata = new Uri(authority).Scheme == Uri.UriSchemeHttps;
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = authority,
                    ValidateIssuer = true,
                    // RFC 8707: the token is for this endpoint only. Some authorization
                    // servers write the resource with a slash at the end.
                    ValidAudiences = [resource, resource + "/"],
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    NameClaimType = "sub",
                };
                // The challenge names the metadata document and the scopes, as the MCP
                // authorization specification of 2026-07-28 asks.
                jwt.Events = new JwtBearerEvents
                {
                    OnChallenge = context =>
                    {
                        context.HandleResponse();
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.Headers.WWWAuthenticate = context.AuthenticateFailure
                            is null
                            ? challenge
                            : challenge + ", error=\"invalid_token\"";
                        return Task.CompletedTask;
                    },
                };
            })
            // The handler of the SDK serves the metadata document (RFC 9728).
            .AddMcp(options =>
            {
                options.ResourceMetadataUri = new Uri(mcp.MetadataUrl);
                options.ResourceMetadata = new ProtectedResourceMetadata
                {
                    Resource = resource,
                    AuthorizationServers = [authority],
                    ScopesSupported = [mcp.ReadScope, mcp.WriteScope],
                    BearerMethodsSupported = ["header"],
                    ResourceName = "Custos",
                };
            });

        var claim = builder.Configuration[
            $"{CustosOptions.SectionName}:{nameof(CustosOptions.DiscordIdClaim)}"
        ]
            ?? new CustosOptions().DiscordIdClaim;
        builder
            .Services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy =>
                policy
                    .AddAuthenticationSchemes(Scheme)
                    .RequireAuthenticatedUser()
                    .RequireClaim(claim)
            );

        builder
            .Services.AddMcpServer(options =>
                options.ServerInfo = new Implementation
                {
                    Name = "custos",
                    Version = ServerVersion.Value,
                }
            )
            // Stateless: one HTTP request is one tool call, and a scoped service lives for one
            // call.
            .WithHttpTransport(http => http.Stateless = true)
            .WithToolsFromAssembly(typeof(McpSetup).Assembly)
            .WithRequestFilters(filters =>
            {
                // The first call filter is the outer one. The challenge reads the refusal of the
                // guard after the guard ran.
                filters.AddCallToolFilter(ScopeChallenge);
                filters.AddCallToolFilter(McpAccessFilter.CallTool);
                filters.AddListToolsFilter(McpAccessFilter.ListTools);
            });
        builder.Services.AddScoped<McpCallContext>();
        builder.Services.AddHttpContextAccessor();
        return builder;
    }

    /// <summary>
    /// Maps <c>/mcp</c> when the section is enabled and the Discord connection is on. The metadata
    /// document needs no route: the authentication handler of the SDK serves it.
    /// </summary>
    public static WebApplication MapCustosMcp(this WebApplication app)
    {
        if (Options(app.Configuration) is null)
            return app;
        if (
            !app
                .Services.GetRequiredService<IServiceProviderIsService>()
                .IsService(typeof(GuildAccessEvaluator))
        )
        {
            LogMcpWithoutDiscord(app.Logger);
            return app;
        }

        app.MapMcp(McpOptions.Path).RequireAuthorization(Policy).RequireRateLimiting(RatePolicy);
        // RFC 9728 lets a client derive the URL from the resource: the well-known prefix before
        // the path of the resource.
        app.MapGet(McpOptions.MetadataPath + McpOptions.Path, () =>
            Results.Redirect(McpOptions.MetadataPath, permanent: true, preserveMethod: true)
        );
        return app;
    }

    /// <summary>The enabled and valid section, or <c>null</c>.</summary>
    private static McpOptions? Options(IConfiguration configuration)
    {
        var options = configuration.GetSection(McpOptions.SectionName).Get<McpOptions>()
            ?? new McpOptions();
        return options.Enabled && options.Problem() is null ? options : null;
    }

    /// <summary>
    /// The outer call filter. A missing scope is a token problem: when the response did not start
    /// yet, it becomes an HTTP <c>403</c> with the challenge <c>insufficient_scope</c>. A harness
    /// then asks the user to log in again and grant the scope.
    /// </summary>
    private static McpRequestFilter<CallToolRequestParams, CallToolResult> ScopeChallenge { get; } =
        next =>
            async (request, cancellationToken) =>
            {
                var result = await next(request, cancellationToken);
                if (
                    request.Services is not { } services
                    || services.GetService<McpCallContext>()?.Denied
                        is not { Type: McpProblem.InsufficientScopeType } denied
                    || services.GetService<IHttpContextAccessor>()?.HttpContext
                        is not { Response.HasStarted: false } http
                )
                {
                    return result;
                }

                var metadata = services
                    .GetRequiredService<IOptions<McpOptions>>()
                    .Value.MetadataUrl;
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                http.Response.Headers.WWWAuthenticate =
                    $"Bearer error=\"insufficient_scope\", scope=\"{denied.RequiredScope}\", "
                    + $"resource_metadata=\"{metadata}\"";
                return result;
            };

    private sealed class Validator : IValidateOptions<McpOptions>
    {
        public ValidateOptionsResult Validate(string? name, McpOptions options) =>
            options.Problem() is { } problem
                ? ValidateOptionsResult.Fail(problem)
                : ValidateOptionsResult.Success;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mcp:Enabled is true, but the Discord connection is off. The server maps no "
            + "MCP endpoint."
    )]
    private static partial void LogMcpWithoutDiscord(ILogger logger);
}
