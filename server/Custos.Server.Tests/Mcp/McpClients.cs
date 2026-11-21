using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Custos.Server.Tests.Mcp;

/// <summary>An MCP client of the SDK over the HTTP client of the test server.</summary>
internal static class McpClients
{
    public static Task<McpClient> ConnectAsync(HttpClient http) =>
        McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(http.BaseAddress!, "/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                },
                http,
                ownsHttpClient: false
            )
        );

    /// <summary>The text of the first content block.</summary>
    public static string Text(CallToolResult result) => ((TextContentBlock)result.Content[0]).Text;
}
