using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Custos.Server.Telemetry;

/// <summary>
/// Traces, metrics, and logs through OpenTelemetry. The server exports them with OTLP only when the
/// environment has <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>. The Aspire AppHost sets it.
/// </summary>
internal static class TelemetrySetup
{
    /// <summary>The meter of EF Core 9 and later.</summary>
    private const string EntityFrameworkCoreMeter = "Microsoft.EntityFrameworkCore";

    public static IHostApplicationBuilder AddCustosTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton(_ => CustosTelemetry.CreateActivitySource());
        builder.Services.AddSingleton(_ => CustosTelemetry.CreateMeter());
        builder.Services.AddSingleton<ServerMetrics>();

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        // The PostgreSQL integration of Aspire adds the traces and the metrics of Npgsql.
        var telemetry = builder
            .Services.AddOpenTelemetry()
            // Only the version: OTEL_SERVICE_NAME sets service.name. A second name from AddService
            // would compete with it.
            .ConfigureResource(resource =>
                resource.AddAttributes([new("service.version", ServerVersion.Value)])
            )
            .WithMetrics(metrics =>
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(CustosTelemetry.Name, EntityFrameworkCoreMeter)
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
                    .AddSource(CustosTelemetry.Name)
            );

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            telemetry.UseOtlpExporter();

        return builder;
    }
}
