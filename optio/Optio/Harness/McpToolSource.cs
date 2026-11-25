using System.Collections.Concurrent;
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

/// <summary>A service for the prompt: its name, its title for the user, its card, and whether
/// its tools are available now.</summary>
internal sealed record ServiceCard(string Name, string Title, string? Card, bool Available);

/// <summary>The services and the tools of one part of a query.</summary>
internal interface IToolSession : IAsyncDisposable
{
    IReadOnlyList<ServiceCard> Services { get; }

    IReadOnlyList<ModelTool> Tools { get; }

    /// <summary>Calls a tool. A failure of the tool or of its service gives a tool error.</summary>
    Task<ToolResult> CallAsync(ToolCall call, CancellationToken cancellationToken);
}

/// <summary>The tools that a query can call for a user.</summary>
internal interface IToolSource
{
    Task<IToolSession> OpenAsync(ToolUser user, CancellationToken cancellationToken);
}

/// <summary>
/// The tools of the MCP services of the configuration. Optio keeps the card and the tool list of
/// each service for <see cref="OptioOptions.CatalogMinutes"/>. It gets them again with the token
/// of a query, because Optio has no account of its own at a service. The model gets each read-only
/// tool as <c>{service}_{tool}</c>, without the hidden tools. Code sets the argument
/// <c>guild_id</c> to the guild of the query, and the model does not see it. Each call writes an
/// audit line.
/// </summary>
internal sealed partial class McpToolSource(
    TokenExchange tokens,
    IHttpClientFactory http,
    IOptions<OptioOptions> options,
    OptioMetrics metrics,
    TimeProvider time,
    ILogger<McpToolSource> logger
) : IToolSource
{
    public const string HttpClientName = "mcp";
    public const string GuildArgument = "guild_id";

    private readonly ConcurrentDictionary<string, Catalog> _catalogs = new();

    public async Task<IToolSession> OpenAsync(ToolUser user, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var session = new Session(this, user);
        try
        {
            foreach (var service in o.McpServices)
            {
                var catalog = await CatalogAsync(service, user, session, cancellationToken);
                session.Add(service, catalog, o.HiddenTools);
            }
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>The kept catalog of a service, or a new one when it is old. Null when the service
    /// is not available. A new catalog leaves its client in the session.</summary>
    private async Task<Catalog?> CatalogAsync(
        McpServiceOptions service,
        ToolUser user,
        Session session,
        CancellationToken cancellationToken
    )
    {
        var now = time.GetUtcNow();
        if (
            _catalogs.TryGetValue(service.Name, out var kept)
            && now - kept.Made < TimeSpan.FromMinutes(options.Value.CatalogMinutes)
        )
        {
            return kept;
        }

        try
        {
            var client = await ConnectAsync(service, user, cancellationToken);
            session.Clients[service.Name] = client;
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            var catalog = new Catalog(
                service.Title ?? client.ServerInfo?.Title ?? service.Name,
                string.IsNullOrWhiteSpace(service.Card) ? client.ServerInstructions : service.Card,
                [
                    .. tools.Select(t => new CatalogTool(
                        t.Name,
                        t.Title ?? t.ProtocolTool.Annotations?.Title ?? t.Name,
                        t.Description,
                        t.JsonSchema,
                        t.ProtocolTool.Annotations?.ReadOnlyHint == true
                    )),
                ],
                now
            );
            _catalogs[service.Name] = catalog;
            return catalog;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            LogServiceUnavailable(logger, e, service.Name);
            return null;
        }
    }

    /// <summary>A client of a service with a token of the user for the read scope.</summary>
    private async Task<McpClient> ConnectAsync(
        McpServiceOptions service,
        ToolUser user,
        CancellationToken cancellationToken
    )
    {
        var token = await tokens.ExchangeAsync(
            user.AccessToken,
            service.ReadScope,
            cancellationToken
        ) ?? throw new InvalidOperationException("The authorization server refused the token.");
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

    /// <summary>The JSON Schema of a tool without the arguments that code sets.</summary>
    internal static JsonElement Without(JsonElement schema, IReadOnlyCollection<string> names)
    {
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        if (node["properties"] is JsonObject properties)
        {
            foreach (var name in names)
                properties.Remove(name);
        }
        if (node["required"] is JsonArray required)
        {
            foreach (
                var item in required.Where(r => names.Contains(r?.GetValue<string>())).ToList()
            )
                required.Remove(item);
        }
        return JsonSerializer.SerializeToElement(node);
    }

    private void LogCallFailed(Exception e, string service, string tool) =>
        LogCallFailed(logger, e, service, tool);

    private void Audit(string service, string tool, ToolUser user, string outcome)
    {
        LogToolCall(logger, user.User, user.Guild, service, tool, outcome);
        metrics.ToolCall(service, tool, outcome);
    }

    /// <summary>The card and the tools of a service, as the service gave them at
    /// <paramref name="Made"/>.</summary>
    private sealed record Catalog(
        string Title,
        string? Card,
        IReadOnlyList<CatalogTool> Tools,
        DateTimeOffset Made
    );

    private sealed record CatalogTool(
        string Name,
        string Title,
        string Description,
        JsonElement Schema,
        bool ReadOnly
    );

    /// <summary>The route of a tool name of the model to its service.</summary>
    private sealed record Route(McpServiceOptions Service, CatalogTool Tool, bool HasGuild);

    private sealed class Session(McpToolSource source, ToolUser user) : IToolSession
    {
        private readonly Dictionary<string, Route> _routes = [];
        private readonly List<ModelTool> _tools = [];
        private readonly List<ServiceCard> _services = [];

        public Dictionary<string, McpClient> Clients { get; } = [];

        public IReadOnlyList<ServiceCard> Services => _services;

        public IReadOnlyList<ModelTool> Tools => _tools;

        public void Add(McpServiceOptions service, Catalog? catalog, string[] hidden)
        {
            _services.Add(
                new ServiceCard(
                    service.Name,
                    catalog?.Title ?? service.Title ?? service.Name,
                    catalog?.Card,
                    catalog is not null
                )
            );
            foreach (var tool in catalog?.Tools ?? [])
            {
                var name = $"{service.Name}_{tool.Name}";
                if (!tool.ReadOnly || hidden.Contains(name))
                    continue;
                var hasGuild =
                    tool.Schema.TryGetProperty("properties", out var properties)
                    && properties.TryGetProperty(GuildArgument, out _);
                _routes[name] = new Route(service, tool, hasGuild);
                _tools.Add(
                    new ModelTool(
                        name,
                        $"{catalog!.Title}: {tool.Title}",
                        tool.Description,
                        Without(tool.Schema, [GuildArgument])
                    )
                );
            }
        }

        public async Task<ToolResult> CallAsync(ToolCall call, CancellationToken cancellationToken)
        {
            if (!_routes.TryGetValue(call.Name, out var route))
                return new ToolResult(call.Id, $"The tool {call.Name} does not exist.", true);

            var service = route.Service.Name;
            var arguments = call.Arguments.ToDictionary(a => a.Key, a => (object?)a.Value);
            if (route.HasGuild)
                arguments[GuildArgument] = user.Guild.ToString(CultureInfo.InvariantCulture);
            try
            {
                if (!Clients.TryGetValue(service, out var client))
                {
                    client = await source.ConnectAsync(route.Service, user, cancellationToken);
                    Clients[service] = client;
                }
                var result = await client.CallToolAsync(
                    route.Tool.Name,
                    arguments,
                    cancellationToken: cancellationToken
                );
                source.Audit(
                    service,
                    route.Tool.Name,
                    user,
                    result.IsError == true ? "error" : "ok"
                );
                var text = string.Join(
                    "\n",
                    result.Content.OfType<TextContentBlock>().Select(c => c.Text)
                );
                return new ToolResult(call.Id, text, result.IsError == true);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                source.Audit(service, route.Tool.Name, user, "failed");
                source.LogCallFailed(e, service, route.Tool.Name);
                return new ToolResult(call.Id, "The service did not answer. Try later.", true);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in Clients.Values)
                await client.DisposeAsync();
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The MCP service {Service} is not available. The query goes on without it"
    )]
    private static partial void LogServiceUnavailable(
        ILogger logger,
        Exception exception,
        string service
    );

    [LoggerMessage(Level = LogLevel.Warning, Message = "The tool {Tool} of {Service} failed")]
    private static partial void LogCallFailed(
        ILogger logger,
        Exception exception,
        string service,
        string tool
    );

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
