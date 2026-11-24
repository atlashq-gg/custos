using NetCord;
using NetCord.Rest;
using NetCord.Services;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;
using Optio.Storage;

namespace Optio.Discord;

/// <summary>
/// <c>/optioadmin</c>. <c>DefaultGuildPermissions</c> hides the command from members without the
/// Administrator permission, but a guild administrator can change it. Thus each handler also has
/// the precondition <c>RequireUserPermissions</c>.
/// </summary>
[SlashCommand(
    "optioadmin",
    "optio administration",
    Contexts = [InteractionContextType.Guild],
    DefaultGuildPermissions = Permissions.Administrator,
    IntegrationTypes = [ApplicationIntegrationType.GuildInstall]
)]
public sealed class AdminCommands(GuildSettingsStore store)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    /// <summary>Opens the form with the settings of now. Close it to change nothing.</summary>
    [RequireUserPermissions<ApplicationCommandContext>(Permissions.Administrator)]
    [SubSlashCommand("settings", "Show and change the settings of optio for this guild")]
    public async Task SettingsAsync()
    {
        var current = await store.GetAsync(Context.Interaction.GuildId!.Value, default);
        await Context.Interaction.SendResponseAsync(
            InteractionCallback.Modal(SettingsModal.Create(current))
        );
    }
}

/// <summary>The form of <c>/optioadmin settings</c>.</summary>
public sealed class SettingsModal(GuildSettingsStore store)
    : ComponentInteractionModule<ModalInteractionContext>
{
    public const string CustomId = "optio-settings";
    private const string PromptId = "prompt";

    internal static ModalProperties Create(GuildSettings current) =>
        new ModalProperties(CustomId, "optio settings").AddComponents(
            new LabelProperties(
                "Prompt of the guild",
                new TextInputProperties(PromptId, TextInputStyle.Paragraph)
                    .WithRequired(false)
                    .WithMaxLength(GuildSettings.MaxPromptLength)
                    .WithPlaceholder("Instructions for the answers to the queries of this guild")
                    .WithValue(current.Prompt)
            )
        );

    [RequireUserPermissions<ModalInteractionContext>(Permissions.Administrator)]
    [ComponentInteraction(CustomId)]
    public async Task SubmitAsync()
    {
        var prompt = Context
            .Components.OfType<Label>()
            .Select(l => l.Component)
            .OfType<TextInput>()
            .FirstOrDefault(i => i.CustomId == PromptId)
            ?.Value;
        string reply;
        if (prompt?.Trim().Length > GuildSettings.MaxPromptLength)
        {
            reply = $"The prompt has more than {GuildSettings.MaxPromptLength} characters.";
        }
        else
        {
            await store.SaveAsync(
                Context.Interaction.GuildId!.Value,
                new GuildSettings(prompt),
                Context.User.Id,
                default
            );
            reply = string.IsNullOrWhiteSpace(prompt)
                ? "The guild has no prompt now."
                : $"The prompt of the guild has {prompt.Trim().Length} characters now.";
        }

        await Context.Interaction.SendResponseAsync(
            InteractionCallback.Message(
                new InteractionMessageProperties { Content = reply, Flags = MessageFlags.Ephemeral }
            )
        );
    }
}
