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

/// <summary>
/// A call of a write tool, as the service gets it after the user accepts. <paramref name="Call"/>
/// has the name of the tool at the service and the arguments for the service: the guild, and
/// <c>confirm</c> true. <paramref name="Shown"/> holds the text values that the question must
/// show, and <paramref name="Ids"/> the values of the ID arguments.
/// </summary>
internal sealed record WriteCall(
    string Service,
    string Title,
    string Status,
    ToolCall Call,
    List<string?> Shown,
    List<string> Ids
);

/// <summary>The services and the tools of one part of a query.</summary>
internal interface IToolSession : IAsyncDisposable
{
    IReadOnlyList<ServiceCard> Services { get; }

    IReadOnlyList<ModelTool> Tools { get; }

    /// <summary>Calls a read tool. A failure of the tool or of its service gives a tool
    /// error.</summary>
    Task<ToolResult> CallAsync(ToolCall call, CancellationToken cancellationToken);

    /// <summary>The write call of a call of the model, or null for a read tool.</summary>
    WriteCall? AsWrite(ToolCall call);
}

/// <summary>The tools that a query can call for a user.</summary>
internal interface IToolSource
{
    Task<IToolSession> OpenAsync(ToolUser user, CancellationToken cancellationToken);

    /// <summary>
    /// Calls a write tool after the user accepted it, with a token of the write scope. A refusal
    /// of the token or an error of the tool gives a tool error. A failure of the service throws:
    /// the result of the change is then not known.
    /// </summary>
    Task<ToolResult> CallWriteAsync(
        ToolUser user,
        WriteCall call,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// The tools of the MCP services of the configuration. Optio keeps the card and the tool list of
/// each service for <see cref="OptioOptions.CatalogMinutes"/>. It gets them again with the token
/// of a query, because Optio has no account of its own at a service. The model gets each tool as
/// <c>{service}_{tool}</c>, without the hidden tools. A tool that the service does not mark
/// read-only is a write tool, and only a service with a write scope gives its write tools. Code
/// sets the argument <c>guild_id</c> to the guild of the query, and <c>confirm</c> of a write tool
/// to true. The model does not see them. Each call writes an audit line.
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
    public const string ConfirmArgument = "confirm";

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
            var client = await ConnectAsync(service, user, service.ReadScope, cancellationToken);
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

    public async Task<ToolResult> CallWriteAsync(
        ToolUser user,
        WriteCall call,
        CancellationToken cancellationToken
    )
    {
        var service = options.Value.McpServices.Single(s => s.Name == call.Service);
        McpClient client;
        try
        {
            client = await ConnectAsync(service, user, service.WriteScope!, cancellationToken);
        }
        catch (TokenRefusedException)
        {
            // No change: the service got no call.
            Audit(service.Name, call.Call.Name, user, "refused");
            return new ToolResult(
                call.Call.Id,
                "The service refused the access for this change. The user can lack the access.",
                true
            );
        }
        await using (client)
        {
            CallToolResult result;
            try
            {
                result = await client.CallToolAsync(
                    call.Call.Name,
                    call.Call.Arguments.ToDictionary(a => a.Key, a => (object?)a.Value),
                    cancellationToken: cancellationToken
                );
            }
            catch
            {
                Audit(service.Name, call.Call.Name, user, "failed");
                throw;
            }
            Audit(service.Name, call.Call.Name, user, result.IsError == true ? "error" : "ok");
            return new ToolResult(call.Call.Id, Text(result), result.IsError == true);
        }
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
            ?? throw new TokenRefusedException();
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

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    /// <summary>True when the JSON Schema of an argument allows the type.</summary>
    private static bool HasType(JsonElement schema, string type) =>
        schema.TryGetProperty("type", out var t)
        && (
            t.ValueKind == JsonValueKind.String
                ? t.GetString() == type
                : t.ValueKind == JsonValueKind.Array
                    && t.EnumerateArray().Any(i => i.GetString() == type)
        );

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

    /// <summary>The route of a tool name of the model to its service. A write tool has
    /// <paramref name="Shown"/>, its text arguments with no <c>_id</c> at the end, and
    /// <paramref name="Confirm"/> when it has the boolean argument <c>confirm</c>.</summary>
    private sealed record Route(
        McpServiceOptions Service,
        CatalogTool Tool,
        string Status,
        bool HasGuild,
        bool Write,
        bool Confirm,
        string[] Shown
    );

    private sealed class TokenRefusedException()
        : Exception("The authorization server refused the token.");

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
                var write = !tool.ReadOnly;
                if (hidden.Contains(name) || (write && string.IsNullOrEmpty(service.WriteScope)))
                    continue;
                var properties = tool.Schema.TryGetProperty("properties", out var p)
                    ? p.EnumerateObject().ToList()
                    : [];
                var confirm =
                    write
                    && properties.Any(a =>
                        a.Name == ConfirmArgument && HasType(a.Value, "boolean")
                    );
                string[] codeSet = confirm ? [GuildArgument, ConfirmArgument] : [GuildArgument];
                var status = $"{catalog!.Title}: {tool.Title}";
                _routes[name] = new Route(
                    service,
                    tool,
                    status,
                    properties.Any(a => a.Name == GuildArgument),
                    write,
                    confirm,
                    [
                        .. properties
                            .Where(a =>
                                write
                                && !a.Name.EndsWith("_id", StringComparison.Ordinal)
                                && !codeSet.Contains(a.Name)
                                && HasType(a.Value, "string")
                            )
                            .Select(a => a.Name),
                    ]
                );
                var schema = Without(tool.Schema, codeSet);
                _tools.Add(
                    new ModelTool(
                        name,
                        status,
                        tool.Description,
                        write ? Questions.WithTextArgument(schema) : schema,
                        write
                    )
                );
            }
        }

