using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NetCord;
using NetCord.Services.ApplicationCommands;
using Optio.Discord;
using Optio.Harness;
using Optio.Storage;
using Optio.Tests.Infrastructure;

namespace Optio.Tests;

/// <summary>The settings of a guild, the command that changes them, and the query limits. Needs
/// Docker for PostgreSQL.</summary>
public sealed class GuildSettingsTests
{
    private const ulong Guild = FakeMembers.Guild;
    private const ulong Admin = FakeMembers.Owner;

    [Test]
    public async Task Store_keeps_the_prompt_and_removes_an_empty_prompt()
    {
        using var factory = new OptioFactory(query: true);
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<GuildSettingsStore>();

        var before = await store.GetAsync(Guild, default);
        await store.SaveAsync(Guild, new GuildSettings("  Be brief.  "), Admin, default);
        var saved = await store.GetAsync(Guild, default);
        await store.SaveAsync(Guild, new GuildSettings(" "), Admin, default);
        var removed = await store.GetAsync(Guild, default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Is.EqualTo(GuildSettings.Default));
            Assert.That(saved.Prompt, Is.EqualTo("Be brief."));
            Assert.That(removed.Prompt, Is.Null);
        }
    }

    [Test]
    public async Task Store_refuses_a_prompt_over_the_limit()
    {
        using var factory = new OptioFactory(query: true);
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<GuildSettingsStore>();
        var prompt = new string('x', GuildSettings.MaxPromptLength + 1);

        await Assert.ThatAsync(
            () => store.SaveAsync(Guild, new GuildSettings(prompt), Admin, default),
            Throws.ArgumentException
        );
    }

    [Test]
    public void Command_and_form_need_the_administrator_permission()
    {
        var command = typeof(AdminCommands).GetCustomAttribute<SlashCommandAttribute>()!;
        MethodInfo[] handlers =
        [
            typeof(AdminCommands).GetMethod(nameof(AdminCommands.SettingsAsync))!,
            typeof(SettingsModal).GetMethod(nameof(SettingsModal.SubmitAsync))!,
        ];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(command.Name, Is.EqualTo("optioadmin"));
            Assert.That(command.DefaultGuildPermissions, Is.EqualTo(Permissions.Administrator));
            foreach (var handler in handlers)
            {
                // RequireUserPermissions<TContext>: an Administrator has each permission in
                // each channel.
                var guard = handler
                    .GetCustomAttributes()
                    .SingleOrDefault(a => a.GetType().Name.StartsWith("RequireUserPermissions"));
                Assert.That(
                    guard?.GetType().GetProperty("ChannelPermissions")?.GetValue(guard),
                    Is.EqualTo(Permissions.Administrator),
                    handler.Name
                );
            }
        }
    }

    [Test]
    public void Limiter_allows_one_running_query_and_the_queries_of_one_hour()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new QueryLimiter(
            Options.Create(new OptioOptions { MaxQueriesPerHour = 2 }),
            time
        );

        using (Assert.EnterMultipleScope())
        {
            Assert.That(limiter.TryStart(Admin), Is.Null);
            Assert.That(limiter.TryStart(Admin), Is.Not.Null, "a query runs");
            limiter.Finish(Admin);
            Assert.That(limiter.TryStart(Admin), Is.Null);
            limiter.Finish(Admin);
            Assert.That(limiter.TryStart(Admin), Is.EqualTo(TimeSpan.FromHours(1)));
            time.Advance(TimeSpan.FromHours(1));
            Assert.That(limiter.TryStart(Admin), Is.Null);
        }
    }

    [Test]
    public void System_prompt_puts_the_prompt_of_the_guild_last_in_its_own_block()
    {
        var text = QueryHarness.SystemPrompt("Rules.", Guild, "Be brief.");

        Assert.That(
            text,
            Is.EqualTo(
                $"Rules.\n\nThe guild of the user has the ID {Guild}. Each tool reads this guild."
                    + "\n\nThe administrators of the guild give these instructions:\n"
                    + "<guild_instructions>\nBe brief.\n</guild_instructions>"
            )
        );
    }
}
