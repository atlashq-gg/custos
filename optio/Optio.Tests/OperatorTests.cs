using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetCord.Services.ApplicationCommands;
using Optio.Discord;
using Optio.Storage;
using Optio.Tests.Infrastructure;

namespace Optio.Tests;

/// <summary>The operator limits and <c>/optioctl</c>. Needs Docker for PostgreSQL.</summary>
public sealed class OperatorTests
{
    private const ulong OperatorGuild = 300000000000000009;
    private const ulong OperatorRole = 500000000000000009;

    [Test]
    public async Task Store_sets_replaces_and_removes_a_limit()
    {
        using var factory = new OptioFactory(
            database: true,
            settings: new()
            {
                ["Optio:GuildTokensPerHour"] = "100",
                ["Optio:UserTokensPerHour"] = "50",
            }
        );
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<OperatorLimitStore>();
        const ulong guild = FakeMembers.Guild;
        const ulong user = FakeMembers.Reader;

        var defaults = await store.GetAsync(guild, user, default);
        await store.SetAsync(OperatorScope.Guild, guild, 1000, FakeMembers.Owner, default);
        await store.SetAsync(OperatorScope.Guild, guild, 2000, FakeMembers.Owner, default);
        await store.SetAsync(OperatorScope.User, user, 500, FakeMembers.Owner, default);
        var set = await store.GetAsync(guild, user, default);
        var other = await store.GetAsync(FakeMembers.OtherGuild, FakeMembers.Owner, default);
        await store.SetAsync(OperatorScope.Guild, guild, null, FakeMembers.Owner, default);
        var removed = await store.GetAsync(guild, user, default);
        var rows = await scope
            .ServiceProvider.GetRequiredService<OptioDbContext>()
            .OperatorLimits.AsNoTracking()
            .ToListAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(defaults, Is.EqualTo(new OperatorLimits(100, 50)));
            Assert.That(set, Is.EqualTo(new OperatorLimits(2000, 500)));
            Assert.That(other, Is.EqualTo(new OperatorLimits(100, 50)));
            Assert.That(removed, Is.EqualTo(new OperatorLimits(100, 500)));
            Assert.That(
                rows.Select(r => (r.Scope, r.TargetId)),
                Is.EqualTo(new[] { (OperatorScope.User, (long)user) })
            );
        }
    }

    [Test]
    public void Operator_is_a_member_of_the_operator_guild_with_an_operator_role()
    {
        var options = new OptioOptions
        {
            OperatorGuildId = (long)OperatorGuild,
            OperatorRoleIds = [(long)OperatorRole],
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                RequireOperatorAttribute.IsOperator(options, OperatorGuild, [OperatorRole]),
                Is.True
            );
            Assert.That(
                RequireOperatorAttribute.IsOperator(options, OperatorGuild, [FakeMembers.ReadRole]),
                Is.False
            );
            Assert.That(
                RequireOperatorAttribute.IsOperator(options, FakeMembers.Guild, [OperatorRole]),
                Is.False
            );
            Assert.That(
                RequireOperatorAttribute.IsOperator(options, null, [OperatorRole]),
                Is.False
            );
            Assert.That(
                RequireOperatorAttribute.IsOperator(
                    new OptioOptions(),
                    OperatorGuild,
                    [OperatorRole]
                ),
                Is.False
            );
        }
    }

    [Test]
    public void Each_operator_command_needs_an_operator()
    {
        var command = typeof(OperatorCommands).GetCustomAttribute<SlashCommandAttribute>()!;
        var handlers = typeof(OperatorCommands)
            .GetNestedTypes()
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttribute<SubSlashCommandAttribute>() is not null)
            .ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(command.Name, Is.EqualTo("optioctl"));
            Assert.That(handlers, Has.Count.EqualTo(2));
            foreach (var handler in handlers)
            {
                Assert.That(
                    handler.GetCustomAttribute<RequireOperatorAttribute>(),
                    Is.Not.Null,
                    handler.Name
                );
            }
        }
    }

    [Test]
    public void Operator_guild_needs_an_operator_role()
    {
        var options = new OptioOptions { OperatorGuildId = (long)OperatorGuild };

        Assert.That(
            options.Validate(new(options)).Select(r => r.ErrorMessage),
            Does.Contain("The operator guild needs an operator role.")
        );
    }
}
