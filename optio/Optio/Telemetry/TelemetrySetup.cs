using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Optio.Telemetry;

/// <summary>
/// Traces, metrics, and logs through OpenTelemetry. optio exports them with OTLP only when the
/// environment has <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>. The source and the meter of optio have the
/// name <see cref="Name"/>.
/// </summary>
internal static class TelemetrySetup
{
    public const string Name = "Optio";

    public static IHostApplicationBuilder AddOptioTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var telemetry = builder
            .Services.AddOpenTelemetry()
            // Only the version: OTEL_SERVICE_NAME sets service.name. A second name from AddService
            // would compete with it.
            .ConfigureResource(resource =>
                resource.AddAttributes([new("service.version", OptioVersion.Value)])
            )
            .WithMetrics(metrics =>
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(Name)
            )
            .WithTracing(tracing =>
                tracing
                    // A container probes the health endpoints often. Their traces carry no
                    // information.
                    .AddAspNetCoreInstrumentation(aspNetCore =>
                        aspNetCore.Filter = context =>
                            !context.Request.Path.StartsWithSegments(Endpoints.Health.Root)
                    )
                    .AddHttpClientInstrumentation()
                    .AddSource(Name)
            );

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            telemetry.UseOtlpExporter();

        return builder;
    }
}
