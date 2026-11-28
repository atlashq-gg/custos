using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Optio.Storage;

/// <summary>
/// The settings document of Optio for a guild. Its members are in PascalCase and each ID is a
/// string, as <c>/optioadmin settings view</c> shows it. A guild without settings has
/// <see cref="Default"/>.
/// </summary>
public sealed record GuildSettings
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The largest number of entries in a list or a map of roles.</summary>
    public const int MaxRoleEntries = 25;

    public const int MaxGuildPromptLength = 1000;
    public const int MaxRolePromptLength = 500;

    public static readonly GuildSettings Default = new();

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public AccessSettings Access { get; init; } = new();
    public PromptSettings Prompts { get; init; } = new();
    public LimitSettings Limits { get; init; } = new();

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

    /// <summary>Reads and examines a document. The result has the settings, or the reason for
    /// the refusal.</summary>
    public static GuildSettingsResult Parse(string json)
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

        return settings is null
            ? GuildSettingsResult.Refused("The document is empty.")
            : settings.Check();
    }

    /// <summary>
    /// Examines the limits of the document, and gives it in the form that Optio keeps: each list
    /// holds an ID one time, and a text of only white space is no text.
    /// </summary>
    public GuildSettingsResult Check()
    {
        var ids = Access
            .QueryRoleIds.Concat(Prompts.Roles.Keys)
            .Concat(Limits.Roles.Keys)
            .Concat(Limits.Users.Keys);
        if (
            ids.Where(id => !SnowflakeListConverter.IsSnowflake(id))
                .Select(id => (ulong?)id)
                .FirstOrDefault() is
            { } wrong
        )
        {
            return GuildSettingsResult.Refused($"{wrong} is not a Discord ID.");
        }

        var queryRoleIds = Access.QueryRoleIds.Distinct().ToList();
        var guildPrompt = Text(Prompts.Guild);
        var rolePrompts = Prompts
            .Roles.Select(p => (p.Key, Text: Text(p.Value)))
            .Where(p => p.Text is not null)
            .ToDictionary(p => p.Key, p => p.Text!);

        var refusal = Entries("Access.QueryRoleIds", queryRoleIds.Count)
            ?? Entries("Prompts.Roles", rolePrompts.Count)
            ?? Entries("Limits.Roles", Limits.Roles.Count)
            ?? (
                guildPrompt?.Length > MaxGuildPromptLength
                    ? $"Prompts.Guild has more than {MaxGuildPromptLength} characters."
                    : null
            )
            ?? rolePrompts
                .Where(p => p.Value.Length > MaxRolePromptLength)
                .Select(p =>
                    $"The prompt of role {p.Key} has more than {MaxRolePromptLength} characters."
                )
                .FirstOrDefault()
            ?? Limits
                .Roles.Concat(Limits.Users)
                .Where(l => l.Value < 1)
                .Select(l => $"The limit of {l.Key} must be 1 token or more.")
                .FirstOrDefault();
        if (refusal is not null)
            return GuildSettingsResult.Refused(refusal);

        return GuildSettingsResult.Accepted(
            this with
            {
                Access = new AccessSettings { QueryRoleIds = queryRoleIds },
                Prompts = new PromptSettings { Guild = guildPrompt, Roles = rolePrompts },
            }
        );
    }

    private static string? Text(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? Entries(string name, int count) =>
        count > MaxRoleEntries
            ? $"{name} has {count} entries. The limit is {MaxRoleEntries}."
            : null;
}

public sealed record AccessSettings
{
    /// <summary>The roles that can ask Optio a question. Empty: an administrator only.</summary>
    public IReadOnlyList<ulong> QueryRoleIds { get; init; } = [];
}

public sealed record PromptSettings
{
    /// <summary>The text of the guild for the system prompt. Null: no text.</summary>
    public string? Guild { get; init; }

    /// <summary>The text of each role for the system prompt, by role ID.</summary>
    public IReadOnlyDictionary<ulong, string> Roles { get; init; } =
        new Dictionary<ulong, string>();
}

public sealed record LimitSettings
{
    /// <summary>The tokens in one hour for a member with the role, by role ID.</summary>
    public IReadOnlyDictionary<ulong, long> Roles { get; init; } = new Dictionary<ulong, long>();

    /// <summary>The tokens in one hour for one member, by user ID. It replaces the limits of the
    /// roles of the member.</summary>
    public IReadOnlyDictionary<ulong, long> Users { get; init; } = new Dictionary<ulong, long>();
}

/// <summary>The settings, or the reason why Optio refused them.</summary>
public sealed record GuildSettingsResult(GuildSettings? Settings, string? Refusal)
{
    public static GuildSettingsResult Accepted(GuildSettings settings) => new(settings, null);

    public static GuildSettingsResult Refused(string reason) => new(null, reason);
}

/// <summary>
/// A list of Discord IDs (snowflakes) as strings of 17 to 20 digits. A number is not an ID here,
/// because JSON numbers lose the precision of a snowflake in many clients. The keys of a map are
/// strings in JSON already.
/// </summary>
internal sealed class SnowflakeListConverter : JsonConverter<IReadOnlyList<ulong>>
{
    /// <summary>The smallest ID of 17 digits.</summary>
    private const ulong MinSnowflake = 10_000_000_000_000_000;

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
            if (
                text is null
                || !text.All(char.IsAsciiDigit)
                || !ulong.TryParse(text, CultureInfo.InvariantCulture, out var id)
            )
            {
                throw new JsonException(
                    "Each ID must be a string of 17 to 20 digits. Not an ID: "
                        + (text ?? reader.TokenType.ToString())
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
            writer.WriteStringValue(id.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndArray();
    }

    internal static bool IsSnowflake(ulong id) => id >= MinSnowflake;
}
