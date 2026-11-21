using NetCord;
using NetCord.Services;

namespace Custos.Server.Discord;

/// <summary>
/// Passes a member with the access <paramref name="access"/> in the guild of the interaction. The
/// interaction holds the roles and the permissions of the member, so the guard needs no request
/// to Discord. The settings of the guild come from the database.
/// </summary>
public sealed class RequireGuildAccessAttribute<TContext>(GuildAccess access)
    : PreconditionAttribute<TContext>
    where TContext : IInteractionContext
{
    public GuildAccess Access => access;

    public override async ValueTask<PreconditionResult> EnsureCanExecuteAsync(
        TContext context,
        IServiceProvider? serviceProvider
    )
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return await InteractionAccess.HasAsync(
            context.Interaction,
            access,
            serviceProvider.GetRequiredService<GuildSettingsStore>()
        )
            ? PreconditionResult.Success
            : PreconditionResult.Fail(
                $"You need the access {access.ToString().ToLowerInvariant()}."
            );
    }
}

/// <summary>The access of the member of an interaction.</summary>
internal static class InteractionAccess
{
    /// <summary>The facts of the member, or <c>null</c> outside a guild.</summary>
    public static MemberFacts? Member(Interaction interaction) =>
        interaction.User is GuildInteractionUser member
            ? new MemberFacts(
                [.. member.RoleIds],
                member.Permissions.HasFlag(Permissions.Administrator)
                    || interaction.Guild?.OwnerId == member.Id
            )
            : null;

    public static async Task<bool> HasAsync(
        Interaction interaction,
        GuildAccess access,
        GuildSettingsStore settings
    )
    {
        if (interaction.GuildId is not { } guildId || Member(interaction) is not { } member)
            return false;
        var granted = AccessRule.Evaluate(
            member,
            await settings.GetAsync(guildId, CancellationToken.None)
        );
        return (granted & access) == access;
    }
}
