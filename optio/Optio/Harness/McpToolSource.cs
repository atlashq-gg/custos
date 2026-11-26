using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
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
/// of a query, because Optio has no account of its own at a service. A new catalog with a tool that
/// the earlier catalog did not have writes a warning. <see cref="McpCatalog"/> makes the tools for
/// the model. Code sets the argument <c>guild_id</c> to the guild of the query, and
/// <c>confirm</c> of a write tool to true. Each call writes an audit line.
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
            if (kept is not null)
            {
                foreach (var tool in McpCatalog.NewTools(kept, catalog))
                    LogNewTool(logger, service.Name, tool);
            }
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

    private void LogCallFailed(Exception e, string service, string tool) =>
        LogCallFailed(logger, e, service, tool);

    private void Audit(string service, string tool, ToolUser user, string outcome)
    {
        LogToolCall(logger, user.User, user.Guild, service, tool, outcome);
        metrics.ToolCall(service, tool, outcome);
    }

    private sealed class TokenRefusedException()
        : Exception("The authorization server refused the token.");

    private sealed class Session(McpToolSource source, ToolUser user) : IToolSession
    {
        private readonly Dictionary<string, ToolRoute> _routes = [];
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
            if (catalog is null)
                return;
            foreach (var (tool, route) in McpCatalog.Tools(service, catalog, hidden))
            {
                _routes[tool.Name] = route;
                _tools.Add(tool);
            }
        }

        public async Task<ToolResult> CallAsync(ToolCall call, CancellationToken cancellationToken)
        {
            if (!_routes.TryGetValue(call.Name, out var route) || route.Write)
                return new ToolResult(call.Id, $"The tool {call.Name} does not exist.", true);

            var service = route.Service.Name;
            var arguments = call.Arguments.ToDictionary(a => a.Key, a => (object?)a.Value);
            if (route.HasGuild)
                arguments[McpCatalog.GuildArgument] = user.Guild.ToString(
                    CultureInfo.InvariantCulture
                );
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
                arguments[McpCatalog.GuildArgument] = JsonSerializer.SerializeToElement(
                    user.Guild.ToString(CultureInfo.InvariantCulture)
                );
            }
            if (route.Confirm)
                arguments[McpCatalog.ConfirmArgument] = JsonSerializer.SerializeToElement(true);
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
                            a.Key != McpCatalog.GuildArgument
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

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The MCP service {Service} has the new tool {Tool}. The query uses it, but the "
            + "evaluation set did not run with it"
    )]
    private static partial void LogNewTool(ILogger logger, string service, string tool);

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
