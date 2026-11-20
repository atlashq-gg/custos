using System.ComponentModel;
using Custos.Server.Discord;
using Custos.Server.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Custos.Server.Mcp;

public sealed record LabelResult(string StructureId, string? OldLabel, string? NewLabel);

/// <summary>
/// The label tool. It calls the same method of <see cref="StructureLabels"/> as the command
/// <c>/custos label</c>, which holds the rules for a user label. A label change is not destructive,
/// so the tool has no <c>confirm</c> argument.
/// </summary>
[McpServerToolType]
public sealed class LabelTools(
    McpCallContext context,
    IServiceProvider services,
    StructureLabels labels
) : McpTools(context, services)
{
    [McpServerTool(
        Name = "set_structure_label",
        Title = "Set the label of a structure",
        ReadOnly = false,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false
    )]
    [McpGuard(McpScope.Write, GuildAccess.Label)]
    [Description(
        "Change the label of a structure in the records of the guild. Without a label, the "
            + "generated label shows again. A label has 1 to 64 characters: letters, digits, "
            + "spaces, and - ' . , ( ) / & +. It is unique in the guild and the war."
    )]
    public Task<CallToolResult> SetStructureLabel(
        [Description("The ID of the Discord guild.")] string guild_id,
        [Description("The ID of the structure, from find_structures.")] string structure_id,
        [Description("The new label. Empty or absent: the generated label shows again.")]
            string? label = null,
        CancellationToken cancellationToken = default
    ) => RunAsync("set_structure_label", async () =>
        {
            var guildId = (long)Context.GuildId!.Value;
            // Only a record of the named guild changes: a structure of a guild that shares its
            // records is not found here.
            if (
                !Guid.TryParse(structure_id, out var structureId)
                || await labels.FindWarAsync(guildId, structureId, cancellationToken)
                    is not { } warId
            )
            {
                throw new McpDeniedException(McpProblem.NotVisible());
            }

            var change = await labels.SetAsync(
                guildId,
                warId,
                structureId,
                label,
                Context.CallerId!.Value,
                "mcp",
                cancellationToken
            );
            return change.Changed
                ? new LabelResult(structure_id, change.OldLabel, change.NewLabel)
                : throw new McpException(change.Refusal!);
        });
}

/// <summary>A refusal that a tool finds after its guard. <see cref="McpTools"/> gives it as a
/// problem body.</summary>
public sealed class McpDeniedException(McpProblem problem) : Exception(problem.Title)
{
    public McpProblem Problem => problem;
}
