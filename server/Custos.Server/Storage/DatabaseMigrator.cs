using Microsoft.EntityFrameworkCore;

namespace Custos.Server.Storage;

/// <summary>
/// Applies the migrations when the server starts, before the other hosted services start. EF Core locks the
/// migration history, so two instances can start at the same time.
/// </summary>
internal sealed class DatabaseMigrator(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CustosDb>().Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
