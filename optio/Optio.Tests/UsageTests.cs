using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optio.Storage;
using Optio.Tests.Infrastructure;

namespace Optio.Tests;

/// <summary>The usage rows of the database. Needs Docker for PostgreSQL.</summary>
public sealed class UsageTests
{
    [Test]
    public async Task Purge_removes_only_the_rows_older_than_the_retention()
    {
        using var factory = new OptioFactory(
            database: true,
            settings: new() { ["Optio:UsageRetentionDays"] = "30" }
        );
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptioDbContext>();
        db.QueryUsage.AddRange(Row(now.AddDays(-31)), Row(now.AddDays(-29)));
        await db.SaveChangesAsync();

        var removed = await factory
            .Services.GetRequiredService<UsagePurge>()
            .PurgeAsync(CancellationToken.None);
        var left = await db.QueryUsage.AsNoTracking().Select(u => u.At).ToListAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(left, Has.Count.EqualTo(1));
            Assert.That(left[0], Is.EqualTo(now.AddDays(-29)).Within(TimeSpan.FromSeconds(1)));
        }
    }

    private static QueryUsageRow Row(DateTimeOffset at) =>
        new()
        {
            At = at,
            GuildId = (long)FakeMembers.Guild,
            UserId = (long)FakeMembers.Reader,
            ClientKey = "0123456789abcdef0123456789abcdef",
            QueryReference = "query-0001",
            Model = "model-1",
            InputTokens = 1,
            OutputTokens = 1,
            CacheReadTokens = 0,
            CacheWriteTokens = 0,
        };
}
