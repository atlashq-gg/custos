namespace Custos.Server.Discord;

/// <summary>The result of an access check: the status of the member, and the access.</summary>
internal sealed record AccessCheck(MemberStatus Status, GuildAccess Access)
{
    public bool Has(GuildAccess access) => (Access & access) == access;
}

/// <summary>
/// The access of a user in a guild, from the current roles in Discord and the settings of the
/// guild. Scoped: it asks Discord one time for each guild and user in one request, and keeps no
/// answer between requests, so a role change applies to the next request.
/// </summary>
internal sealed class GuildAccessEvaluator(IDiscordDirectory directory, GuildSettingsStore settings)
{
    private readonly Dictionary<(ulong Guild, ulong User), AccessCheck> _checks = [];

    /// <summary>Throws <see cref="DiscordUnavailableException"/>.</summary>
    public async Task<AccessCheck> EvaluateAsync(
        ulong guildId,
        ulong userId,
        CancellationToken cancellationToken
    )
    {
        if (_checks.TryGetValue((guildId, userId), out var known))
            return known;

        var member = await directory.GetMemberAsync(guildId, userId, cancellationToken);
        var check = member.Facts is null
            ? new AccessCheck(member.Status, GuildAccess.None)
            : new AccessCheck(
                member.Status,
                AccessRule.Evaluate(
                    member.Facts,
                    await settings.GetAsync(guildId, cancellationToken)
                )
            );
        _checks[(guildId, userId)] = check;
        return check;
    }
}
