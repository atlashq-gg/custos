using System.Text;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using Optio.Storage;

namespace Optio.Discord;

/// <summary>The replies of the settings commands. Each reply is ephemeral and mentions no
/// one.</summary>
internal static class SettingsReplies
{
    /// <summary>The limit of Discord for the content of a message, with space for the
    /// text.</summary>
    private const int MaxDocumentLength = 1800;

    /// <summary>Examines and saves a full document. The reply names each role that the guild does
    /// not have.</summary>
    public static async Task<InteractionMessageProperties> ApplyAsync(
        GuildSettingsStore store,
        Guild? guild,
        ulong guildId,
        ulong userId,
        string json
    )
    {
        var parsed = GuildSettings.Parse(json);
        if (parsed.Settings is not { } document)
            return Refused(parsed.Refusal!);

        var result = await store.UpdateAsync(guildId, _ => document, userId, default);
        if (result.Settings is not { } settings)
            return Refused(result.Refusal!);

        var text = new StringBuilder("Optio saved the settings.");
        var unknown = guild is null
            ? []
            : settings
                .Access.QueryRoleIds.Concat(settings.Prompts.Roles.Keys)
                .Concat(settings.Limits.Roles.Keys)
                .Distinct()
                .Where(id => !guild.Roles.ContainsKey(id))
                .ToList();
        if (unknown.Count > 0)
        {
            text.Append(" The guild has no role with these IDs: ")
                .AppendJoin(", ", unknown)
                .Append(". The settings keep them.");
        }
        return Show(guildId, settings, text.ToString());
    }

    /// <summary>Applies one change of a focused command, and replies with
    /// <paramref name="done"/>.</summary>
    public static async Task<InteractionMessageProperties> UpdateAsync(
        GuildSettingsStore store,
        ApplicationCommandContext context,
        Func<GuildSettings, GuildSettings> change,
        string done
    )
    {
        var result = await store.UpdateAsync(
            context.Interaction.GuildId!.Value,
            change,
            context.User.Id,
            default
        );
        return result.Settings is null ? Refused(result.Refusal!) : Text(done);
    }

    /// <summary>The document as text, and as a file.</summary>
    public static InteractionMessageProperties Show(
        ulong guildId,
        GuildSettings settings,
        string text
    )
    {
        var json = settings.ToJson();
        var content =
            json.Length <= MaxDocumentLength
                ? $"{text}\n```json\n{json}\n```"
                : $"{text} The document is in the file.";
        return Text(content)
            .WithAttachments([
                new AttachmentProperties(
                    $"optio-settings-{guildId}.json",
                    new MemoryStream(Encoding.UTF8.GetBytes(json))
                ),
            ]);
    }

    public static InteractionMessageProperties Refused(string reason) =>
        Text($"Optio did not save the settings. {reason}");

    public static InteractionMessageProperties Text(string content) =>
        new InteractionMessageProperties()
            .WithContent(content)
            .WithAllowedMentions(AllowedMentionsProperties.None)
            .WithFlags(MessageFlags.Ephemeral);
}