        public async Task<ToolResult> CallAsync(ToolCall call, CancellationToken cancellationToken)
        {
            if (!_routes.TryGetValue(call.Name, out var route) || route.Write)
                return new ToolResult(call.Id, $"The tool {call.Name} does not exist.", true);

            var service = route.Service.Name;
            var arguments = call.Arguments.ToDictionary(a => a.Key, a => (object?)a.Value);
            if (route.HasGuild)
                arguments[GuildArgument] = user.Guild.ToString(CultureInfo.InvariantCulture);
            try
            {
                if (!Clients.TryGetValue(service, out var client))
                {
                    client = await source.ConnectAsync(
                        route.Service,
                        user,
                        route.Service.ReadScope,
                        cancellationToken
                    );
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
                return new ToolResult(call.Id, Text(result), result.IsError == true);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                source.Audit(service, route.Tool.Name, user, "failed");
                source.LogCallFailed(e, service, route.Tool.Name);
                return new ToolResult(call.Id, "The service did not answer. Try later.", true);
            }
        }

        public WriteCall? AsWrite(ToolCall call)
        {
            if (!_routes.TryGetValue(call.Name, out var route) || !route.Write)
                return null;
            var arguments = call.Arguments.ToDictionary();
            arguments.Remove(Questions.TextArgument);
            if (route.HasGuild)
            {
                arguments[GuildArgument] = JsonSerializer.SerializeToElement(
                    user.Guild.ToString(CultureInfo.InvariantCulture)
                );
            }
            if (route.Confirm)
                arguments[ConfirmArgument] = JsonSerializer.SerializeToElement(true);
            return new WriteCall(
                route.Service.Name,
                route.Tool.Title,
                route.Status,
                call with
                {
                    Name = route.Tool.Name,
                    Arguments = arguments,
                },
                [.. route.Shown.Select(name => Value(arguments, name))],
                [
                    .. arguments
                        .Where(a =>
                            a.Key != GuildArgument
                            && a.Key.EndsWith("_id", StringComparison.Ordinal)
                            && a.Value.ValueKind == JsonValueKind.String
                        )
                        .Select(a => a.Value.GetString()!),
                ]
            );
        }

        /// <summary>The value of an argument as text, or null when the call has no
        /// value.</summary>
        private static string? Value(Dictionary<string, JsonElement> arguments, string name) =>
            arguments.TryGetValue(name, out var value)
                ? value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Null => null,
                    _ => value.GetRawText(),
                }
                : null;

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
