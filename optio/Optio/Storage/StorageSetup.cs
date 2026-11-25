using Optio.Endpoints;

namespace Optio.Storage;

internal static class StorageSetup
{
    public static bool IsEnabled(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(
            configuration.GetConnectionString(OptioDbContext.ConnectionName)
        );

    /// <summary>
    /// Registers the database when the configuration has its connection string. Without it,
    /// optio keeps no guild settings, and has no commands and no query.
    /// </summary>
    public static IHostApplicationBuilder AddOptioStorage(this IHostApplicationBuilder builder)
    {
        if (!IsEnabled(builder.Configuration))
            return builder;

        // No retries: Discord and Custos answer a failure, and the user tries again. The check of
        // readiness is below, with the tag of the readiness endpoint.
        builder.AddNpgsqlDbContext<OptioDbContext>(
            OptioDbContext.ConnectionName,
            settings =>
            {
                settings.DisableRetry = true;
                settings.DisableHealthChecks = true;
            },
            OptioDbContext.Configure
        );
        builder.Services.AddScoped<GuildSettingsStore>();
        builder
            .Services.AddHealthChecks()
            .AddDbContextCheck<OptioDbContext>("database", tags: [Health.ReadyTag]);

        // The migrator must start first: hosted services start in the order of registration.
        builder.Services.AddHostedService<DatabaseMigrator>();
        return builder;
    }
}
