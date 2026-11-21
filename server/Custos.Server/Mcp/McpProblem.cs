using System.Text.Encodings.Web;
using System.Text.Json;

namespace Custos.Server.Mcp;

/// <summary>
/// A refusal of a tool call, as a problem body (RFC 9457) in a tool result with <c>isError</c>. A
/// refusal for access is never an HTTP <c>403</c>: a harness answers that with a new login, and a
/// new login gives no role. Only a token problem gets an HTTP status.
/// </summary>
public sealed record McpProblem(string Type, string Title, int Status, string? Detail)
{
    public const string ForbiddenType = "urn:custos:mcp:forbidden";
    public const string NotVisibleType = "urn:custos:mcp:not-visible";
    public const string DiscordUnavailableType = "urn:custos:mcp:discord-unavailable";
    public const string InsufficientScopeType = "urn:custos:mcp:insufficient-scope";

    /// <summary>The scope that <see cref="InsufficientScope"/> names.</summary>
    public string? RequiredScope { get; init; }

    public static McpProblem Forbidden(string detail) =>
        new(ForbiddenType, "You do not have the access for this tool.", 403, detail);

    /// <summary>
    /// The answer for a guild that does not exist, a guild without the bot, and a guild where the
    /// caller is not a member. The answer is the same, so it does not tell that a guild exists.
    /// </summary>
    public static McpProblem NotVisible() =>
        new(NotVisibleType, "Not found, or not visible to you.", 404, null);

    public static McpProblem DiscordUnavailable() =>
        new(
            DiscordUnavailableType,
            "Discord is not available.",
            503,
            "The server could not read your roles from Discord. Try again later."
        );

    public static McpProblem InsufficientScope(string scope) =>
        new(
            InsufficientScopeType,
            "Your login does not permit this tool.",
            403,
            $"This tool needs the scope {scope}. Log in again and grant it."
        )
        {
            RequiredScope = scope,
        };

    public string ToJson() => JsonSerializer.Serialize(this, McpJson.Options);
}

/// <summary>The JSON of the tool results: snake_case members, no null members.</summary>
public static class McpJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System
                .Text
                .Json
                .Serialization
                .JsonIgnoreCondition
                .WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
