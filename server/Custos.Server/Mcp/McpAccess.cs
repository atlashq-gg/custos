using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Custos.Server.Discord;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Custos.Server.Mcp;

public enum McpScope
{
    Read,
    Write,
}

/// <summary>
/// The guard of an MCP tool: the scope of the token, and the access of the caller in the guild of
/// the argument <see cref="GuildArgument"/>. With <see cref="GuildAccess.None"/>, the guard
/// examines the token only. The server refuses each call of a tool without a guard.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class McpGuardAttribute(McpScope scope, GuildAccess access) : Attribute
{
    public McpScope Scope => scope;
    public GuildAccess Access => access;
    public string GuildArgument { get; init; } = "guild_id";
}

/// <summary>What the guard found for one call. Scoped: one HTTP request is one tool
/// call.</summary>
public sealed class McpCallContext
{
    public ulong? CallerId { get; set; }
    public ulong? GuildId { get; set; }
    public AccessCheck? Check { get; set; }
    public McpProblem? Denied { get; set; }
}

/// <summary>
/// The request filters of the SDK that apply the guards. The call filter runs the guard before
/// the tool. The list filter hides each tool whose scope the token does not have.
/// </summary>
internal static class McpAccessFilter
{
    private const string ScopeClaim = "scope";

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallTool { get; } =
        next =>
            async (request, cancellationToken) =>
            {
                // An unknown tool goes on to the SDK, which reports it.
                if (request.MatchedPrimitive is not { } tool)
                    return await next(request, cancellationToken);

                var services = request.Services
                    ?? throw new InvalidOperationException("The MCP request has no services.");
                var guard = tool.Metadata.OfType<McpGuardAttribute>().LastOrDefault();
                var context = services.GetRequiredService<McpCallContext>();
                context.Denied = await EvaluateAsync(
                    guard,
                    request.User,
                    request.Params?.Arguments,
                    services,
                    context,
                    cancellationToken
                );
                if (context.Denied is null)
                    return await next(request, cancellationToken);

                McpTools.Record(services, request.Params?.Name ?? "", context, "denied", default);
                return McpTools.Problem(context.Denied);
            };

    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> ListTools { get; } =
        next =>
            async (request, cancellationToken) =>
            {
                var result = await next(request, cancellationToken);
                var services = request.Services ?? request.Server.Services!;
                var options = services.GetRequiredService<IOptions<McpOptions>>().Value;
                var tools = request.Server.ServerOptions.ToolCollection;
                result.Tools =
                [
                    .. result.Tools.Where(t =>
                        tools is not null
                        && tools.TryGetPrimitive(t.Name, out var tool)
                        && tool.Metadata.OfType<McpGuardAttribute>().LastOrDefault() is { } guard
                        && HasScope(request.User, ScopeName(guard.Scope, options))
                    ),
                ];
                return result;
            };

    /// <summary>The refusal of a call, or <c>null</c>. The cheap checks run first.</summary>
    internal static async Task<McpProblem?> EvaluateAsync(
        McpGuardAttribute? guard,
        ClaimsPrincipal? user,
        IDictionary<string, JsonElement>? arguments,
        IServiceProvider services,
        McpCallContext context,
        CancellationToken cancellationToken
    )
    {
        if (guard is null)
            return McpProblem.Forbidden("This tool has no guard.");

        var options = services.GetRequiredService<IOptions<McpOptions>>().Value;
        var scope = ScopeName(guard.Scope, options);
        if (!HasScope(user, scope))
            return McpProblem.InsufficientScope(scope);

        var claim = services.GetRequiredService<IOptions<CustosOptions>>().Value.DiscordIdClaim;
        if (
            !ulong.TryParse(
                user?.FindFirst(claim)?.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var callerId
            )
        )
        {
            return McpProblem.Forbidden($"The token has no valid claim {claim}.");
        }
        context.CallerId = callerId;
        if (guard.Access == GuildAccess.None)
            return null;

        if (
            arguments is null
            || !arguments.TryGetValue(guard.GuildArgument, out var value)
            || value.ValueKind != JsonValueKind.String
            || !ulong.TryParse(
                value.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var guildId
            )
        )
        {
            return McpProblem.NotVisible();
        }

        AccessCheck check;
        try
        {
            check = await services
                .GetRequiredService<GuildAccessEvaluator>()
                .EvaluateAsync(guildId, callerId, cancellationToken);
        }
        catch (DiscordUnavailableException)
        {
            return McpProblem.DiscordUnavailable();
        }

        context.GuildId = guildId;
        context.Check = check;
        if (check.Status != MemberStatus.Member)
            return McpProblem.NotVisible();
        return check.Has(guard.Access)
            ? null
            : McpProblem.Forbidden(
                $"This tool needs the access {guard.Access.ToString().ToLowerInvariant()} "
                    + "in the guild."
            );
    }

    public static string ScopeName(McpScope scope, McpOptions options) =>
        scope == McpScope.Write ? options.WriteScope : options.ReadScope;

    public static bool HasScope(ClaimsPrincipal? user, string scope) =>
        user is not null
        && user.FindAll(ScopeClaim)
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
}
