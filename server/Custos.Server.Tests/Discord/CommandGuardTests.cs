using System.Reflection;
using Custos.Server.Discord;
using NetCord;
using NetCord.Services;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

namespace Custos.Server.Tests.Discord;

/// <summary>
/// Pins each command and component handler with its guards in <c>CommandGuards.txt</c>. The test
/// reads the attributes as the source declares them, with <see cref="CustomAttributeData"/>.
/// </summary>
public sealed class CommandGuardTests
{
    private const string Snapshot = "Discord/CommandGuards.txt";
    private static readonly string[] UserGuards = ["RequireUserPermissions", "RequireGuildAccess"];

    private static readonly Assembly Server = typeof(DiscordSetup).Assembly;

    private static IEnumerable<Type> Modules() =>
        Server
            .GetTypes()
            .Where(t =>
                !t.IsAbstract
                && (
                    IsSubclassOfGeneric(t, typeof(ApplicationCommandModule<>))
                    || IsSubclassOfGeneric(t, typeof(ComponentInteractionModule<>))
                )
            );

    [Test]
    public void Each_handler_has_the_guards_of_the_snapshot()
    {
        var actual = Modules()
            .SelectMany(module =>
                module
                    .GetMethods(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                    )
                    .Select(method => (Method: method, Surface: SurfaceOf(module, method)))
                    .Where(x => x.Surface is not null)
                    .Select(x =>
                        $"{x.Surface} | {TypeName(module)}.{x.Method.Name} | "
                        + GuardsOf(module, x.Method)
                    )
            )
            .Order(StringComparer.Ordinal)
            .ToList();
        var expected = SnapshotLines()
            .Where(l => !l.StartsWith('#'))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.That(
            actual,
            Is.EqualTo(expected),
            $"The handlers and the guards differ from {Snapshot}. If the change is correct, change "
                + "the file."
        );
    }

    [Test]
    public void Each_handler_without_a_user_guard_tells_why()
    {
        var lines = SnapshotLines();
        var unexplained = lines
            .Select((line, i) => (Line: line, Previous: i > 0 ? lines[i - 1] : ""))
            .Where(x => !x.Line.StartsWith('#') && !UserGuards.Any(x.Line.Contains))
            .Where(x =>
                !x.Previous.StartsWith("# open by design:", StringComparison.Ordinal)
                && !x.Previous.StartsWith("# inline:", StringComparison.Ordinal)
            )
            .Select(x => x.Line);

        Assert.That(unexplained, Is.Empty);
    }

    [Test]
    public void Netcord_loads_the_modules()
    {
        var commands =
            new ApplicationCommandService<
                ApplicationCommandContext,
                AutocompleteInteractionContext
            >();
        commands.AddModules(Server);
        var modals = new ComponentInteractionService<ModalInteractionContext>();
        modals.AddModules(Server);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                commands.GetCommands().Select(c => c.Name),
                Is.EquivalentTo(new[] { "custos", "custosadmin" })
            );
            Assert.That(
                modals.GetComponentInteractions().Select(i => i.Key.ToString()),
                Does.Contain(SettingsModal.CustomId)
            );
        }
    }

    private static List<string> SnapshotLines() =>
        [
            .. File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, Snapshot))
                .Select(l => l.Trim())
                .Where(l => l.Length > 0),
        ];

    private static string? SurfaceOf(Type module, MethodInfo method)
    {
        foreach (var data in method.GetCustomAttributesData())
        {
            var type = data.AttributeType;
            if (type == typeof(SlashCommandAttribute) || type == typeof(SubSlashCommandAttribute))
                return "/" + string.Join(' ', [.. CommandGroupsOf(module), FirstArgument(data)]);
            if (type == typeof(ComponentInteractionAttribute))
            {
                var context = module.BaseType!.GetGenericArguments()[0].Name;
                return $"{context.Replace("InteractionContext", "").ToLowerInvariant()} "
                    + FirstArgument(data);
            }
        }
        return null;
    }

    private static IEnumerable<string> CommandGroupsOf(Type module) =>
        EnclosingTypes(module)
            .Reverse()
            .SelectMany(t => t.GetCustomAttributesData())
            .Where(d =>
                d.AttributeType == typeof(SlashCommandAttribute)
                || d.AttributeType == typeof(SubSlashCommandAttribute)
            )
            .Select(FirstArgument);

    private static string GuardsOf(Type module, MethodInfo method)
    {
        var declarations = EnclosingTypes(module)
            .SelectMany(t => t.GetCustomAttributesData())
            .Concat(method.GetCustomAttributesData())
            .ToList();
        var guards = declarations
            .Where(d => IsSubclassOfGeneric(d.AttributeType, typeof(PreconditionAttribute<>)))
            .Select(Render)
            .Concat(
                declarations
                    .SelectMany(d => d.NamedArguments)
                    .Where(a =>
                        a.MemberName == nameof(ApplicationCommandAttribute.DefaultGuildPermissions)
                    )
                    .Select(a => $"DefaultGuildPermissions({Value(a.TypedValue)})")
            )
            .Order(StringComparer.Ordinal)
            .ToList();
        return guards.Count == 0 ? "none" : string.Join("; ", guards);
    }

    /// <summary><c>Name(arguments)</c>. A string argument with its default value is left out: it
    /// is a message format, not a guard.</summary>
    private static string Render(CustomAttributeData data)
    {
        var name = data.AttributeType.Name;
        name = name[..name.IndexOf("Attribute", StringComparison.Ordinal)];
        var parameters = data.Constructor.GetParameters();
        var arguments = data
            .ConstructorArguments.Where(
                (a, i) =>
                    !(
                        a.ArgumentType == typeof(string)
                        && parameters[i].HasDefaultValue
                        && Equals(a.Value, parameters[i].DefaultValue)
                    )
            )
            .Select(Value)
            .Concat(data.NamedArguments.Select(a => $"{a.MemberName}={Value(a.TypedValue)}"))
            .ToList();
        return arguments.Count == 0 ? name : $"{name}({string.Join(", ", arguments)})";
    }

    private static string Value(CustomAttributeTypedArgument argument) =>
        argument.ArgumentType.IsEnum
            ? Enum.ToObject(argument.ArgumentType, argument.Value!).ToString()!.Replace(", ", "|")
            : argument.Value?.ToString() ?? "null";

    private static string FirstArgument(CustomAttributeData data) =>
        data.ConstructorArguments[0].Value?.ToString() ?? "";

    private static IEnumerable<Type> EnclosingTypes(Type type)
    {
        for (var t = type; t is not null; t = t.DeclaringType)
            yield return t;
    }

    private static string TypeName(Type type) =>
        string.Join('.', EnclosingTypes(type).Reverse().Select(t => t.Name));

    private static bool IsSubclassOfGeneric(Type type, Type generic)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == generic)
                return true;
        }
        return false;
    }
}
