using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Custos.Server.Storage;

/// <summary>
/// The database of the server, PostgreSQL. The names of tables and columns are in snake_case. The
/// history tables have one partition for each war (<see cref="WarRegistry"/>), so the migration
/// creates them with SQL.
/// </summary>
public sealed class CustosDbContext(DbContextOptions<CustosDbContext> options) : DbContext(options)
{
    /// <summary>The name of the connection string, and of the database in the AppHost.</summary>
    public const string ConnectionName = "custos";

    public DbSet<War> Wars => Set<War>();
    public DbSet<Upload> Uploads => Set<Upload>();
    public DbSet<Structure> Structures => Set<Structure>();
    public DbSet<Stockpile> Stockpiles => Set<Stockpile>();
    public DbSet<StockpileEntry> StockpileEntries => Set<StockpileEntry>();
    public DbSet<StockpileChange> StockpileChanges => Set<StockpileChange>();
    public DbSet<StructureChange> StructureChanges => Set<StructureChange>();
    public DbSet<GuildSettingsRow> GuildSettings => Set<GuildSettingsRow>();
    public DbSet<MapRegion> MapRegions => Set<MapRegion>();
    public DbSet<MapPlace> MapPlaces => Set<MapPlace>();
    public DbSet<MapItem> MapItems => Set<MapItem>();

    /// <summary>Adds the options that the server and the design-time factory share.</summary>
    public static void Configure(DbContextOptionsBuilder options) =>
        options.UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<War>(e =>
        {
            e.HasKey(w => w.WarId);
            e.Property(w => w.WarId).ValueGeneratedNever();
        });

        model.Entity<Upload>(e =>
        {
            e.HasKey(u => new
            {
                u.GuildId,
                u.WarId,
                u.UploadId,
            });
            e.HasOne<War>()
                .WithMany()
                .HasForeignKey(u => u.WarId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        model.Entity<Structure>(e =>
        {
            e.HasKey(s => new
            {
                s.GuildId,
                s.WarId,
                s.StructureId,
            });
            e.Property(s => s.Details).HasColumnType("jsonb");
            e.HasOne<War>()
                .WithMany()
                .HasForeignKey(s => s.WarId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        model.Entity<Stockpile>(e =>
        {
            e.HasKey(s => new
            {
                s.GuildId,
                s.WarId,
                s.StockpileId,
            });
            e.HasOne<Structure>()
                .WithMany()
                .HasForeignKey(s => new
                {
                    s.GuildId,
                    s.WarId,
                    s.StructureId,
                })
                .OnDelete(DeleteBehavior.NoAction);
        });

        model.Entity<StockpileEntry>(e =>
        {
            e.HasKey(s => new
            {
                s.GuildId,
                s.WarId,
                s.StockpileId,
                s.Category,
                s.CodeName,
            });
            e.HasOne<Stockpile>()
                .WithMany()
                .HasForeignKey(s => new
                {
                    s.GuildId,
                    s.WarId,
                    s.StockpileId,
                })
                .OnDelete(DeleteBehavior.Cascade);
        });

        // No foreign keys, so that the server can drop the partition of a war alone.
        model.Entity<StockpileChange>(e =>
        {
            e.HasKey(s => new
            {
                s.GuildId,
                s.WarId,
                s.StockpileId,
                s.ObservedAt,
            });
            e.Property(s => s.Entries).HasColumnType("jsonb");
        });

        model.Entity<MapRegion>(e =>
        {
            e.HasKey(r => new { r.WarId, r.Region });
            e.HasOne<War>()
                .WithMany()
                .HasForeignKey(r => r.WarId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        model.Entity<MapPlace>(e =>
        {
            e.HasKey(p => new
            {
                p.WarId,
                p.Region,
                p.PlaceIndex,
            });
            e.HasOne<MapRegion>()
                .WithMany()
                .HasForeignKey(p => new { p.WarId, p.Region })
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<MapItem>(e =>
        {
            e.HasKey(i => new
            {
                i.WarId,
                i.Region,
                i.ItemIndex,
            });
            e.HasOne<MapRegion>()
                .WithMany()
                .HasForeignKey(i => new { i.WarId, i.Region })
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<GuildSettingsRow>(e =>
        {
            e.ToTable("guild_settings");
            e.HasKey(s => s.GuildId);
            e.Property(s => s.GuildId).ValueGeneratedNever();
            e.Property(s => s.Document).HasColumnType("jsonb");
            // The reverse lookup of the shared guilds: document @> '{"Sharing": ...}'.
            e.HasIndex(s => s.Document).HasMethod("gin").HasOperators("jsonb_path_ops");
        });

        model.Entity<StructureChange>(e =>
        {
            e.HasKey(s => new
            {
                s.GuildId,
                s.WarId,
                s.StructureId,
                s.ObservedAt,
            });
            e.Property(s => s.Details).HasColumnType("jsonb");
        });
    }
}

/// <summary>Creates the context for <c>dotnet ef</c>. A migration needs no connection.</summary>
internal sealed class CustosDbContextDesignTimeFactory
    : IDesignTimeDbContextFactory<CustosDbContext>
{
    public CustosDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CustosDbContext>().UseNpgsql();
        CustosDbContext.Configure(options);
        return new CustosDbContext(options.Options);
    }
}
