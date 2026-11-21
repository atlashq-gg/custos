using System.Diagnostics;
using Custos.Server.Storage;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Custos.Server.Discord;

/// <summary><c>/custos</c>: the commands for the members of a guild.</summary>
[SlashCommand(
    "custos",
    "Custos",
    Contexts = [InteractionContextType.Guild],
    IntegrationTypes = [ApplicationIntegrationType.GuildInstall]
)]
public sealed class LabelCommands(
    StructureLabels labels,
    WarRegistry wars,
    ActivitySource activities
) : ApplicationCommandModule<ApplicationCommandContext>
{
    public const string StructureOption = "structure";

    [RequireGuildAccess<ApplicationCommandContext>(GuildAccess.Label)]
    [SubSlashCommand("label", "Change the label of a structure, or remove it")]
    public async Task<InteractionMessageProperties> LabelAsync(
        [SlashCommandParameter(
            Name = StructureOption,
            Description = "The structure",
            AutocompleteProviderType = typeof(StructureAutocomplete)
        )]
            string structure,
        [SlashCommandParameter(
            Name = "label",
            Description = "The new label. Without it, the generated label shows again.",
            MaxLength = StructureLabels.MaxLength
        )]
            string? label = null
    )
    {
        var guildId = Context.Interaction.GuildId!.Value;
        using var activity = activities.StartActivity("custos label");
        activity?.SetTag("custos.guild_id", guildId.ToString());

        var war = await wars.CurrentAsync(CancellationToken.None);
        var structureId = war is null
            ? null
            : await ResolveAsync((long)guildId, war.WarId, structure);
        if (war is null || structureId is null)
            return Reply("The guild has no structure with this label in the current war.");

        var change = await labels.SetAsync(
            (long)guildId,
            war.WarId,
            structureId.Value,
            label,
            Context.User.Id,
            "discord",
            CancellationToken.None
        );
        return Reply(
            change.Changed
                ? $"The label \"{change.OldLabel}\" is now \"{change.NewLabel}\"."
                : $"The server did not change the label. {change.Refusal}"
        );
    }

    /// <summary>The ID of an autocomplete choice, or a label that a user typed.</summary>
    private async Task<Guid?> ResolveAsync(long guildId, Guid warId, string value)
    {
        if (Guid.TryParse(value, out var id))
            return id;
        var matches = await labels.SearchAsync(guildId, warId, value, 25, CancellationToken.None);
        return matches
            .FirstOrDefault(m =>
                string.Equals(m.Label, value.Trim(), StringComparison.OrdinalIgnoreCase)
            )
            ?.StructureId;
    }

    private static InteractionMessageProperties Reply(string text) =>
        new InteractionMessageProperties()
            .WithContent(text)
            .WithAllowedMentions(AllowedMentionsProperties.None)
            .WithFlags(MessageFlags.Ephemeral);
}

/// <summary>
/// Completes the option <c>structure</c> with the labels of the current war. A member without the
/// access <c>label</c> gets no choices, because the command refuses that member.
/// </summary>
public sealed class StructureAutocomplete(
    StructureLabels labels,
    WarRegistry wars,
    GuildSettingsStore settings
) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    /// <summary>The limits of Discord for a list of choices and for the name of a
    /// choice.</summary>
    private const int MaxChoices = 25;
    private const int MaxNameLength = 100;

    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context
    )
    {
        if (
            context.Interaction.GuildId is not { } guildId
            || !await InteractionAccess.HasAsync(context.Interaction, GuildAccess.Label, settings)
            || await wars.CurrentAsync(CancellationToken.None) is not { } war
        )
        {
            return [];
        }

        var matches = await labels.SearchAsync(
            (long)guildId,
            war.WarId,
            option.Value ?? "",
            MaxChoices,
            CancellationToken.None
        );
        return matches.Select(m => new ApplicationCommandOptionChoiceProperties(
            m.Label.Length > MaxNameLength ? m.Label[..MaxNameLength] : m.Label,
            m.StructureId.ToString()
        ));
    }
}
