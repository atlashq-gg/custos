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
    /// Optio keeps no guild settings and no usage rows, and has no commands.
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
        builder.Services.AddSingleton<UsagePurge>();
        builder.Services.AddHostedService(services => services.GetRequiredService<UsagePurge>());
        return builder;
    }
}
