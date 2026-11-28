using Optio.Events;
using Optio.Harness;
using Optio.Storage;

namespace Optio.Queries;

internal static class QuerySetup
{
    /// <summary>
    /// Registers the query when the configuration has the stream, the Claude API key, the system
    /// prompt, and one MCP service or more. Without them, the discovery document has no
    /// <c>query_endpoint</c>, and the upload and the stream still work.
    /// </summary>
    public static IHostApplicationBuilder AddOptioQuery(this IHostApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(OptioOptions.SectionName).Get<OptioOptions>()
            ?? new OptioOptions();
        if (
            !builder.Services.Any(s => s.ServiceType == typeof(EventStream))
            || string.IsNullOrWhiteSpace(options.ClaudeApiKey)
            || string.IsNullOrWhiteSpace(options.SystemPrompt)
            || options.McpServices.Count == 0
        )
        {
            return builder;
        }

        builder.Services.AddHttpClient(McpToolSource.HttpClientName);
        builder.Services.AddSingleton<IModelClient, AnthropicModelClient>();
        builder.Services.AddSingleton<IToolSource, McpToolSource>();
        builder.Services.AddSingleton<UsageRecorder>();
        builder.Services.AddSingleton<QueryHarness>();
        builder.Services.AddSingleton<QueryStore>();
        builder.Services.AddSingleton<QueryService>();
        builder.Services.AddHostedService(services => services.GetRequiredService<QueryService>());
        return builder;
    }
}
