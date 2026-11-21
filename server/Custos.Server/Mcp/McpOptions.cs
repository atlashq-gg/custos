namespace Custos.Server.Mcp;

/// <summary>
/// The configuration section <c>Mcp</c>: the MCP endpoint as an OAuth resource server. The
/// authorization server issues the tokens. The server only validates them.
/// </summary>
public sealed class McpOptions
{
    public const string SectionName = "Mcp";
    public const string Path = "/mcp";
    public const string MetadataPath = "/.well-known/oauth-protected-resource";

    /// <summary>Without <c>true</c>, the server maps no MCP endpoint.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The URL of the endpoint as a client sees it, for example
    /// <c>https://custos.example.com/mcp</c>. A token must have it in <c>aud</c>. Its path must be
    /// <see cref="Path"/>.
    /// </summary>
    public string? Resource { get; set; }

    /// <summary>The issuer of the MCP tokens. Empty means <see
    /// cref="CustosOptions.Authority"/>.</summary>
    public string? Authority { get; set; }

    /// <summary>The scope of the read tools.</summary>
    public string ReadScope { get; set; } = "custos:read";

    /// <summary>The scope of the label tool.</summary>
    public string WriteScope { get; set; } = "custos:write";

    /// <summary>The largest number of MCP requests of one caller in one minute.</summary>
    public int RequestsPerMinute { get; set; } = 60;

    public Uri ResourceUri => new(Resource!);

    /// <summary>The URL of the metadata document (RFC 9728), at the root of the origin of <see
    /// cref="Resource"/>.</summary>
    public string MetadataUrl => $"{ResourceUri.GetLeftPart(UriPartial.Authority)}{MetadataPath}";

    /// <summary>The first problem of an enabled section, or <c>null</c>.</summary>
    public string? Problem()
    {
        if (!Enabled)
            return null;
        if (
            !Uri.TryCreate(Resource, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
        )
        {
            return $"{SectionName}:Resource must be an absolute HTTP URL without a query.";
        }
        if (uri.AbsolutePath != Path)
            return $"{SectionName}:Resource must have the path {Path}.";
        if (!string.IsNullOrEmpty(Authority) && !Uri.TryCreate(Authority, UriKind.Absolute, out _))
            return $"{SectionName}:Authority must be an absolute URL.";
        if (string.IsNullOrWhiteSpace(ReadScope) || string.IsNullOrWhiteSpace(WriteScope))
            return $"{SectionName}:ReadScope and {SectionName}:WriteScope must have a value.";
        if (RequestsPerMinute < 1)
            return $"{SectionName}:RequestsPerMinute must be 1 or more.";
        return null;
    }
}
