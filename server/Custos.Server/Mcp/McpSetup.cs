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
/// The MCP endpoint <c>/mcp</c> as an OAuth resource server, with stateless HTTP. Its tokens have
/// the audience <see cref="CustosOptions.Resource"/>, as the tokens of the upload API. The scheme
/// <see cref="Scheme"/> adds the challenge of the MCP authorization specification. The endpoint
/// needs the Discord connection.
/// </summary>
internal static partial class McpSetup
{
    public const string Scheme = "mcp";
    public const string Policy = "mcp";
    public const string RatePolicy = "mcp";

    /// <summary>
    /// The card of the server in the MCP member <c>instructions</c>: the purpose, the records
    /// that the server holds, and the records that it does not hold. A harness with more than one
    /// service gives it to the model, so that the model selects the correct service.
    /// </summary>
    public const string Card =
        "Custos holds the logistics records of Foxhole guilds. It knows the stockpiles, the "
        + "items in each stockpile with their quantities, and the structures that hold the "
        + "stockpiles, such as storage depots and seaports, with their labels. The records "
        + "come from the map data files that players upload, and each record belongs to a war. "
        + "Custos does not hold players, crews, tasks, orders, or plans. It does not know the "
        + "war map, the front lines, or the production of items.";

    public static IHostApplicationBuilder AddCustosMcp(this IHostApplicationBuilder builder)
    {
        builder
            .Services.AddOptions<McpOptions>()
            .BindConfiguration(McpOptions.SectionName)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<McpOptions>, Validator>();

        if (Options(builder.Configuration) is not { } options)
            return builder;
        var (mcp, custos) = options;

        var authority = string.IsNullOrEmpty(mcp.Authority) ? custos.Authority : mcp.Authority;
        var resource = custos.Resource;
        var metadataUrl = McpOptions.MetadataUrl(resource);
        var challenge =
            $"Bearer resource_metadata=\"{metadataUrl}\", "
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
                options.ResourceMetadataUri = new Uri(metadataUrl);
                options.ResourceMetadata = new ProtectedResourceMetadata
                {
                    Resource = resource,
                    AuthorizationServers = [authority],
                    ScopesSupported = [mcp.ReadScope, mcp.WriteScope],
                    BearerMethodsSupported = ["header"],
                    ResourceName = "Custos",
                };
            });

        builder
            .Services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy =>
                policy
                    .AddAuthenticationSchemes(Scheme)
                    .RequireAuthenticatedUser()
                    .RequireClaim(custos.DiscordIdClaim)
            );

        builder
            .Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "custos",
                    Title = "Custos",
                    Version = ServerVersion.Value,
                };
                options.ServerInstructions = Card;
            })
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

    /// <summary>The enabled and valid section with the section <c>Custos</c>, or
    /// <c>null</c>.</summary>
    private static (McpOptions Mcp, CustosOptions Custos)? Options(IConfiguration configuration)
    {
        var mcp = configuration.GetSection(McpOptions.SectionName).Get<McpOptions>()
            ?? new McpOptions();
        var custos = configuration.GetSection(CustosOptions.SectionName).Get<CustosOptions>()
            ?? new CustosOptions();
        return mcp.Enabled && mcp.Problem(custos.Resource) is null ? (mcp, custos) : null;
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

                var metadata = McpOptions.MetadataUrl(
                    services.GetRequiredService<IOptions<CustosOptions>>().Value.Resource
                );
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                http.Response.Headers.WWWAuthenticate =
                    $"Bearer error=\"insufficient_scope\", scope=\"{denied.RequiredScope}\", "
                    + $"resource_metadata=\"{metadata}\"";
                return result;
            };

    private sealed class Validator(IOptions<CustosOptions> custos) : IValidateOptions<McpOptions>
    {
        public ValidateOptionsResult Validate(string? name, McpOptions options) =>
            options.Problem(custos.Value.Resource) is { } problem
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
