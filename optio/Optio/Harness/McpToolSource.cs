using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Optio.Telemetry;
using Optio.Tokens;

namespace Optio.Harness;

/// <summary>The user of the tools: the access token of Custos, the guild, and the user.</summary>
internal sealed record ToolUser(string AccessToken, ulong Guild, ulong User);

/// <summary>A call of a write tool of a service. It waits for the confirmation of the user. Its
/// arguments are the arguments for the service. <paramref name="Shown"/> holds the names of the
/// arguments that the user sees in the confirmation: those whose names do not end in
/// <c>_id</c>.</summary>
internal sealed record WriteCall(
    string Service,
    string Title,
    ToolCall Call,
    IReadOnlyList<string> Shown
);

/// <summary>The tools of one query.</summary>
internal interface IToolSession : IAsyncDisposable
{
    IReadOnlyList<ModelTool> Tools { get; }

    /// <summary>Calls a read tool.</summary>
    Task<ToolResult> CallAsync(ToolCall call, CancellationToken cancellationToken);

    /// <summary>The write call of a call of a write tool, or null for another tool.</summary>
    WriteCall? AsWrite(ToolCall call);
}

/// <summary>The tools that the query harness can call for a user.</summary>
internal interface IToolSource
{
    /// <summary>Opens the tools for a user in a guild.</summary>
    Task<IToolSession> OpenAsync(ToolUser user, CancellationToken cancellationToken);

    /// <summary>Calls a write tool after the user accepted the call.</summary>
    Task<ToolResult> CallAsync(ToolUser user, WriteCall call, CancellationToken cancellationToken);
}

