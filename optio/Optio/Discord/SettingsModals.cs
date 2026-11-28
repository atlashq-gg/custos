using NetCord;
using NetCord.Rest;
using NetCord.Services;
using NetCord.Services.ComponentInteractions;
using Optio.Storage;

namespace Optio.Discord;

/// <summary>The form of <c>/optioadmin settings update</c> without a file.</summary>
public sealed class SettingsModal(GuildSettingsStore store)
    : ComponentInteractionModule<ModalInteractionContext>
{
    public const string CustomId = "optio-settings";
    private const string DocumentId = "document";

    /// <summary>The limit of Discord for the value of a text input.</summary>
    private const int MaxLength = 4000;

    internal static ModalProperties Create(GuildSettings current) =>
        new ModalProperties(CustomId, "Optio settings").AddComponents(
            new LabelProperties(
                "Settings document (JSON)",
                new TextInputProperties(DocumentId, TextInputStyle.Paragraph)
                    .WithMaxLength(MaxLength)
                    .WithValue(current.ToJson() is { Length: <= MaxLength } json ? json : null)
            )
        );

    [RequireUserPermissions<ModalInteractionContext>(Permissions.Administrator)]
    [ComponentInteraction(CustomId)]
    public async Task<InteractionMessageProperties> SubmitAsync()
    {
        var text = Context.Value(DocumentId);
        if (string.IsNullOrWhiteSpace(text))
            return SettingsReplies.Refused("The form has no document.");

        return await SettingsReplies.ApplyAsync(
            store,
            Context.Guild,
            Context.Interaction.GuildId!.Value,
            Context.User.Id,
            text
        );
    }
}

/// <summary>
/// The form of <c>/optioadmin prompt</c>. The custom ID holds the role, or 0 for the text of the
/// guild. An empty form removes the text.
/// </summary>
public sealed class PromptModal(GuildSettingsStore store)
    : ComponentInteractionModule<ModalInteractionContext>
{
    public const string CustomId = "optio-prompt";
    private const string TextId = "text";

    internal static ModalProperties Create(Role? role, string? current) =>
        new ModalProperties(
            $"{CustomId}:{role?.Id ?? 0}",
            role is null ? "Text of the guild" : $"Text of {Short(role.Name)}"
        ).AddComponents(
            new LabelProperties(
                role is null ? "For each question in this guild" : "For each question of a member",
                new TextInputProperties(TextId, TextInputStyle.Paragraph)
                    .WithRequired(false)
                    .WithMaxLength(
                        role is null
                            ? GuildSettings.MaxGuildPromptLength
                            : GuildSettings.MaxRolePromptLength
                    )
                    .WithPlaceholder("Instructions for the answers of Optio")
                    .WithValue(current)
            )
        );

    [RequireUserPermissions<ModalInteractionContext>(Permissions.Administrator)]
    [ComponentInteraction(CustomId)]
    public async Task<InteractionMessageProperties> SubmitAsync(ulong roleId)
    {
        var text = Context.Value(TextId);
        var result = await store.UpdateAsync(
            Context.Interaction.GuildId!.Value,
            s => s with { Prompts = Change(s.Prompts, roleId, text) },
            Context.User.Id,
            default
        );
        if (result.Settings is not { } settings)
            return SettingsReplies.Refused(result.Refusal!);

        var saved =
            roleId == 0 ? settings.Prompts.Guild : settings.Prompts.Roles.GetValueOrDefault(roleId);
        var owner = roleId == 0 ? "The guild" : $"The role <@&{roleId}>";
        return SettingsReplies.Text(
            saved is null
                ? $"{owner} has no text now."
                : $"{owner} has a text of {saved.Length} characters now."
        );
    }

    private static PromptSettings Change(PromptSettings prompts, ulong roleId, string? text)
    {
        if (roleId == 0)
            return prompts with { Guild = text };

        var roles = prompts.Roles.ToDictionary();
        roles[roleId] = text ?? "";
        return prompts with { Roles = roles };
    }

    /// <summary>The title of a modal has 45 characters at most.</summary>
    private static string Short(string name) => name.Length <= 30 ? name : name[..30];
}

internal static class ModalValues
{
    /// <summary>The value of the text input <paramref name="id"/> of a modal.</summary>
    public static string? Value(this ModalInteractionContext context, string id) =>
        context
            .Components.OfType<Label>()
            .Select(l => l.Component)
            .OfType<TextInput>()
            .FirstOrDefault(i => i.CustomId == id)
            ?.Value;
}
