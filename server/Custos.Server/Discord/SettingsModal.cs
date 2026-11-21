using System.Diagnostics;
using NetCord;
using NetCord.Rest;
using NetCord.Services;
using NetCord.Services.ComponentInteractions;

namespace Custos.Server.Discord;

/// <summary>The form of <c>/custosadmin settings update</c> without a file.</summary>
public sealed class SettingsModal(GuildSettingsStore store, ActivitySource activities)
    : ComponentInteractionModule<ModalInteractionContext>
{
    public const string CustomId = "custos-settings";
    private const string DocumentId = "document";

    /// <summary>The limit of Discord for the value of a text input.</summary>
    private const int MaxLength = 4000;

    internal static ModalProperties Create(GuildSettings current) =>
        new ModalProperties(CustomId, "Custos settings").AddComponents(
            new LabelProperties(
                "Settings document (JSON)",
                new TextInputProperties(DocumentId, TextInputStyle.Paragraph)
                    .WithMaxLength(MaxLength)
                    .WithValue(current.ToJson())
            )
        );

    [RequireUserPermissions<ModalInteractionContext>(Permissions.Administrator)]
    [ComponentInteraction(CustomId)]
    public async Task<InteractionMessageProperties> SubmitAsync()
    {
        var guildId = Context.Interaction.GuildId!.Value;
        using var activity = SettingsReplies.Start(activities, "update (form)", guildId);
        var text = Context
            .Components.OfType<Label>()
            .Select(l => l.Component)
            .OfType<TextInput>()
            .FirstOrDefault(i => i.CustomId == DocumentId)
            ?.Value;
        if (string.IsNullOrWhiteSpace(text))
            return SettingsReplies.Refused("The form has no document.");

        return await SettingsReplies.ApplyAsync(
            store,
            Context.Guild,
            guildId,
            Context.User.Id,
            text
        );
    }
}
