using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NetCord;
using NetCord.Services.ApplicationCommands;
using Optio.Discord;
using Optio.Storage;
using Optio.Tests.Infrastructure;

namespace Optio.Tests;

/// <summary>The settings of a guild and the command that changes them. Needs Docker for
/// PostgreSQL.</summary>
public sealed class GuildSettingsTests
{
    private const ulong Guild = FakeMembers.Guild;
    private const ulong Admin = FakeMembers.Owner;

    [Test]
    public async Task Store_keeps_the_prompt_and_removes_an_empty_prompt()
    {
        using var factory = new OptioFactory(database: true);
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
        using var factory = new OptioFactory(database: true);
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<GuildSettingsStore>();
        var prompt = new string('x', GuildSettings.MaxPromptLength + 1);

        await Assert.ThatAsync(
            () => store.SaveAsync(Guild, new GuildSettings(prompt), Admin, default),
            Throws.ArgumentException
        );
    }

    [Test]
    public async Task Readiness_checks_the_database()
    {
        using var factory = new OptioFactory(database: true);

        var response = await factory.CreateClient().GetAsync("/health/ready");

        Assert.That((int)response.StatusCode, Is.EqualTo(200));
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
                // RequireUserPermissions<TContext> sets ChannelPermissions: an Administrator has
                // each permission in each channel.
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
}
