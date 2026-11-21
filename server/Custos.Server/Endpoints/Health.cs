using Custos.Server.Storage;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Custos.Server.Endpoints;

/// <summary>
/// The health endpoints. A tag selects the checks of each endpoint: <see cref="LiveTag"/> for
/// <see cref="AlivePath"/>, and <see cref="ReadyTag"/> for <see cref="ReadyPath"/>. Readiness
/// holds the database only.
/// </summary>
internal static class Health
{
    public const string Root = "/health";
    public const string AlivePath = Root + "/alive";
    public const string ReadyPath = Root + "/ready";
    public const string LiveTag = "live";
    public const string ReadyTag = "ready";

    public static IServiceCollection AddCustosHealthChecks(this IServiceCollection services)
    {
        services
            .AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag])
            .AddDbContextCheck<CustosDbContext>("database", tags: [ReadyTag]);
        return services;
    }

    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks(AlivePath, Tagged(LiveTag));
        app.MapHealthChecks(ReadyPath, Tagged(ReadyTag));
        return app;
    }

    private static HealthCheckOptions Tagged(string tag) =>
        new() { Predicate = check => check.Tags.Contains(tag) };
}
