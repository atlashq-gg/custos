using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Optio.Tokens;

namespace Optio.Harness;

/// <summary>The tools of one query.</summary>
internal interface IToolSession : IAsyncDisposable
{
    IReadOnlyList<ModelTool> Tools { get; }

    Task<ToolResult> CallAsync(ToolCall call, CancellationToken cancellationToken);
}

/// <summary>The tools that the query harness can call for a user.</summary>
internal interface IToolSource
{
    /// <summary>Opens the tools for a user in a guild, with the access token of Custos.</summary>
    Task<IToolSession> OpenAsync(
        string accessToken,
        ulong guild,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// The read tools of the MCP services of the configuration. For each service, optio gets a token
/// of the user with the read scope by exchange. The service lists only the tools of that scope.
/// Code sets the argument <c>guild_id</c> to the guild of the stream, and the model does not see
/// it (decision on #48 of 2026-11-24).
/// </summary>
internal sealed partial class McpToolSource(
    TokenExchange tokens,
    IHttpClientFactory http,
    IOptions<OptioOptions> options,
    ILogger<McpToolSource> logger
) : IToolSource
{
    public const string HttpClientName = "mcp";
    public const string GuildArgument = "guild_id";

    public async Task<IToolSession> OpenAsync(
        string accessToken,
        ulong guild,
        CancellationToken cancellationToken
    )
    {
        var session = new Session(guild);
        try
        {
            foreach (var service in options.Value.McpServices)
            {
                var token = await tokens.ExchangeAsync(
                    accessToken,
                    service.ReadScope,
                    cancellationToken
                )
                    ?? throw new QueryException(
                        "The service cannot read the records for you now. Try again later."
                    );
                var client = await McpClient.CreateAsync(
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
                session.Clients.Add(client);
                foreach (
                    var tool in await client.ListToolsAsync(cancellationToken: cancellationToken)
                )
                {
                    if (!session.Add(tool, client))
                        LogDuplicateTool(logger, tool.Name, service.Name);
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

    private sealed class Session(ulong guild) : IToolSession
    {
        private readonly Dictionary<string, (McpClient Client, bool HasGuild)> _routes = [];
        private readonly List<ModelTool> _tools = [];

        public List<McpClient> Clients { get; } = [];

        public IReadOnlyList<ModelTool> Tools => _tools;

        /// <summary>False when a tool with the same name is already there.</summary>
        public bool Add(McpClientTool tool, McpClient client)
        {
            if (_routes.ContainsKey(tool.Name))
                return false;
            var hasGuild =
                tool.JsonSchema.TryGetProperty("properties", out var properties)
                && properties.TryGetProperty(GuildArgument, out _);
            _routes[tool.Name] = (client, hasGuild);
            _tools.Add(
                new ModelTool(
                    tool.Name,
                    tool.Title ?? tool.Name,
                    tool.Description,
                    WithoutGuild(tool.JsonSchema)
                )
            );
            return true;
        }

        public async Task<ToolResult> CallAsync(ToolCall call, CancellationToken cancellationToken)
        {
            if (!_routes.TryGetValue(call.Name, out var route))
                return new ToolResult(call.Id, $"The tool {call.Name} does not exist.", true);

            var arguments = call.Arguments.ToDictionary(a => a.Key, a => (object?)a.Value);
            if (route.HasGuild)
                arguments[GuildArgument] = guild.ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                );
            var result = await route.Client.CallToolAsync(
                call.Name,
                arguments,
                cancellationToken: cancellationToken
            );
            var text = string.Join(
                "\n",
                result.Content.OfType<TextContentBlock>().Select(c => c.Text)
            );
            return new ToolResult(call.Id, text, result.IsError == true);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in Clients)
                await client.DisposeAsync();
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The tool {Tool} of {Service} has the name of another tool. optio leaves it out"
    )]
    private static partial void LogDuplicateTool(ILogger logger, string tool, string service);
}

/// <summary>A failure of a query with a message for the user.</summary>
internal sealed class QueryException(string message) : Exception(message);
