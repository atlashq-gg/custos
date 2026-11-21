using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Custos.Server.Discord;

/// <summary>
/// The settings document of a guild. Its members are in PascalCase and each ID is a string, as
/// <c>/custosadmin settings</c> shows it. A guild without settings has <see cref="Default"/>.
/// </summary>
public sealed record GuildSettings
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The largest number of entries in one list.</summary>
    public const int MaxListEntries = 25;

    public static readonly GuildSettings Default = new();

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public AccessSettings Access { get; init; } = new();
    public SharingSettings Sharing { get; init; } = new();

    /// <summary>
    /// The JSON of the document: PascalCase members, each ID as a string, and no member that the
    /// document does not define.
    /// </summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        Converters = { new SnowflakeListConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Reads and examines a document for the guild <paramref name="guildId"/>. The result has the
    /// settings, or the reason for the refusal. A list keeps each ID one time.
    /// </summary>
    public static GuildSettingsResult Parse(string json, ulong guildId)
    {
        GuildSettings? settings;
        try
        {
            var version = JsonNode.Parse(json)?[nameof(SchemaVersion)];
            if (
                version?.GetValueKind() is not JsonValueKind.Number
                || version.GetValue<int>() != CurrentSchemaVersion
            )
            {
                return GuildSettingsResult.Refused(
                    $"{nameof(SchemaVersion)} must be {CurrentSchemaVersion}."
                );
            }

            settings = JsonSerializer.Deserialize<GuildSettings>(json, Json);
        }
        catch (Exception e)
            when (e is JsonException or FormatException or InvalidOperationException)
        {
            return GuildSettingsResult.Refused($"The document is not valid: {e.Message}");
        }

        if (settings is null)
            return GuildSettingsResult.Refused("The document is empty.");

        var lists = new (string Name, IReadOnlyList<ulong> Ids)[]
        {
            (
                $"{nameof(Access)}.{nameof(AccessSettings.ReportRoleIds)}",
                settings.Access.ReportRoleIds
            ),
            ($"{nameof(Access)}.{nameof(AccessSettings.ReadRoleIds)}", settings.Access.ReadRoleIds),
            (
                $"{nameof(Access)}.{nameof(AccessSettings.LabelRoleIds)}",
                settings.Access.LabelRoleIds
            ),
            (
                $"{nameof(Sharing)}.{nameof(SharingSettings.ReadGuildIds)}",
                settings.Sharing.ReadGuildIds
            ),
        };
        foreach (var (name, ids) in lists)
        {
            if (ids.Count > MaxListEntries)
            {
                return GuildSettingsResult.Refused(
                    $"{name} has {ids.Count} entries. The limit is {MaxListEntries}."
                );
            }
        }

        if (settings.Sharing.ReadGuildIds.Contains(guildId))
        {
            return GuildSettingsResult.Refused(
                $"{nameof(Sharing)}.{nameof(SharingSettings.ReadGuildIds)} must not hold this "
                    + "guild."
            );
        }

        return GuildSettingsResult.Accepted(
            settings with
            {
                Access = new AccessSettings
                {
                    ReportRoleIds = [.. settings.Access.ReportRoleIds.Distinct()],
                    ReadRoleIds = [.. settings.Access.ReadRoleIds.Distinct()],
                    LabelRoleIds = [.. settings.Access.LabelRoleIds.Distinct()],
                },
                Sharing = new SharingSettings
                {
                    ReadGuildIds = [.. settings.Sharing.ReadGuildIds.Distinct()],
                },
            }
        );
    }
}

public sealed record AccessSettings
{
    /// <summary>The roles that can report changes: an upload. Empty: each member.</summary>
    public IReadOnlyList<ulong> ReportRoleIds { get; init; } = [];

    /// <summary>The roles that can read the records. Empty: an administrator only.</summary>
    public IReadOnlyList<ulong> ReadRoleIds { get; init; } = [];

    /// <summary>The roles that can change a label. Empty: an administrator only.</summary>
    public IReadOnlyList<ulong> LabelRoleIds { get; init; } = [];
}

public sealed record SharingSettings
{
    /// <summary>The other guilds that can read the records of this guild.</summary>
    public IReadOnlyList<ulong> ReadGuildIds { get; init; } = [];
}

/// <summary>The settings, or the reason why the server refused the document.</summary>
public sealed record GuildSettingsResult(GuildSettings? Settings, string? Refusal)
{
    public static GuildSettingsResult Accepted(GuildSettings settings) => new(settings, null);

    public static GuildSettingsResult Refused(string reason) => new(null, reason);
}

/// <summary>
/// A list of Discord IDs (snowflakes) as strings of 17 to 20 digits. A number is not an ID here,
/// because JSON numbers lose the precision of a snowflake in many clients.
/// </summary>
internal sealed class SnowflakeListConverter : JsonConverter<IReadOnlyList<ulong>>
{
    public override IReadOnlyList<ulong> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("A list of IDs must be an array.");

        var ids = new List<ulong>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (!IsSnowflake(text, out var id))
            {
                throw new JsonException(
                    "Each ID must be a string of 17 to 20 digits. Not an ID: "
                        + Describe(ref reader)
                );
            }
            ids.Add(id);
        }
        return ids;
    }

    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlyList<ulong> value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStartArray();
        foreach (var id in value)
            writer.WriteStringValue(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteEndArray();
    }

    internal static bool IsSnowflake(string? text, out ulong id)
    {
        id = 0;
        return text is { Length: >= 17 and <= 20 }
            && text.All(char.IsAsciiDigit)
            && ulong.TryParse(text, out id);
    }

    private static string Describe(ref Utf8JsonReader reader) =>
        reader.TokenType switch
        {
            JsonTokenType.String => Quote(reader.GetString() ?? ""),
            JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.ValueSpan),
            _ => reader.TokenType.ToString(),
        };

    private static string Quote(string text) =>
        $"\"{(text.Length > 30 ? text[..30] + "..." : text)}\"";
}
