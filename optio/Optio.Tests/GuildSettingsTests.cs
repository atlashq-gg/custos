using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NetCord;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;
using Optio.Discord;
using Optio.Storage;
using Optio.Tests.Infrastructure;

namespace Optio.Tests;

/// <summary>The settings document of a guild and the commands that change it. The store tests
/// need Docker for PostgreSQL.</summary>
public sealed class GuildSettingsTests
{
    private const ulong Guild = FakeMembers.Guild;
    private const ulong Admin = FakeMembers.Owner;
    private const ulong Role = FakeMembers.ReadRole;

    [Test]
    public void Parse_reads_a_full_document_and_cleans_it()
    {
        var result = GuildSettings.Parse(
            $$"""
            {
              "SchemaVersion": 1,
              "Access": { "QueryRoleIds": ["{{Role}}", "{{Role}}"] },
              "Prompts": { "Guild": "  Be brief.  ", "Roles": { "{{Role}}": " " } },
              "Limits": { "Roles": { "{{Role}}": 5000 }, "Users": { "{{Admin}}": 100 } }
            }
            """
        );

        var settings = result.Settings!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings.Access.QueryRoleIds, Is.EqualTo(new[] { Role }));
            Assert.That(settings.Prompts.Guild, Is.EqualTo("Be brief."));
            Assert.That(settings.Prompts.Roles, Is.Empty);
            Assert.That(settings.Limits.Roles[Role], Is.EqualTo(5000));
            Assert.That(settings.Limits.Users[Admin], Is.EqualTo(100));
            Assert.That(GuildSettings.Parse(settings.ToJson()).Settings, Is.Not.Null);
        }
    }

    [TestCase("""{"SchemaVersion": 2}""", "SchemaVersion must be 1")]
    [TestCase("""{"SchemaVersion": 1, "Other": 1}""", "not valid")]
    [TestCase("""{"SchemaVersion": 1, "Access": {"QueryRoleIds": [1]}}""", "not valid")]
    [TestCase("""{"SchemaVersion": 1, "Access": {"QueryRoleIds": ["12"]}}""", "not a Discord")]
    [TestCase(
        """{"SchemaVersion": 1, "Limits": {"Roles": {"500000000000000001": 0}}}""",
        "1 token or more"
    )]
    public void Parse_refuses_a_document_that_is_not_valid(string json, string reason)
    {
        Assert.That(GuildSettings.Parse(json).Refusal, Does.Contain(reason));
    }

    [Test]
    public void Check_applies_the_caps_of_roles_and_texts_but_not_of_users()
    {
        var roles = Enumerable.Range(1, GuildSettings.MaxRoleEntries + 1).Select(Id).ToList();
        var users = Enumerable.Range(1, 200).ToDictionary(Id, _ => 10L);
        var settings = new GuildSettings { Limits = new() { Users = users } };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings.Check().Settings, Is.Not.Null);
            Assert.That(
                (settings with { Access = new() { QueryRoleIds = roles } }).Check().Refusal,
                Does.Contain("Access.QueryRoleIds has 26 entries")
            );
            Assert.That(
                (
                    settings with
                    {
                        Prompts = new()
                        {
                            Roles = new Dictionary<ulong, string>
                            {
                                [Role] = new('x', GuildSettings.MaxRolePromptLength + 1),
                            },
                        },
                    }
                )
                    .Check()
                    .Refusal,
                Does.Contain("more than 500 characters")
            );
            Assert.That(
                (
                    settings with
                    {
                        Prompts = new()
                        {
                            Guild = new('x', GuildSettings.MaxGuildPromptLength + 1),
                        },
                    }
                )
                    .Check()
                    .Refusal,
                Does.Contain("more than 1000 characters")
            );
        }
    }

    [Test]
    public async Task Store_keeps_a_change_and_refuses_a_change_over_a_limit()
    {
        using var factory = new OptioFactory(database: true);
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<GuildSettingsStore>();

        var before = await store.GetAsync(Guild, default);
        await store.UpdateAsync(
            Guild,
            s => s with { Prompts = new() { Guild = "Hi." } },
            Admin,
            default
        );
        var refused = await store.UpdateAsync(
            Guild,
            s => s with { Prompts = new() { Guild = new('x', 1001) } },
            Admin,
            default
        );
        var after = await store.GetAsync(Guild, default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before.ToJson(), Is.EqualTo(GuildSettings.Default.ToJson()));
            Assert.That(refused.Refusal, Is.Not.Null);
            Assert.That(after.Prompts.Guild, Is.EqualTo("Hi."));
        }
    }

    [Test]
    public async Task Store_keeps_each_of_concurrent_changes()
    {
        using var factory = new OptioFactory(database: true);
        var roles = Enumerable.Range(1, 4).Select(Id).ToList();

        await Task.WhenAll(
            roles.Select(async role =>
            {
                await using var scope = factory.Services.CreateAsyncScope();
                await scope
                    .ServiceProvider.GetRequiredService<GuildSettingsStore>()
                    .UpdateAsync(
                        Guild,
                        s =>
                            s with
                            {
                                Access = new() { QueryRoleIds = [.. s.Access.QueryRoleIds, role] },
                            },
                        Admin,
                        default
                    );
            })
        );

        await using var check = factory.Services.CreateAsyncScope();
        var settings = await check
            .ServiceProvider.GetRequiredService<GuildSettingsStore>()
            .GetAsync(Guild, default);
        Assert.That(settings.Access.QueryRoleIds, Is.EquivalentTo(roles));
    }

    [Test]
    public async Task Migration_moves_the_prompt_of_a_guild_into_the_document()
    {
        using var factory = new OptioFactory(database: true);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptioDbContext>();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261124200433_Initial");
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO guild_settings (guild_id, prompt, updated_at, updated_by)
            VALUES ({(long)Guild}, 'Be brief.', now(), {(long)Admin})
            """
        );

        await migrator.MigrateAsync();
        var settings = await scope
            .ServiceProvider.GetRequiredService<GuildSettingsStore>()
            .GetAsync(Guild, default);

        Assert.That(settings.Prompts.Guild, Is.EqualTo("Be brief."));
    }

    [Test]
    public async Task Readiness_checks_the_database()
    {
        using var factory = new OptioFactory(database: true);

        var response = await factory.CreateClient().GetAsync("/health/ready");

        Assert.That((int)response.StatusCode, Is.EqualTo(200));
    }

    [Test]
    public void Each_command_and_form_needs_the_administrator_permission()
    {
        var command = typeof(AdminCommands).GetCustomAttribute<SlashCommandAttribute>()!;
        var handlers = typeof(AdminCommands)
            .Assembly.GetTypes()
            .SelectMany(t => t.GetMethods())
            .Where(m =>
                m.GetCustomAttribute<SubSlashCommandAttribute>() is not null
                || m.GetCustomAttribute<ComponentInteractionAttribute>() is not null
            )
            .ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(command.Name, Is.EqualTo("optioadmin"));
            Assert.That(command.DefaultGuildPermissions, Is.EqualTo(Permissions.Administrator));
            Assert.That(handlers, Has.Count.EqualTo(10));
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
                    $"{handler.DeclaringType!.Name}.{handler.Name}"
                );
            }
        }
    }

    private static ulong Id(int n) => 600000000000000000 + (ulong)n;
}
