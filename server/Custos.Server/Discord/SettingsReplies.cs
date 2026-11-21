using System.Diagnostics;
using System.Text;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

namespace Custos.Server.Discord;

/// <summary>The replies of the settings commands. Each reply is ephemeral.</summary>
internal static class SettingsReplies
{
    /// <summary>The limit of Discord for the content of a message, with space for the
    /// text.</summary>
    private const int MaxDocumentLength = 1800;

    public static Activity? Start(ActivitySource activities, string command, ulong guildId)
    {
        var activity = activities.StartActivity($"custosadmin settings {command}");
        activity?.SetTag("custos.guild_id", guildId.ToString());
        return activity;
    }

    /// <summary>Examines and saves a document. The reply names each role that the guild does not
    /// have.</summary>
    public static async Task<InteractionMessageProperties> ApplyAsync(
        GuildSettingsStore store,
        Guild? guild,
        ulong guildId,
        ulong userId,
        string json
    )
    {
        var result = GuildSettings.Parse(json, guildId);
        if (result.Settings is not { } settings)
            return Refused(result.Refusal!);

        await store.SaveAsync(guildId, settings, userId, "discord", CancellationToken.None);

        var text = new StringBuilder("The server saved the settings.");
        var access = settings.Access;
        var unknown = guild is null
            ? []
            : access
                .ReportRoleIds.Concat(access.ReadRoleIds)
                .Concat(access.LabelRoleIds)
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
        return new InteractionMessageProperties()
            .WithContent(content)
            .WithAttachments([
                new AttachmentProperties(
                    $"custos-settings-{guildId}.json",
                    new MemoryStream(Encoding.UTF8.GetBytes(json))
                ),
            ])
            .WithAllowedMentions(AllowedMentionsProperties.None)
            .WithFlags(MessageFlags.Ephemeral);
    }

    public static InteractionMessageProperties Refused(string reason) =>
        new InteractionMessageProperties()
            .WithContent($"The server did not save the settings. {reason}")
            .WithAllowedMentions(AllowedMentionsProperties.None)
            .WithFlags(MessageFlags.Ephemeral);
}
