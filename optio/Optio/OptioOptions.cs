using System.ComponentModel.DataAnnotations;

namespace Optio;

/// <summary>The configuration section <c>Optio</c>.</summary>
public sealed class OptioOptions
{
    public const string SectionName = "Optio";

    /// <summary>The origin of optio as Custos sees it. The discovery document builds its URLs from
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
    /// The client ID of Custos. Empty means the URL of the client metadata document of optio. Set
    /// it when the authorization server reaches optio under a different host than Custos.
    /// </summary>
    public string? CustosClientId { get; set; }

    /// <summary>The client ID of optio. A token of Custos for optio has it in <c>aud</c>.</summary>
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

    /// <summary>
    /// The sources of events (optio/docs/producers.md). optio reads the stream
    /// <c>events.&lt;source&gt;.stream</c> of each source from the broker in the connection string
    /// <see cref="BrokerConnectionName"/>.
    /// </summary>
    public string[] Sources { get; set; } = [];

    /// <summary>How long optio keeps the events for a replay.</summary>
    [Range(1, 1440)]
    public int ReplayWindowMinutes { get; set; } = 15;

    /// <summary>The open streams of one user. One more stream gets <c>429</c>.</summary>
    [Range(1, 100)]
    public int MaxStreamsPerUser { get; set; } = 5;

    /// <summary>The token of the Discord bot of optio. Keep it in
    /// <c>/run/secrets/Optio__DiscordToken</c>. Without it, optio has no stream.</summary>
    public string? DiscordToken { get; set; }

    /// <summary>The guild of a stream whose token has no claim <c>active_guild</c>. Only in the
    /// environment Development, as in the server.</summary>
    public long? DevelopmentGuildId { get; set; }

    /// <summary>The Discord user of a stream whose token has no claim <c>discord_id</c>. Only in
    /// the environment Development, as in the server.</summary>
    public long? DevelopmentDiscordUserId { get; set; }

    /// <summary>The key of the model provider. Keep it in <c>/run/secrets/Optio__ModelApiKey</c>.
    /// Without it, optio answers no query.</summary>
    public string? ModelApiKey { get; set; }

    /// <summary>The model of the query harness.</summary>
    [Required]
    public string ModelId { get; set; } = "claude-haiku-5-5";

    /// <summary>The effort of the model: <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c>, or
    /// <c>max</c>.</summary>
    [RegularExpression("^(low|medium|high|xhigh|max)$")]
    public string ModelEffort { get; set; } = "medium";

    /// <summary>The system prompt of the query harness. The repository holds an example in
    /// <c>optio/docs/system-prompt.example.txt</c>. Without it, optio answers no query.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>The queries of one user in one hour.</summary>
    [Range(1, 10000)]
    public int MaxQueriesPerHour { get; set; } = 20;

    /// <summary>The MCP services whose read tools the query harness can call.</summary>
    public List<McpServiceOptions> McpServices { get; set; } = [];

    /// <summary>The name of the connection string of the broker.</summary>
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

/// <summary>An MCP service of the query harness.</summary>
public sealed class McpServiceOptions
{
    /// <summary>A short name for the logs.</summary>
    [Required]
    public string Name { get; set; } = "";

    /// <summary>The MCP endpoint, for example <c>http://server:8080/mcp</c>.</summary>
    [Required, Url]
    public string Url { get; set; } = "";

    /// <summary>The scope of the token for the read tools, for example
    /// <c>custos:read</c>.</summary>
    [Required]
    public string ReadScope { get; set; } = "";
}
