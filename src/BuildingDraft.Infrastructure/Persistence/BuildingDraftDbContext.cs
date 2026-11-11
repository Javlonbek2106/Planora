using BuildingDraft.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingDraft.Infrastructure.Persistence;

public sealed class BuildingDraftDbContext(DbContextOptions<BuildingDraftDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<GeometryVersion> GeometryVersions => Set<GeometryVersion>();
    public DbSet<GeometryOperation> GeometryOperations => Set<GeometryOperation>();
    public DbSet<NodeRow> Nodes => Set<NodeRow>();
    public DbSet<WallRow> Walls => Set<WallRow>();
    public DbSet<RoomRow> Rooms => Set<RoomRow>();
    public DbSet<RoomBoundaryRow> RoomBoundaries => Set<RoomBoundaryRow>();
    public DbSet<OpeningRow> Openings => Set<OpeningRow>();
    public DbSet<ConstraintRow> Constraints => Set<ConstraintRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ConfigureProjects();
        modelBuilder.ConfigureGeometryHistory();
        modelBuilder.ConfigureGeometrySnapshot();
    }
}

static class ModelConfiguration
{
    public static void ConfigureProjects(this ModelBuilder model)
    {
        model.Entity<Project>(e =>
        {
            e.ToTable("projects");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).ValueGeneratedNever();
            e.Property(p => p.Name).HasMaxLength(200);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.Units).HasConversion<string>().HasMaxLength(10);
            e.HasIndex(p => p.OwnerId);
            e.Ignore(p => p.Original);
            e.Ignore(p => p.ActiveVariants);
            e.HasMany(p => p.Floors).WithOne().HasForeignKey(f => f.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(p => p.Variants).WithOne().HasForeignKey(v => v.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(p => p.Floors).HasField("floors").UsePropertyAccessMode(PropertyAccessMode.Field);
            e.Navigation(p => p.Variants).HasField("variants").UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        model.Entity<Floor>(e =>
        {
            e.ToTable("floors");
            e.HasKey(f => f.Id);
            e.Property(f => f.Id).ValueGeneratedNever();
            e.HasIndex(f => new { f.ProjectId, f.FloorNumber }).IsUnique();
        });

        model.Entity<DesignVariant>(e =>
        {
            e.ToTable("design_variants");
            e.HasKey(v => v.Id);
            e.Property(v => v.Id).ValueGeneratedNever();
            e.Property(v => v.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(v => v.CurrentVersionNumber).IsConcurrencyToken();
            e.HasQueryFilter(v => !v.IsDeleted);
            // The database itself guarantees a single Original Layout per project.
            e.HasIndex(v => v.ProjectId).IsUnique().HasFilter("type = 'Original'");
        });
    }

    public static void ConfigureGeometryHistory(this ModelBuilder model)
    {
        model.Entity<GeometryVersion>(e =>
        {
            e.ToTable("geometry_versions");
            e.HasKey(v => v.Id);
            e.Property(v => v.Id).ValueGeneratedNever();
            e.Property(v => v.Hash).HasMaxLength(64);
            e.HasIndex(v => new { v.VariantId, v.VersionNumber }).IsUnique();
            e.HasOne<DesignVariant>().WithMany().HasForeignKey(v => v.VariantId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<GeometryOperation>(e =>
        {
            e.ToTable("geometry_operations");
            e.HasKey(o => o.Id);
            e.Property(o => o.Id).ValueGeneratedNever();
            e.Property(o => o.Command).HasMaxLength(100);
            e.HasIndex(o => o.ClientOperationId).IsUnique();
            e.HasIndex(o => o.VariantId);
            e.HasOne<GeometryVersion>().WithMany().HasForeignKey(o => o.VersionId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    public static void ConfigureGeometrySnapshot(this ModelBuilder model)
    {
        model.Entity<NodeRow>(e =>
        {
            e.ToTable("geometry_nodes");
            e.HasKey(n => new { n.GeometryVersionId, n.Id });
            e.OwnedByVersion();
            e.HasOne<Floor>().WithMany().HasForeignKey(n => n.FloorId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<WallRow>(e =>
        {
            e.ToTable("walls");
            e.HasKey(w => new { w.GeometryVersionId, w.Id });
            e.OwnedByVersion();
            e.HasOne<NodeRow>().WithMany().HasForeignKey(w => new { w.GeometryVersionId, w.StartNodeId })
                .HasPrincipalKey(n => new { n.GeometryVersionId, n.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<NodeRow>().WithMany().HasForeignKey(w => new { w.GeometryVersionId, w.EndNodeId })
                .HasPrincipalKey(n => new { n.GeometryVersionId, n.Id }).OnDelete(DeleteBehavior.Restrict);
            e.Property(w => w.Classification).HasConversion<string>().HasMaxLength(20);
        });

        model.Entity<RoomRow>(e =>
        {
            e.ToTable("rooms");
            e.HasKey(r => new { r.GeometryVersionId, r.Id });
            e.OwnedByVersion();
            e.Property(r => r.AreaType).HasConversion<string>().HasMaxLength(20);
            e.HasOne<Floor>().WithMany().HasForeignKey(r => r.FloorId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<RoomBoundaryRow>(e =>
        {
            e.ToTable("room_boundaries");
            e.HasKey(b => new { b.GeometryVersionId, b.RoomId, b.SequenceNumber });
            e.OwnedByVersion();
            e.HasOne<RoomRow>().WithMany().HasForeignKey(b => new { b.GeometryVersionId, b.RoomId })
                .HasPrincipalKey(r => new { r.GeometryVersionId, r.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WallRow>().WithMany().HasForeignKey(b => new { b.GeometryVersionId, b.WallId })
                .HasPrincipalKey(w => new { w.GeometryVersionId, w.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<OpeningRow>(e =>
        {
            e.ToTable("openings");
            e.HasKey(o => new { o.GeometryVersionId, o.Id });
            e.OwnedByVersion();
            e.Property(o => o.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasOne<WallRow>().WithMany().HasForeignKey(o => new { o.GeometryVersionId, o.WallId })
                .HasPrincipalKey(w => new { w.GeometryVersionId, w.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<ConstraintRow>(e =>
        {
            e.ToTable("geometry_constraints");
            e.HasKey(c => new { c.GeometryVersionId, c.Id });
            e.OwnedByVersion();
            e.Property(c => c.Type).HasConversion<string>().HasMaxLength(30);
        });
    }

    static void OwnedByVersion<T>(this EntityTypeBuilder<T> entity) where T : class
    {
        entity.HasOne<GeometryVersion>().WithMany()
            .HasForeignKey("GeometryVersionId").OnDelete(DeleteBehavior.Restrict);
    }
}
