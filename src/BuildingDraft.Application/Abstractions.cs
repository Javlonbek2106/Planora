using BuildingDraft.Domain;
using BuildingDraft.Domain.Abstractions;
using BuildingDraft.Domain.Measurements;
using BuildingDraft.Domain.Projects;

namespace BuildingDraft.Application;

public interface ICurrentUser
{
    Guid UserId { get; }
}

// Ownership is part of every lookup, so another user's data is indistinguishable from missing data.
static class RepositoryExtensions
{
    public static async Task<Project> GetAsync(this IProjectRepository repository, Guid projectId, Guid ownerId, CancellationToken ct) =>
        await repository.FindAsync(projectId, ownerId, ct) ?? throw NotFound("Project");

    public static async Task<Project> GetByVariantAsync(this IProjectRepository repository, Guid variantId, Guid ownerId, CancellationToken ct) =>
        await repository.FindByVariantAsync(variantId, ownerId, ct) ?? throw NotFound("Variant");

    public static async Task<Project> GetByFloorAsync(this IProjectRepository repository, Guid floorId, Guid ownerId, CancellationToken ct) =>
        await repository.FindByFloorAsync(floorId, ownerId, ct) ?? throw NotFound("Floor");

    public static async Task<Measurement> GetAsync(this IMeasurementRepository repository, Guid measurementId, Guid ownerId, CancellationToken ct) =>
        await repository.FindAsync(measurementId, ownerId, ct) ?? throw NotFound("Measurement");

    static DomainException NotFound(string what) => new("NOT_FOUND", $"{what} not found.");
}
