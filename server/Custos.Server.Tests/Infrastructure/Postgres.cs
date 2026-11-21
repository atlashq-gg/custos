using Npgsql;
using Testcontainers.PostgreSql;

namespace Custos.Server.Tests.Infrastructure;

/// <summary>
/// One PostgreSQL container for the test run. It starts on first use. Each factory gets its own
/// database, and the server creates it with its migrations. Testcontainers removes the container
/// after the run.
/// </summary>
internal static class Postgres
{
    private static readonly Lazy<Task<PostgreSqlContainer>> Container = new(async () =>
    {
        // Each test server has its own database and its own pool of connections. The default of
        // 100 connections is too few for the full run.
        var container = new PostgreSqlBuilder("postgres:18")
            .WithCommand("-c", "max_connections=1000")
            .Build();
        await container.StartAsync();
        return container;
    });

    /// <summary>The connection string of a new, empty database.</summary>
    public static string NewDatabase()
    {
        var container = Container.Value.GetAwaiter().GetResult();
        return new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = $"custos_{Guid.NewGuid():N}",
        }.ConnectionString;
    }
}
