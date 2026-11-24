using System.Reflection;

namespace Optio;

/// <summary>
/// The version of this build: the informational version that CI sets with
/// <c>-p:InformationalVersion</c>, for example <c>0.1.0</c> or <c>unstable+1a2b3c4</c>. A local
/// build reports <c>unstable</c>.
/// </summary>
internal static class OptioVersion
{
    public static readonly string Value = typeof(OptioVersion)
        .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion
        ?? "unknown";
}
