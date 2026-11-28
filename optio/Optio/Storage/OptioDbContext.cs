using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Optio.Storage;

/// <summary>
/// The database of Optio, PostgreSQL. It holds the settings of Optio for each guild, the operator
/// limits, and the token use of the queries, and no records of a domain service. The names of
/// tables and columns are in snake_case.
/// </summary>
public sealed class OptioDbContext(DbContextOptions<OptioDbContext> options) : DbContext(options)
{
    /// <summary>The name of the connection string, and of the database in the AppHost.</summary>
    public const string ConnectionName = "optio";

    public DbSet<GuildSettingsRow> GuildSettings => Set<GuildSettingsRow>();

    public DbSet<QueryUsageRow> QueryUsage => Set<QueryUsageRow>();

    public DbSet<OperatorLimitRow> OperatorLimits => Set<OperatorLimitRow>();

    /// <summary>Adds the options that Optio and the design-time factory share.</summary>
    public static void Configure(DbContextOptionsBuilder options) =>
        options.UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<GuildSettingsRow>(e =>
        {
            e.ToTable("guild_settings");
            e.HasKey(s => s.GuildId);
            e.Property(s => s.GuildId).ValueGeneratedNever();
            e.Property(s => s.Document).HasColumnType("jsonb");
            e.Property(s => s.Version).IsRowVersion();
        });
        model.Entity<QueryUsageRow>(e =>
        {
            e.ToTable("query_usage");
            e.Property(u => u.ClientKey).HasMaxLength(32);
            e.Property(u => u.QueryReference).HasMaxLength(64);
            e.Property(u => u.Model).HasMaxLength(100);
            e.Property(u => u.ExternalReference).HasMaxLength(100);
            e.HasIndex(u => new { u.UserId, u.At });
            e.HasIndex(u => new { u.GuildId, u.At });
            e.HasIndex(u => u.At);
        });
        model.Entity<OperatorLimitRow>(e =>
        {
            e.ToTable("operator_limits");
            e.HasKey(l => new { l.Scope, l.TargetId });
            e.Property(l => l.Scope).HasConversion<string>().HasMaxLength(10);
        });
    }
}

/// <summary>The settings of Optio for one guild.</summary>
public sealed class GuildSettingsRow
{
    public required long GuildId { get; init; }

    /// <summary>The settings document (<see cref="Storage.GuildSettings"/>) as JSON.</summary>
    public required string Document { get; set; }

    /// <summary>The PostgreSQL row version <c>xmin</c>. A change of a row that another change
    /// changed first fails.</summary>
    public uint Version { get; set; }

    public required DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The Discord user ID of the last change.</summary>
    public required long UpdatedBy { get; set; }
}

/// <summary>For <c>dotnet ef migrations add</c>. It needs no database.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<OptioDbContext>
{
    public OptioDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OptioDbContext>().UseNpgsql();
        OptioDbContext.Configure(options);
        return new OptioDbContext(options.Options);
    }
}
