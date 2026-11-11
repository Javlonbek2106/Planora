using BuildingDraft.Domain.Audit;
using BuildingDraft.Domain.Export;
using BuildingDraft.Domain.Measurements;
using BuildingDraft.Domain.Projects;
using BuildingDraft.Domain.Scans;
using BuildingDraft.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingDraft.Infrastructure.Persistence;

public sealed class BuildingDraftDbContext(DbContextOptions<BuildingDraftDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<GeometryVersion> GeometryVersions => Set<GeometryVersion>();
    public DbSet<GeometryOperation> GeometryOperations => Set<GeometryOperation>();
    public DbSet<Measurement> Measurements => Set<Measurement>();
    public DbSet<ExportJob> ExportJobs => Set<ExportJob>();
    public DbSet<ProjectFile> ProjectFiles => Set<ProjectFile>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ScanSession> ScanSessions => Set<ScanSession>();
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
        modelBuilder.ConfigureMeasurements();
        modelBuilder.ConfigureExports();
        modelBuilder.ConfigureScans();
        modelBuilder.ConfigureUsers();
        modelBuilder.ConfigureAudit();
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

    public static void ConfigureMeasurements(this ModelBuilder model)
    {
        model.Entity<Measurement>(e =>
        {
            e.ToTable("measurements");
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).ValueGeneratedNever();
            e.Property(m => m.TargetProperty).HasMaxLength(50);
            e.Property(m => m.Reference).HasConversion<string>().HasMaxLength(20);
            e.Property(m => m.Source).HasConversion<string>().HasMaxLength(20);
            e.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(m => m.DeviceName).HasMaxLength(200);
            e.Ignore(m => m.IsEstimate);
            e.HasIndex(m => m.ProjectId);
            e.HasIndex(m => new { m.VariantId, m.TargetObjectId });
            e.HasOne<Project>().WithMany().HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DesignVariant>().WithMany().HasForeignKey(m => m.VariantId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    public static void ConfigureExports(this ModelBuilder model)
    {
        model.Entity<ProjectFile>(e =>
        {
            e.ToTable("project_files");
            e.HasKey(f => f.Id);
            e.Property(f => f.Id).ValueGeneratedNever();
            e.Property(f => f.FileName).HasMaxLength(260);
            e.Property(f => f.ContentType).HasMaxLength(100);
            e.Property(f => f.StorageKey).HasMaxLength(200);
            e.Property(f => f.Checksum).HasMaxLength(64);
            e.HasIndex(f => f.ProjectId);
            e.HasOne<Project>().WithMany().HasForeignKey(f => f.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<ExportJob>(e =>
        {
            e.ToTable("export_jobs");
            e.HasKey(j => j.Id);
            e.Property(j => j.Id).ValueGeneratedNever();
            e.Property(j => j.Format).HasConversion<string>().HasMaxLength(10);
            e.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(j => j.ErrorCode).HasMaxLength(100);
            e.HasIndex(j => j.ProjectId);
            e.HasOne<Project>().WithMany().HasForeignKey(j => j.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<GeometryVersion>().WithMany().HasForeignKey(j => j.VersionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProjectFile>().WithMany().HasForeignKey(j => j.ResultFileId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    public static void ConfigureScans(this ModelBuilder model)
    {
        model.Entity<ScanSession>(e =>
        {
            e.ToTable("scan_sessions");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.Source).HasConversion<string>().HasMaxLength(20);
            e.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(s => s.DeviceModel).HasMaxLength(200);
            e.HasIndex(s => s.ProjectId);
            e.HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DesignVariant>().WithMany().HasForeignKey(s => s.VariantId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Floor>().WithMany().HasForeignKey(s => s.FloorId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    public static void ConfigureUsers(this ModelBuilder model)
    {
        model.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).ValueGeneratedNever();
            e.Property(u => u.ExternalSubject).HasMaxLength(256);
            e.Property(u => u.Email).HasMaxLength(320);
            e.Property(u => u.DisplayName).HasMaxLength(200);
            e.HasIndex(u => u.ExternalSubject).IsUnique();
        });
    }

    public static void ConfigureAudit(this ModelBuilder model)
    {
        model.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).ValueGeneratedNever();
            e.Property(a => a.Action).HasMaxLength(60);
            e.Property(a => a.EntityType).HasMaxLength(40);
            e.HasIndex(a => a.ProjectId);
            e.HasOne<Project>().WithMany().HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    static void OwnedByVersion<T>(this EntityTypeBuilder<T> entity) where T : class
    {
        entity.HasOne<GeometryVersion>().WithMany()
            .HasForeignKey("GeometryVersionId").OnDelete(DeleteBehavior.Restrict);
    }
}
