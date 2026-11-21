using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Custos.Server.Telemetry;

/// <summary>
/// Creates the one activity source and the one meter of the server. The two have the name
/// <see cref="Name"/> and the version of the build. The container holds each as a singleton.
/// </summary>
internal static class CustosTelemetry
{
    public const string Name = "Custos.Server";

    public static ActivitySource CreateActivitySource() => new(Name, ServerVersion.Value);

    public static Meter CreateMeter() => new(Name, ServerVersion.Value);
}
