using System.Globalization;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Rest;
using NetCord.Services;
using NetCord.Services.ApplicationCommands;
using Optio.Storage;

namespace Optio.Discord;

/// <summary>
/// <c>/optioctl</c>, the commands of the operators of Optio. Optio registers it only in the
/// operator guild (<see cref="OptioOptions.OperatorGuildId"/>). Each handler has the
/// precondition <see cref="RequireOperatorAttribute"/>.
/// </summary>
[SlashCommand(
    Name,
    "Optio operation",
    Contexts = [InteractionContextType.Guild],
    IntegrationTypes = [ApplicationIntegrationType.GuildInstall]
)]
public sealed class OperatorCommands : ApplicationCommandModule<ApplicationCommandContext>
{
    public const string Name = "optioctl";

    [SubSlashCommand("limit", "The tokens in one hour for a guild or a user")]
    public sealed class LimitCommands(OperatorLimitStore store)
        : ApplicationCommandModule<ApplicationCommandContext>
    {
        [RequireOperator]
        [SubSlashCommand("guild", "Set the pool of a guild: the tokens of all its members")]
        public Task<InteractionMessageProperties> GuildAsync(
            [SlashCommandParameter(Name = "guild_id", Description = "The ID of the guild")]
                string guildId,
            [SlashCommandParameter(
                Name = "tokens",
                Description = "The tokens in one hour. No value sets the default.",
                MinValue = 1
            )]
                long? tokens = null
        ) => SetAsync(OperatorScope.Guild, guildId, tokens);

        [RequireOperator]
        [SubSlashCommand("user", "Set the cap of a user: the tokens of the user in all guilds")]
        public Task<InteractionMessageProperties> UserAsync(
            [SlashCommandParameter(Name = "user_id", Description = "The ID of the user")]
                string userId,
            [SlashCommandParameter(
                Name = "tokens",
                Description = "The tokens in one hour. No value sets the default.",
                MinValue = 1
            )]
                long? tokens = null
        ) => SetAsync(OperatorScope.User, userId, tokens);

        private async Task<InteractionMessageProperties> SetAsync(
            OperatorScope scope,
            string id,
            long? tokens
        )
        {
            // A Discord integer option stops at 2^53, so the ID is a string.
            if (
                !ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var target)
            )
                return SettingsReplies.Text($"{id} is not a Discord ID.");

            await store.SetAsync(scope, target, tokens, Context.User.Id, CancellationToken.None);
            var limit =
                scope == OperatorScope.Guild ? $"pool of guild {target}" : $"cap of <@{target}>";
            return SettingsReplies.Text(
                tokens is { } value
                    ? $"The {limit} is {value.ToString("N0", CultureInfo.InvariantCulture)} "
                        + "tokens in one hour."
                    : $"The {limit} is the default of the configuration."
            );
        }
    }
}

/// <summary>Only a member of the operator guild with an operator role
/// (<see cref="OptioOptions.OperatorRoleIds"/>) can use the command.</summary>
public sealed class RequireOperatorAttribute : PreconditionAttribute<ApplicationCommandContext>
{
    public override ValueTask<PreconditionResult> EnsureCanExecuteAsync(
        ApplicationCommandContext context,
        IServiceProvider? serviceProvider
    )
    {
        var options = serviceProvider!.GetRequiredService<IOptions<OptioOptions>>().Value;
        var isOperator = IsOperator(
            options,
            context.Interaction.GuildId,
            (context.User as GuildInteractionUser)?.RoleIds ?? []
        );
        return ValueTask.FromResult<PreconditionResult>(
            isOperator
                ? PreconditionResult.Success
                : PreconditionResult.Fail("Only an operator of Optio can use this command.")
        );
    }

    /// <summary>True in the operator guild for a member with an operator role.</summary>
    internal static bool IsOperator(OptioOptions options, ulong? guild, IEnumerable<ulong> roles) =>
        guild is { } id
        && (long)id == options.OperatorGuildId
        && roles.Any(role => options.OperatorRoleIds.Contains((long)role));
}
