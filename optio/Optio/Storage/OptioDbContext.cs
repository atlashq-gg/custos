using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Optio.Storage;

/// <summary>
/// The database of Optio, PostgreSQL. It holds the settings of Optio for each guild, and no
/// records of a domain service. The names of tables and columns are in snake_case.
/// </summary>
public sealed class OptioDbContext(DbContextOptions<OptioDbContext> options) : DbContext(options)
{
    /// <summary>The name of the connection string, and of the database in the AppHost.</summary>
    public const string ConnectionName = "optio";

    public DbSet<GuildSettingsRow> GuildSettings => Set<GuildSettingsRow>();

    /// <summary>Adds the options that Optio and the design-time factory share.</summary>
    public static void Configure(DbContextOptionsBuilder options) =>
        options.UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder model) =>
        model.Entity<GuildSettingsRow>(e =>
        {
            e.ToTable("guild_settings");
            e.HasKey(s => s.GuildId);
            e.Property(s => s.GuildId).ValueGeneratedNever();
            e.Property(s => s.Prompt).HasMaxLength(Storage.GuildSettings.MaxPromptLength);
        });
}

/// <summary>The settings of Optio for one guild.</summary>
public sealed class GuildSettingsRow
{
    public required long GuildId { get; init; }

    /// <summary>The prompt of the guild for the query harness. Null: no prompt.</summary>
    public string? Prompt { get; set; }

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
