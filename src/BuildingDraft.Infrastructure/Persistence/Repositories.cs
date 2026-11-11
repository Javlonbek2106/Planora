using BuildingDraft.Domain;
using BuildingDraft.Domain.Abstractions;
using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Export;
using BuildingDraft.Domain.Measurements;
using BuildingDraft.Domain.Projects;
using BuildingDraft.Domain.Scans;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BuildingDraft.Infrastructure.Persistence;

public sealed class EfProjectRepository(BuildingDraftDbContext db) : IProjectRepository
{
    IQueryable<Project> Owned(Guid ownerId) =>
        db.Projects.Include(p => p.Floors).Include(p => p.Variants).Where(p => p.OwnerId == ownerId);

    public Task<Project?> FindAsync(Guid projectId, Guid ownerId, CancellationToken ct) =>
        Owned(ownerId).FirstOrDefaultAsync(p => p.Id == projectId, ct);

    public Task<Project?> FindByVariantAsync(Guid variantId, Guid ownerId, CancellationToken ct) =>
        Owned(ownerId).FirstOrDefaultAsync(p => p.Variants.Any(v => v.Id == variantId), ct);

    public Task<Project?> FindByFloorAsync(Guid floorId, Guid ownerId, CancellationToken ct) =>
        Owned(ownerId).FirstOrDefaultAsync(p => p.Floors.Any(f => f.Id == floorId), ct);

    // Ids are time-ordered UUIDv7, so ordering by id is ordering by creation.
    public async Task<(IReadOnlyList<Project> Items, int Total)> ListAsync(Guid ownerId, int page, int pageSize, CancellationToken ct)
    {
        var query = Owned(ownerId).Where(p => p.Status == ProjectStatus.Active);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public void Add(Project project) => db.Projects.Add(project);
}

public sealed class EfGeometryVersionRepository(BuildingDraftDbContext db) : IGeometryVersionRepository
{
    public Task<GeometryVersion?> FindAsync(Guid versionId, CancellationToken ct) =>
        db.GeometryVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, ct);

    public Task<GeometryVersion?> FindByNumberAsync(Guid variantId, int versionNumber, CancellationToken ct) =>
        db.GeometryVersions.AsNoTracking().FirstOrDefaultAsync(v => v.VariantId == variantId && v.VersionNumber == versionNumber, ct);

    public async Task<IReadOnlyList<GeometryVersion>> ListAsync(Guid variantId, CancellationToken ct) =>
        await db.GeometryVersions.AsNoTracking().Where(v => v.VariantId == variantId).OrderBy(v => v.VersionNumber).ToListAsync(ct);

    public async Task<GeometryModel> LoadModelAsync(Guid versionId, CancellationToken ct)
    {
        var rows = new GeometryRows(
            await db.Nodes.AsNoTracking().Where(r => r.GeometryVersionId == versionId).ToListAsync(ct),
            await db.Walls.AsNoTracking().Where(r => r.GeometryVersionId == versionId).ToListAsync(ct),
            await db.Rooms.AsNoTracking().Where(r => r.GeometryVersionId == versionId).ToListAsync(ct),
            await db.RoomBoundaries.AsNoTracking().Where(r => r.GeometryVersionId == versionId).ToListAsync(ct),
            await db.Openings.AsNoTracking().Where(r => r.GeometryVersionId == versionId).ToListAsync(ct),
            await db.Constraints.AsNoTracking().Where(r => r.GeometryVersionId == versionId).ToListAsync(ct));
        return GeometryRowMapper.ToModel(rows);
    }

    public void Add(GeometryVersion version, GeometryModel model)
    {
        var rows = GeometryRowMapper.ToRows(version.Id, model);
        db.GeometryVersions.Add(version);
        db.Nodes.AddRange(rows.Nodes);
        db.Walls.AddRange(rows.Walls);
        db.Rooms.AddRange(rows.Rooms);
        db.RoomBoundaries.AddRange(rows.Boundaries);
        db.Openings.AddRange(rows.Openings);
        db.Constraints.AddRange(rows.Constraints);
    }
}

public sealed class EfGeometryOperationRepository(BuildingDraftDbContext db) : IGeometryOperationRepository
{
    public Task<GeometryOperation?> FindByClientIdAsync(Guid clientOperationId, CancellationToken ct) =>
        db.GeometryOperations.AsNoTracking().FirstOrDefaultAsync(o => o.ClientOperationId == clientOperationId, ct);

    public async Task<IReadOnlyList<GeometryOperation>> ListSinceAsync(Guid variantId, int afterVersion, CancellationToken ct) =>
        await db.GeometryOperations.AsNoTracking()
            .Where(o => o.VariantId == variantId && o.ResultVersionNumber > afterVersion)
            .OrderBy(o => o.ResultVersionNumber)
            .ToListAsync(ct);

    public void Add(GeometryOperation operation) => db.GeometryOperations.Add(operation);
}

public sealed class EfMeasurementRepository(BuildingDraftDbContext db) : IMeasurementRepository
{
    public Task<Measurement?> FindAsync(Guid measurementId, Guid ownerId, CancellationToken ct) =>
        (from m in db.Measurements
         join p in db.Projects on m.ProjectId equals p.Id
         where m.Id == measurementId && p.OwnerId == ownerId
         select m).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Measurement>> ListAsync(Guid projectId, Guid? variantId, Guid? targetObjectId, CancellationToken ct) =>
        await db.Measurements
            .Where(m => m.ProjectId == projectId
                        && (variantId == null || m.VariantId == variantId)
                        && (targetObjectId == null || m.TargetObjectId == targetObjectId))
            .OrderBy(m => m.Id)
            .ToListAsync(ct);

    public void Add(Measurement measurement) => db.Measurements.Add(measurement);
}

public sealed class EfExportJobRepository(BuildingDraftDbContext db) : IExportJobRepository
{
    public Task<ExportJob?> FindAsync(Guid jobId, Guid ownerId, CancellationToken ct) =>
        (from j in db.ExportJobs
         join p in db.Projects on j.ProjectId equals p.Id
         where j.Id == jobId && p.OwnerId == ownerId
         select j).FirstOrDefaultAsync(ct);

    public void Add(ExportJob job) => db.ExportJobs.Add(job);
}

public sealed class EfProjectFileRepository(BuildingDraftDbContext db) : IProjectFileRepository
{
    public Task<ProjectFile?> FindAsync(Guid fileId, CancellationToken ct) =>
        db.ProjectFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct);

    public void Add(ProjectFile file) => db.ProjectFiles.Add(file);
}

public sealed class EfScanSessionRepository(BuildingDraftDbContext db) : IScanSessionRepository
{
    public Task<ScanSession?> FindAsync(Guid sessionId, Guid ownerId, CancellationToken ct) =>
        (from s in db.ScanSessions
         join p in db.Projects on s.ProjectId equals p.Id
         where s.Id == sessionId && p.OwnerId == ownerId
         select s).FirstOrDefaultAsync(ct);

    public void Add(ScanSession session) => db.ScanSessions.Add(session);
}

public sealed class EfUnitOfWork(BuildingDraftDbContext db) : IUnitOfWork
{
    const string UniqueViolation = "23505";

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException
                                       || ex is DbUpdateException { InnerException: PostgresException { SqlState: UniqueViolation } })
        {
            throw new DomainException("VERSION_CONFLICT", "The data was changed by another request. Reload and retry.");
        }
    }
}
