using System.ComponentModel.DataAnnotations;

namespace Optio;

/// <summary>The configuration section <c>Optio</c>.</summary>
public sealed class OptioOptions : IValidatableObject
{
    public const string SectionName = "Optio";

    /// <summary>The origin of Optio as Custos sees it. The discovery document builds its URLs from
    /// it.</summary>
    [Required, Url]
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>The name that Custos shows to the user.</summary>
    [Required]
    public string ServiceName { get; set; } = "optio";

    /// <summary>A page for humans about the service, for the discovery document.</summary>
    [Url]
    public string? ServiceDocumentation { get; set; }

    /// <summary>The scopes that Custos requests. Set them in configuration: a binding appends to a
    /// default array.</summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>The largest upload. It must be equal to the value of the server.</summary>
    [Range(1, int.MaxValue)]
    public int UploadMaxBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>The upload interval of the server, for the discovery document. The server applies
    /// it.</summary>
    [Range(0, int.MaxValue)]
    public int UploadMinIntervalSeconds { get; set; } = 60;

    /// <summary>The issuer URL of the authorization server, for example a Keycloak realm.</summary>
    [Required, Url]
    public string Authority { get; set; } = "";

    /// <summary>
    /// The URL of the metadata document of the authorization server. Empty means
    /// <c>{Authority}/.well-known/openid-configuration</c>.
    /// </summary>
    [Url]
    public string? OpenIdConfiguration { get; set; }

    /// <summary>
    /// The client ID of Custos. Empty means the URL of the client metadata document of Optio. Set
    /// it when the authorization server reaches Optio under a different host than Custos.
    /// </summary>
    public string? CustosClientId { get; set; }

    /// <summary>The client ID of optio. A token of Custos for Optio has it in <c>aud</c>.</summary>
    [Required]
    public string OptioClientId { get; set; } = "optio";

    /// <summary>The secret of the client optio, for the token exchange. Keep it in
    /// <c>/run/secrets/Optio__OptioClientSecret</c>.</summary>
    [Required]
    public string OptioClientSecret { get; set; } = "";

    /// <summary>The upload API of the server, for example
    /// <c>http://server:8080/api/v1/uploads</c>.</summary>
    [Required, Url]
    public string UploadApiUrl { get; set; } = "";

    /// <summary>The scope of the token for the upload API.</summary>
    [Required]
    public string UploadScope { get; set; } = "custos:write";

    /// <summary>How long the broker keeps the events for a replay: the maximum age of the stream
    /// of the events (optio/docs/producers.md).</summary>
    [Range(1, 1440)]
    public int ReplayWindowMinutes { get; set; } = 15;

    /// <summary>The maximum size of the stream of the events on the broker. The broker then
    /// removes the oldest events.</summary>
    [Range(1, 100_000)]
    public int StreamMaxMegabytes { get; set; } = 100;

    /// <summary>The open streams of one user. One more stream gets <c>429</c>.</summary>
    [Range(1, 100)]
    public int MaxStreamsPerUser { get; set; } = 5;

    /// <summary>The token of the Discord bot of Optio. Keep it in
    /// <c>/run/secrets/Optio__DiscordToken</c>. Without it, Optio has no stream.</summary>
    public string? DiscordToken { get; set; }

    /// <summary>The guild of a stream whose token has no claim <c>active_guild</c>. Only in the
    /// environment Development, as in the server.</summary>
    public long? DevelopmentGuildId { get; set; }

    /// <summary>The Discord user of a stream whose token has no claim <c>discord_id</c>. Only in
    /// the environment Development, as in the server.</summary>
    public long? DevelopmentDiscordUserId { get; set; }

    /// <summary>The key of the Claude API. Keep it in <c>/run/secrets/Optio__ClaudeApiKey</c>.
    /// Without it, Optio answers no query.</summary>
    public string? ClaudeApiKey { get; set; }

    /// <summary>The model of the query.</summary>
    [Required]
    public string ModelId { get; set; } = "claude-haiku-5-5";

    /// <summary>The effort of the model: <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c>, or
    /// <c>max</c>.</summary>
    [RegularExpression("^(low|medium|high|xhigh|max)$")]
    public string ModelEffort { get; set; } = "medium";

    /// <summary>The system prompt of the query. The repository holds an example in
    /// <c>optio/docs/system-prompt.example.txt</c>. Without it, Optio answers no query.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>The text of the operator about the services together: the words that they share,
    /// and how their records relate. It follows the cards of the services in the prompt.</summary>
    public string? OperatorText { get; set; }

