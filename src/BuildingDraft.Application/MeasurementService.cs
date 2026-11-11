using System.Text.Json;
using BuildingDraft.Domain;
using BuildingDraft.Domain.Abstractions;
using BuildingDraft.Domain.Geometry.Commands;
using BuildingDraft.Domain.Measurements;

namespace BuildingDraft.Application;

public sealed class MeasurementService(
    IProjectRepository projects,
    IMeasurementRepository measurements,
    IGeometryVersionRepository versions,
    GeometryService geometry,
    IUnitOfWork unitOfWork,
    ICurrentUser user,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<MeasurementDto>> ListAsync(Guid projectId, Guid? variantId, Guid? targetObjectId, CancellationToken ct)
    {
        await projects.GetAsync(projectId, user.UserId, ct);
        return [..(await measurements.ListAsync(projectId, variantId, targetObjectId, ct)).Select(MeasurementDto.From)];
    }

    public async Task<MeasurementDto> CreateAsync(Guid projectId, CreateMeasurementRequest request, CancellationToken ct)
    {
        var project = await projects.GetAsync(projectId, user.UserId, ct);
        var variant = project.GetVariant(request.VariantId);

        var current = await versions.FindByNumberAsync(variant.Id, variant.CurrentVersionNumber, ct)
                      ?? throw new DomainException("NOT_FOUND", "Current geometry version not found.");
        if (!(await versions.LoadModelAsync(current.Id, ct)).Walls.ContainsKey(request.TargetObjectId))
            throw new DomainException("TARGET_NOT_FOUND", "The measured wall does not exist in this variant.");

        var measurement = Measurement.Record(
            project.Id, variant.Id, request.TargetObjectId, request.ValueMm, request.Reference, request.Source,
            request.Confidence, request.DeviceName, request.Notes, request.CapturedAt ?? clock.GetUtcNow());
        measurements.Add(measurement);
        await unitOfWork.SaveChangesAsync(ct);
        return MeasurementDto.From(measurement);
    }

    public async Task<MeasurementDto> ChangeStatusAsync(Guid measurementId, ChangeMeasurementStatusRequest request, CancellationToken ct)
    {
        var measurement = await measurements.GetAsync(measurementId, user.UserId, ct);
        measurement.ChangeStatus(request.Status);
        await unitOfWork.SaveChangesAsync(ct);
        return MeasurementDto.From(measurement);
    }

    // The measurement bookkeeping and the geometry change share one unit of work: geometry.ExecuteAsync
    // saves both together, and a rejected command saves neither.
    public async Task<CommandResultDto> ApplyAsync(Guid measurementId, ApplyMeasurementRequest request, CancellationToken ct)
    {
        var measurement = await measurements.GetAsync(measurementId, user.UserId, ct);
        measurement.EnsureApplicable();

        var earlier = await measurements.ListAsync(measurement.ProjectId, measurement.VariantId, measurement.TargetObjectId, ct);
        foreach (var old in earlier.Where(m => m.Id != measurement.Id && m.IsApplied && m.Status != MeasurementStatus.Rejected))
            old.Supersede();
        measurement.MarkApplied(request.ExpectedVersion + 1);

        var command = new ApplyMeasurement(measurement.TargetObjectId, measurement.ValueMm, measurement.Reference);
        var payload = JsonSerializer.SerializeToElement(command, GeometryCommandCatalog.Json);
        return await geometry.ExecuteAsync(measurement.VariantId,
            new(request.ClientOperationId, request.ExpectedVersion, "apply_measurement", payload, request.DeviceId), ct);
    }
}
