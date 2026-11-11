using BuildingDraft.Domain.Abstractions;
using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Projects;

namespace BuildingDraft.Application;

public sealed record StagedOperation(Guid ClientOperationId, int ExpectedVersion, string CommandType, string Payload, string? DeviceId);

// Creates the next geometry version and its operation record. It only stages them in the unit of work;
// callers decide when to save, so several versions can be committed together.
public sealed class GeometryVersionWriter(
    IGeometryVersionRepository versions,
    IGeometryOperationRepository operations,
    MeasurementRecorder measurementRecorder,
    TimeProvider clock)
{
    public async Task<GeometryVersion> StageAsync(Project project, DesignVariant variant, GeometryVersion parent, GeometryModel model, StagedOperation operation, CancellationToken ct)
    {
        project.EnsureFloorsExist(model.Nodes.Values.Select(n => n.FloorId).Concat(model.Rooms.Values.Select(r => r.FloorId)));

        // Anything that can refuse the operation is checked before the first thing is staged.
        var measurement = await measurementRecorder.PrepareAsync(project, variant, operation.CommandType, operation.Payload, ct);

        var now = clock.GetUtcNow();
        var next = variant.AdvanceVersion(operation.ExpectedVersion);
        var version = GeometryVersion.Create(variant.Id, next, parent.Id, model.ComputeHash(), now);
        versions.Add(version, model);
        operations.Add(GeometryOperation.Record(project.Id, version, operation.ClientOperationId, operation.CommandType,
            operation.Payload, operation.DeviceId, now));
        if (measurement is not null)
            measurementRecorder.Commit(measurement, version);
        return version;
    }
}
