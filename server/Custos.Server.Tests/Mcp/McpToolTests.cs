using System.Reflection;
using Custos.Server.Mcp;
using ModelContextProtocol.Server;

namespace Custos.Server.Tests.Mcp;

/// <summary>Pins each MCP tool with its guard, its arguments, and its description in
/// <c>McpTools.txt</c>.</summary>
public sealed class McpToolTests
{
    private const string Snapshot = "Mcp/McpTools.txt";

    [Test]
    public void Each_tool_has_the_guard_and_the_arguments_of_the_snapshot()
    {
        var actual = typeof(McpSetup)
            .Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t =>
                t.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                )
            )
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(Line)
            .Order(StringComparer.Ordinal)
            .ToList();
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, Snapshot))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.That(
            actual,
            Is.EqualTo(expected),
            $"The tools differ from {Snapshot}. If the change is correct, change the file."
        );
    }

    [Test]
    public void Each_argument_name_is_in_snake_case()
    {
        var names = typeof(McpSetup)
            .Assembly.GetTypes()
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .SelectMany(Arguments)
            .Select(p => p.Name!);

        Assert.That(names, Has.All.Match("^[a-z][a-z0-9_]*$"));
    }

    private static string Line(MethodInfo method)
    {
        var tool = method.GetCustomAttribute<McpServerToolAttribute>()!;
        var guard = method.GetCustomAttribute<McpGuardAttribute>();
        var arguments = Arguments(method)
            .Select(p => $"{p.Name}:{TypeName(p.ParameterType)}{(p.HasDefaultValue ? "?" : "")}")
            .ToList();
        return string.Join(
            " | ",
            tool.Name,
            $"{method.DeclaringType!.Name}.{method.Name}",
            guard is null ? "none" : $"{guard.Scope}, {guard.Access}",
            arguments.Count == 0 ? "none" : string.Join(", ", arguments),
            method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description
                ?? "none"
        );
    }

    /// <summary>The parameters of the input schema: not the cancellation token.</summary>
    private static IEnumerable<ParameterInfo> Arguments(MethodInfo method) =>
        method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken));

    private static string TypeName(Type type) =>
        type switch
        {
            _ when type == typeof(string) => "string",
            _ when type == typeof(int) => "int",
            _ when type == typeof(bool) => "bool",
            _ when Nullable.GetUnderlyingType(type) is { } inner => TypeName(inner),
            _ when type.IsArray => $"{TypeName(type.GetElementType()!)}[]",
            _ => type.Name,
        };
}
