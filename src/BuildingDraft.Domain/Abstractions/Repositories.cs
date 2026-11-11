using BuildingDraft.Domain.Export;
using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Measurements;
using BuildingDraft.Domain.Projects;
using BuildingDraft.Domain.Scans;
using BuildingDraft.Domain.Users;

namespace BuildingDraft.Domain.Abstractions;

// Ports owned by the domain; Infrastructure provides the adapters (Dependency Inversion).
public interface IProjectRepository
{
    Task<Project?> FindAsync(Guid projectId, Guid ownerId, CancellationToken ct);
    Task<Project?> FindByVariantAsync(Guid variantId, Guid ownerId, CancellationToken ct);
    Task<Project?> FindByFloorAsync(Guid floorId, Guid ownerId, CancellationToken ct);
    Task<(IReadOnlyList<Project> Items, int Total)> ListAsync(Guid ownerId, int page, int pageSize, CancellationToken ct);
    void Add(Project project);
}

public interface IGeometryVersionRepository
{
    Task<GeometryVersion?> FindAsync(Guid versionId, CancellationToken ct);
    Task<GeometryVersion?> FindByNumberAsync(Guid variantId, int versionNumber, CancellationToken ct);
    Task<IReadOnlyList<GeometryVersion>> ListAsync(Guid variantId, CancellationToken ct);
    Task<GeometryModel> LoadModelAsync(Guid versionId, CancellationToken ct);
    void Add(GeometryVersion version, GeometryModel model);
}

public interface IGeometryOperationRepository
{
    Task<GeometryOperation?> FindByClientIdAsync(Guid clientOperationId, CancellationToken ct);
    Task<IReadOnlyList<GeometryOperation>> ListSinceAsync(Guid variantId, int afterVersion, CancellationToken ct);
    void Add(GeometryOperation operation);
}

public interface IUnitOfWork
{
    // Throws DomainException(VERSION_CONFLICT) when another writer won the race.
    Task SaveChangesAsync(CancellationToken ct);
}

public interface IMeasurementRepository
{
    Task<Measurement?> FindAsync(Guid measurementId, Guid ownerId, CancellationToken ct);
    Task<IReadOnlyList<Measurement>> ListAsync(Guid projectId, Guid? variantId, Guid? targetObjectId, CancellationToken ct);
    void Add(Measurement measurement);
}

public interface IExportJobRepository
{
    Task<ExportJob?> FindAsync(Guid jobId, Guid ownerId, CancellationToken ct);
    void Add(ExportJob job);
}

public interface IProjectFileRepository
{
    Task<ProjectFile?> FindAsync(Guid fileId, CancellationToken ct);
    void Add(ProjectFile file);
}

public interface IFileStorage
{
    Task SaveAsync(string key, byte[] content, CancellationToken ct);
    Task<byte[]?> ReadAsync(string key, CancellationToken ct);
}

public interface IScanSessionRepository
{
    Task<ScanSession?> FindAsync(Guid sessionId, Guid ownerId, CancellationToken ct);
    void Add(ScanSession session);
}

public interface IUserRepository
{
    Task<User?> FindBySubjectAsync(string externalSubject, CancellationToken ct);
    void Add(User user);
}
