using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Optio.Telemetry;

/// <summary>
/// Traces, metrics, and logs through OpenTelemetry. Optio exports them with OTLP only when the
/// environment has <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>. The source and the meter of Optio have the
/// name <see cref="Name"/>.
/// </summary>
internal static class TelemetrySetup
{
    public const string Name = "Optio";
    public const string ServiceNameVariable = "OTEL_SERVICE_NAME";

    public static IHostApplicationBuilder AddOptioTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var telemetry = builder
            .Services.AddOpenTelemetry()
            // The name of the service is Optio. OTEL_SERVICE_NAME replaces it, for example with the
            // name of a resource of the AppHost. A name from AddService would replace the variable,
            // so Optio adds it only without the variable.
            .ConfigureResource(resource =>
            {
                if (string.IsNullOrEmpty(builder.Configuration[ServiceNameVariable]))
                    resource.AddService(Name, serviceVersion: OptioVersion.Value);
                else
                    resource.AddAttributes([new("service.version", OptioVersion.Value)]);
            })
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
