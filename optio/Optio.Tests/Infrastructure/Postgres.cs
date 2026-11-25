using Npgsql;
using Testcontainers.PostgreSql;

namespace Optio.Tests.Infrastructure;

/// <summary>
/// One PostgreSQL container for the test run. It starts on first use. Each factory gets its own
/// database, and Optio creates it with its migrations. Testcontainers removes the container after
/// the run.
/// </summary>
internal static class Postgres
{
    private static readonly Lazy<Task<PostgreSqlContainer>> Container = new(async () =>
    {
        var container = new PostgreSqlBuilder("postgres:18").Build();
        await container.StartAsync();
        return container;
    });

    /// <summary>The connection string of a new, empty database.</summary>
    public static string NewDatabase()
    {
        var container = Container.Value.GetAwaiter().GetResult();
        return new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = $"optio_{Guid.NewGuid():N}",
        }.ConnectionString;
    }
}
