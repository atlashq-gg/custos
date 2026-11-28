using System.Globalization;
using NetCord;
using NetCord.Rest;
using NetCord.Services;
using NetCord.Services.ApplicationCommands;
using Optio.Storage;

namespace Optio.Discord;

/// <summary>
/// <c>/optioadmin</c>. <c>DefaultGuildPermissions</c> hides the command from members without the
/// Administrator permission, but a guild administrator can change it. Thus each handler also has
/// the precondition <c>RequireUserPermissions</c>.
/// </summary>
[SlashCommand(
    "optioadmin",
    "Optio administration",
    Contexts = [InteractionContextType.Guild],
    DefaultGuildPermissions = Permissions.Administrator,
    IntegrationTypes = [ApplicationIntegrationType.GuildInstall]
)]
public sealed class AdminCommands : ApplicationCommandModule<ApplicationCommandContext>
{
    [SubSlashCommand("settings", "The settings document of Optio for this guild")]
    public sealed class SettingsCommands(GuildSettingsStore store, SettingsAttachmentReader reader)
        : ApplicationCommandModule<ApplicationCommandContext>
    {
        [RequireUserPermissions<ApplicationCommandContext>(Permissions.Administrator)]
        [SubSlashCommand("view", "Show the settings document, and attach it as a file")]
        public async Task<InteractionMessageProperties> ViewAsync()
        {
            var guildId = Context.Interaction.GuildId!.Value;
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

    [SubSlashCommand("prompt", "The text of the guild or of a role for the answers of Optio")]
    public sealed class PromptCommands(GuildSettingsStore store)
        : ApplicationCommandModule<ApplicationCommandContext>
    {
        [RequireUserPermissions<ApplicationCommandContext>(Permissions.Administrator)]
        [SubSlashCommand("guild", "Change the text for each question in this guild")]
        public async Task GuildAsync()
        {
            var current = await store.GetAsync(Context.Interaction.GuildId!.Value, default);
            await Context.Interaction.SendResponseAsync(
                InteractionCallback.Modal(PromptModal.Create(null, current.Prompts.Guild))
            );
        }

        [RequireUserPermissions<ApplicationCommandContext>(Permissions.Administrator)]
        [SubSlashCommand("role", "Change the text for each question of a member with a role")]
        public async Task RoleAsync(
            [SlashCommandParameter(Name = "role", Description = "The role")] Role role
        )
        {
            var current = await store.GetAsync(Context.Interaction.GuildId!.Value, default);
            await Context.Interaction.SendResponseAsync(
                InteractionCallback.Modal(
                    PromptModal.Create(role, current.Prompts.Roles.GetValueOrDefault(role.Id))
                )
            );
        }
    }

    [SubSlashCommand("access", "The roles that can ask Optio a question")]
    public sealed class AccessCommands(GuildSettingsStore store)
        : ApplicationCommandModule<ApplicationCommandContext>
    {
        [RequireUserPermissions<ApplicationCommandContext>(Permissions.Administrator)]
        [SubSlashCommand("add", "Let the members with a role ask Optio a question")]
        public Task<InteractionMessageProperties> AddAsync(
            [SlashCommandParameter(Name = "role", Description = "The role")] Role role
        ) =>
            SettingsReplies.UpdateAsync(
                store,
                Context,
                s =>
                    s with
                    {
                        Access = new() { QueryRoleIds = [.. s.Access.QueryRoleIds, role.Id] },
                    },
                $"Members with the role <@&{role.Id}> can ask Optio a question."
            );

        [RequireUserPermissions<ApplicationCommandContext>(Permissions.Administrator)]
        [SubSlashCommand("remove", "Stop the questions of the members with a role")]
        public Task<InteractionMessageProperties> RemoveAsync(
            [SlashCommandParameter(Name = "role", Description = "The role")] Role role
        ) =>
            SettingsReplies.UpdateAsync(
                store,
                Context,
                s =>
                    s with
                    {
                        Access = new()
                        {
                            QueryRoleIds = [.. s.Access.QueryRoleIds.Where(id => id != role.Id)],
                        },
                    },
                $"The role <@&{role.Id}> gives no access to the questions."
            );
    }

    [SubSlashCommand("limit", "The tokens in one hour for a role or a member")]
    public sealed class LimitCommands(GuildSettingsStore store)
        : ApplicationCommandModule<ApplicationCommandContext>
    {
        [RequireUserPermissions<ApplicationCommandContext>(Permissions.Administrator)]
        [SubSlashCommand("role", "Set the tokens in one hour for a member with a role")]
        public Task<InteractionMessageProperties> RoleAsync(
            [SlashCommandParameter(Name = "role", Description = "The role")] Role role,
            [SlashCommandParameter(
                Name = "tokens",
                Description = "The tokens in one hour. No value removes the limit.",
                MinValue = 1
            )]
                long? tokens = null
        ) =>
            SettingsReplies.UpdateAsync(
                store,
                Context,
                s =>
                    s with
                    {
                        Limits = s.Limits with { Roles = With(s.Limits.Roles, role.Id, tokens) },
                    },
                Limit($"<@&{role.Id}>", tokens)
            );

        [RequireUserPermissions<ApplicationCommandContext>(Permissions.Administrator)]
        [SubSlashCommand("user", "Set the tokens in one hour for one member")]
        public Task<InteractionMessageProperties> UserAsync(
            [SlashCommandParameter(Name = "user", Description = "The member")] User user,
            [SlashCommandParameter(
                Name = "tokens",
                Description = "The tokens in one hour. No value removes the limit.",
                MinValue = 1
            )]
                long? tokens = null
        ) =>
            SettingsReplies.UpdateAsync(
                store,
                Context,
                s =>
                    s with
                    {
                        Limits = s.Limits with { Users = With(s.Limits.Users, user.Id, tokens) },
                    },
                Limit($"<@{user.Id}>", tokens)
            );

        private static Dictionary<ulong, long> With(
            IReadOnlyDictionary<ulong, long> limits,
            ulong id,
            long? tokens
        )
        {
            var changed = limits.ToDictionary();
            if (tokens is { } value)
                changed[id] = value;
            else
                changed.Remove(id);
            return changed;
        }

        private static string Limit(string who, long? tokens) =>
            tokens is { } value
                ? $"The limit of {who} is "
                    + $"{value.ToString("N0", CultureInfo.InvariantCulture)} tokens in one hour."
                : $"{who} has no limit of its own.";
    }
}
