using System.Diagnostics;
using NetCord;
using NetCord.Rest;
using NetCord.Services;
using NetCord.Services.ApplicationCommands;

namespace Custos.Server.Discord;

/// <summary>
/// <c>/custosadmin</c>. <c>DefaultGuildPermissions</c> hides the command from members without the
/// Administrator permission, but a guild administrator can change it. Thus each handler also has
/// the precondition <c>RequireUserPermissions</c>.
/// </summary>
[SlashCommand(
    "custosadmin",
    "Custos administration",
    Contexts = [InteractionContextType.Guild],
    DefaultGuildPermissions = Permissions.Administrator,
    IntegrationTypes = [ApplicationIntegrationType.GuildInstall]
)]
public sealed class AdminCommands : ApplicationCommandModule<ApplicationCommandContext>
{
    [SubSlashCommand("settings", "The settings of Custos for this guild")]
    public sealed class SettingsCommands(
        GuildSettingsStore store,
        SettingsAttachmentReader reader,
        ActivitySource activities
    ) : ApplicationCommandModule<ApplicationCommandContext>
    {
        [RequireUserPermissions<ApplicationCommandContext>(Permissions.Administrator)]
        [SubSlashCommand("view", "Show the settings document, and attach it as a file")]
        public async Task<InteractionMessageProperties> ViewAsync()
        {
            var guildId = Context.Interaction.GuildId!.Value;
            using var activity = SettingsReplies.Start(activities, "view", guildId);
            var settings = await store.GetAsync(guildId, CancellationToken.None);
            return SettingsReplies.Show(guildId, settings, "The settings of this guild.");
        }

        [RequireUserPermissions<ApplicationCommandContext>(Permissions.Administrator)]
        [SubSlashCommand("update", "Replace the full settings document from a file or a form")]
        public async Task UpdateAsync(
            [SlashCommandParameter(Name = "file", Description = "The settings document (JSON)")]
                Attachment? file = null
        )
        {
            var guildId = Context.Interaction.GuildId!.Value;
            using var activity = SettingsReplies.Start(activities, "update", guildId);

            // A modal must be the first response.
            if (file is null)
            {
                var current = await store.GetAsync(guildId, CancellationToken.None);
                await Context.Interaction.SendResponseAsync(
                    InteractionCallback.Modal(SettingsModal.Create(current))
                );
                return;
            }

            await Context.Interaction.SendResponseAsync(
                InteractionCallback.DeferredMessage(MessageFlags.Ephemeral)
            );
            var document = await reader.ReadAsync(file, CancellationToken.None);
            var reply = document.Text is null
                ? SettingsReplies.Refused(document.Refusal!)
                : await SettingsReplies.ApplyAsync(
                    store,
                    Context.Guild,
                    guildId,
                    Context.User.Id,
                    document.Text
                );
            await Context.Interaction.SendFollowupMessageAsync(reply);
        }
    }
}
