using Optio.Storage;
using Optio.Streams;

namespace Optio.Harness;

internal static class HarnessSetup
{
    /// <summary>
    /// Registers the query when the configuration has the stream, the database, the model key,
    /// the system prompt, and one MCP service or more. Without them, the discovery document has
    /// no <c>query_endpoint</c>, and the upload and the stream still work.
    /// </summary>
    public static IHostApplicationBuilder AddOptioHarness(this IHostApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(OptioOptions.SectionName).Get<OptioOptions>()
            ?? new OptioOptions();
        if (
            !StreamSetup.IsEnabled(builder.Configuration)
            || !StorageSetup.IsEnabled(builder.Configuration)
            || string.IsNullOrWhiteSpace(options.ModelApiKey)
            || string.IsNullOrWhiteSpace(options.SystemPrompt)
            || options.McpServices.Count == 0
        )
        {
            return builder;
        }

        builder.Services.AddSingleton<IModelClient, AnthropicModelClient>();
        builder.Services.AddSingleton<QueryLimiter>();
        builder.Services.AddScoped<IToolSource, McpToolSource>();
        builder.Services.AddScoped<QueryHarness>();
        builder.Services.AddHttpClient(McpToolSource.HttpClientName);
        return builder;
    }
}