    /// <summary>The MCP services whose tools a query can use.</summary>
    public List<McpServiceOptions> McpServices { get; set; } = [];

    /// <summary>The tools that the model does not get, as <c>{service}_{tool}</c>, for example a
    /// tool that only selects a guild.</summary>
    public string[] HiddenTools { get; set; } = [];

    /// <summary>The queries of one user in one hour.</summary>
    [Range(1, 10_000)]
    public int MaxQueriesPerHour { get; set; } = 20;

    /// <summary>The follow-up window: a query in this time after the last answer of a client
    /// continues its conversation.</summary>
    [Range(1, 1440)]
    public int FollowUpMinutes { get; set; } = 10;

    /// <summary>The largest conversation of a client, as JSON. Optio removes the oldest queries
    /// of a larger conversation.</summary>
    [Range(1_000, 500_000)]
    public int MaxConversationBytes { get; set; } = 100_000;

    /// <summary>The budget of time of one part of a query. After it, the model gets one last step
    /// with no tools.</summary>
    [Range(5, 600)]
    public int PartBudgetSeconds { get; set; } = 60;

    /// <summary>The lifetime of a question before a change. Without a decision, the query then
    /// closes as expired.</summary>
    [Range(10, 600)]
    public int QuestionSeconds { get; set; } = 60;

    /// <summary>The budget of tool calls of one part of a query.</summary>
    [Range(1, 100)]
    public int MaxToolCallsPerPart { get; set; } = 8;

    /// <summary>How long Optio keeps the card and the tool list of a service before it gets them
    /// again.</summary>
    [Range(1, 1440)]
    public int CatalogMinutes { get; set; } = 10;

    /// <summary>The data annotations do not reach the items of a list: this examines each MCP
    /// service, and that each has its own name.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var service in McpServices)
        {
            List<ValidationResult> results = [];
            Validator.TryValidateObject(service, new(service), results, true);
            foreach (var result in results)
                yield return result;
        }
        if (McpServices.DistinctBy(s => s.Name).Count() != McpServices.Count)
            yield return new ValidationResult("Each MCP service needs its own name.");
    }

    public TimeSpan FollowUpWindow => TimeSpan.FromMinutes(FollowUpMinutes);

    public TimeSpan PartBudget => TimeSpan.FromSeconds(PartBudgetSeconds);

    /// <summary>The name of the connection string of the broker: a NATS URL with the broker user,
    /// for example <c>nats://optio:...@broker:4222</c>.</summary>
    public const string BrokerConnectionName = "events";

    public TimeSpan ReplayWindow => TimeSpan.FromMinutes(ReplayWindowMinutes);

    public string ResolvedOpenIdConfiguration =>
        string.IsNullOrEmpty(OpenIdConfiguration)
            ? Combine(Authority, "/.well-known/openid-configuration")
            : OpenIdConfiguration;

    public string ResolvedCustosClientId =>
        string.IsNullOrEmpty(CustosClientId)
            ? Combine(PublicBaseUrl, Endpoints.ClientMetadata.Path)
            : CustosClientId;

    public static string Combine(string baseUrl, string path) => baseUrl.TrimEnd('/') + path;
}

/// <summary>An MCP service of the query.</summary>
public sealed class McpServiceOptions
{
    /// <summary>A short name. It is the first part of the name of each tool of the
    /// service.</summary>
    [Required, RegularExpression("^[a-z0-9]{1,16}$")]
    public string Name { get; set; } = "";

    /// <summary>The name of the service for the user, for example in a status line. Empty means
    /// the title that the service gives, else <see cref="Name"/>.</summary>
    public string? Title { get; set; }

    /// <summary>The MCP endpoint, for example <c>http://server:8080/mcp</c>.</summary>
    [Required, Url]
    public string Url { get; set; } = "";

    /// <summary>The scope of the token for the read tools, for example
    /// <c>custos:read</c>.</summary>
    [Required]
    public string ReadScope { get; set; } = "";

    /// <summary>The scope of the token for the write tools, for example <c>custos:write</c>.
    /// Without it, a query uses no write tool of the service.</summary>
    public string? WriteScope { get; set; }

    /// <summary>The card of the service, for a service that gives none in its MCP
    /// <c>instructions</c>. It replaces the card of the service.</summary>
    public string? Card { get; set; }
}
