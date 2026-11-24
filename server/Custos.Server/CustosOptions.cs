using System.ComponentModel.DataAnnotations;

namespace Custos.Server;

/// <summary>The configuration section <c>Custos</c>.</summary>
public sealed class CustosOptions
{
    public const string SectionName = "Custos";

    /// <summary>The issuer URL of the authorization server, for example a Keycloak realm.</summary>
    [Required, Url]
    public string Authority { get; set; } = "";

    /// <summary>
    /// The audience of the tokens of this server: the URL of the MCP endpoint as a client sees it,
    /// for example <c>https://custos.example.com/mcp</c>. The upload API and the MCP endpoint
    /// accept a token only with this value in <c>aud</c>.
    /// </summary>
    [Required, Url]
    public string Resource { get; set; } = "";

    [Range(1, int.MaxValue)]
    public int UploadMaxBytes { get; set; } = 4 * 1024 * 1024;

    [Range(0, int.MaxValue)]
    public int UploadMinIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// The origin of the Foxhole war API. Each shard has its own host, for example
    /// war-service-live-2 for shard 2.
    /// </summary>
    [Required, Url]
    public string WarApiBaseUrl { get; set; } = "https://war-service-live.foxholeservices.com";

    /// <summary>
    /// A stable label for the shard of <see cref="WarApiBaseUrl"/>. The war API does not label its
    /// shard. The label is a part of the ID of each structure, so do not change it during a war.
    /// The server supports one shard.
    /// </summary>
    [Required]
    public string Shard { get; set; } = "live";

    /// <summary>The time between two requests for the current war. <c>0</c> stops the
    /// requests.</summary>
    [Range(0, int.MaxValue)]
    public int WarPollIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// The guild of a request whose token has no claim <c>active_guild</c>. The server uses it only
    /// in the environment Development, and ignores it in all other environments.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long? DevelopmentGuildId { get; set; }

    /// <summary>The claim of the access token with the Discord user ID, as a string.</summary>
    [Required]
    public string DiscordIdClaim { get; set; } = "discord_id";

    /// <summary>
    /// The Discord user of a request whose token has no claim <see cref="DiscordIdClaim"/>. The
    /// server uses it only in the environment Development, and ignores it in all other
    /// environments.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long? DevelopmentDiscordUserId { get; set; }

    /// <summary>
    /// The catalog file of the game: display names and categories. A relative path starts at the
    /// folder of the server. Without the file, each name is the code name.
    /// </summary>
    [Required]
    public string CatalogPath { get; set; } = "catalog/catalog.json";

    public static string Combine(string baseUrl, string path) => baseUrl.TrimEnd('/') + path;
}
