using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Measurements;
using BuildingDraft.Domain.Projects;

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