/// <summary>
/// The tools of the MCP services of the configuration. For each service, optio gets a token of the
/// user with the read scope by exchange, and the service lists the read tools. With a write scope,
/// optio also lists the tools with the read and write scopes. A tool that is only in that list is
/// a write tool: optio calls it only after the confirmation of the user, with a token of the write
/// scope. Code sets the argument <c>guild_id</c> to the guild of the stream, and the model does
/// not see it (decisions on #48 of 2026-11-24). Each call writes an audit line to the log.
/// </summary>
internal sealed partial class McpToolSource(
    TokenExchange tokens,
    IHttpClientFactory http,
    IOptions<OptioOptions> options,
    OptioMetrics metrics,
    ILogger<McpToolSource> logger
) : IToolSource
{
    public const string HttpClientName = "mcp";
    public const string GuildArgument = "guild_id";

    public async Task<IToolSession> OpenAsync(ToolUser user, CancellationToken cancellationToken)
    {
        var session = new Session(this, user);
        try
        {
            foreach (var service in options.Value.McpServices)
            {
                var client = await ConnectAsync(
                    service,
                    user,
                    service.ReadScope,
                    cancellationToken
                );
                session.Clients.Add(client);
                var read = await client.ListToolsAsync(cancellationToken: cancellationToken);
                foreach (var tool in read)
                    Add(session, service, tool, client);

                if (string.IsNullOrWhiteSpace(service.WriteScope))
                    continue;
                await using var writeClient = await ConnectAsync(
                    service,
                    user,
                    $"{service.ReadScope} {service.WriteScope}",
                    cancellationToken
                );
                foreach (
                    var tool in await writeClient.ListToolsAsync(
                        cancellationToken: cancellationToken
                    )
                )
                {
                    if (read.All(r => r.Name != tool.Name))
                        Add(session, service, tool, null);
                }
            }
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public async Task<ToolResult> CallAsync(
        ToolUser user,
        WriteCall call,
        CancellationToken cancellationToken
    )
    {
        var service = options.Value.McpServices.Single(s => s.Name == call.Service);
        await using var client = await ConnectAsync(
            service,
            user,
            service.WriteScope!,
            cancellationToken
        );
        return await CallToolAsync(client, service.Name, user, call.Call, cancellationToken);
    }

    private void Add(
        Session session,
        McpServiceOptions service,
        McpClientTool tool,
        McpClient? client
    )
    {
        if (!session.Add(service.Name, tool, client))
            LogDuplicateTool(logger, tool.Name, service.Name);
    }

    /// <summary>The JSON Schema of a tool without the argument <c>guild_id</c>.</summary>
    internal static JsonElement WithoutGuild(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        if (node["properties"] is JsonObject properties)
            properties.Remove(GuildArgument);
        if (node["required"] is JsonArray required)
        {
            foreach (
                var item in required.Where(r => r?.GetValue<string>() == GuildArgument).ToList()
            )
                required.Remove(item);
        }
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>A client of a service with a token of the user for the scope.</summary>
    private async Task<McpClient> ConnectAsync(
        McpServiceOptions service,
        ToolUser user,
        string scope,
        CancellationToken cancellationToken
    )
    {
        var token = await tokens.ExchangeAsync(user.AccessToken, scope, cancellationToken)
            ?? throw new QueryException(
                "The service cannot get your records now. Try again later."
            );
        return await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(service.Url),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string>
                    {
                        ["Authorization"] = $"Bearer {token}",
                    },
                },
                http.CreateClient(HttpClientName),
                ownsHttpClient: true
            ),
            cancellationToken: cancellationToken
        );
    }

    /// <summary>Calls a tool, and writes the audit line and the metric of the call.</summary>
    private async Task<ToolResult> CallToolAsync(
        McpClient client,
        string service,
        ToolUser user,
        ToolCall call,
        CancellationToken cancellationToken
    )
    {
        CallToolResult result;
        try
        {
            result = await client.CallToolAsync(
                call.Name,
                call.Arguments.ToDictionary(a => a.Key, a => (object?)a.Value),
                cancellationToken: cancellationToken
            );
        }
        catch
        {
            Audit(service, call.Name, user, "failed");
            throw;
        }
        Audit(service, call.Name, user, result.IsError == true ? "error" : "ok");
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return new ToolResult(call.Id, text, result.IsError == true);
    }

    private void Audit(string service, string tool, ToolUser user, string outcome)
    {
        LogToolCall(logger, user.User, user.Guild, service, tool, outcome);
        metrics.ToolCall(service, tool, outcome);
    }

    private sealed class Session(McpToolSource source, ToolUser user) : IToolSession
    {
        private readonly Dictionary<string, Route> _routes = [];
        private readonly List<ModelTool> _tools = [];

        public List<McpClient> Clients { get; } = [];

        public IReadOnlyList<ModelTool> Tools => _tools;

        /// <summary>False when a tool with the same name is already there.</summary>
        public bool Add(string service, McpClientTool tool, McpClient? client)
        {
            if (_routes.ContainsKey(tool.Name))
                return false;
            var names = tool.JsonSchema.TryGetProperty("properties", out var properties)
                ? properties.EnumerateObject().Select(p => p.Name).ToList()
                : [];
            var title = tool.Title ?? tool.Name;
            _routes[tool.Name] = new Route(
                service,
                title,
                client,
                names.Contains(GuildArgument),
                [.. names.Where(n => !n.EndsWith("_id", StringComparison.Ordinal))]
            );
            _tools.Add(
                new ModelTool(
                    tool.Name,
                    title,
                    tool.Description,
                    WithoutGuild(tool.JsonSchema),
                    client is null
                )
            );
            return true;
        }

        public async Task<ToolResult> CallAsync(ToolCall call, CancellationToken cancellationToken)
        {
            if (!_routes.TryGetValue(call.Name, out var route) || route.Client is null)
                return new ToolResult(call.Id, $"The tool {call.Name} does not exist.", true);
            return await source.CallToolAsync(
                route.Client,
                route.Service,
                user,
                WithGuild(call, route.HasGuild),
                cancellationToken
            );
        }

        public WriteCall? AsWrite(ToolCall call) =>
            _routes.TryGetValue(call.Name, out var route) && route.Client is null
                ? new WriteCall(
                    route.Service,
                    route.Title,
                    WithGuild(call, route.HasGuild),
                    route.Shown
                )
                : null;

        private ToolCall WithGuild(ToolCall call, bool hasGuild)
        {
            if (!hasGuild)
                return call;
            var arguments = call.Arguments.ToDictionary();
            arguments[GuildArgument] = JsonSerializer.SerializeToElement(
                user.Guild.ToString(CultureInfo.InvariantCulture)
            );
            return call with { Arguments = arguments };
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in Clients)
                await client.DisposeAsync();
        }

        /// <summary>The route of a tool. A write tool has no client: optio connects with the
        /// write scope after the confirmation.</summary>
        private sealed record Route(
            string Service,
            string Title,
            McpClient? Client,
            bool HasGuild,
            string[] Shown
        );
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The tool {Tool} of {Service} has the name of another tool. optio leaves it out"
    )]
    private static partial void LogDuplicateTool(ILogger logger, string tool, string service);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Tool call: user {User}, guild {Guild}, service {Service}, tool {Tool}, "
            + "outcome {Outcome}"
    )]
    private static partial void LogToolCall(
        ILogger logger,
        ulong user,
        ulong guild,
        string service,
        string tool,
        string outcome
    );
}

/// <summary>A failure of a query with a message for the user.</summary>
internal sealed class QueryException(string message) : Exception(message);
