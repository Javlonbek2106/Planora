using System.Text.Json;
using BuildingDraft.Domain;
using BuildingDraft.Domain.Abstractions;
using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Geometry.Commands;
using BuildingDraft.Domain.Geometry.Pipeline;
using BuildingDraft.Domain.Projects;

namespace BuildingDraft.Application;

public sealed class GeometryService(
    IProjectRepository projects,
    IGeometryVersionRepository versions,
    IGeometryOperationRepository operations,
    IUnitOfWork unitOfWork,
    GeometryProcessor processor,
    ICurrentUser user,
    TimeProvider clock)
{
    const string RestoreCommand = "restore_geometry_version";

    public async Task<GeometryDto> GetAsync(Guid variantId, int? versionNumber, CancellationToken ct)
    {
        var (_, variant) = await OpenAsync(variantId, ct);
        var version = await versions.FindByNumberAsync(variantId, versionNumber ?? variant.CurrentVersionNumber, ct)
                      ?? throw new DomainException("NOT_FOUND", "Version not found.");
        return GeometryDto.From(await versions.LoadModelAsync(version.Id, ct), version.VersionNumber, version.Hash);
    }

    public async Task<ValidationDto> ValidateAsync(Guid variantId, CancellationToken ct)
    {
        var (_, variant) = await OpenAsync(variantId, ct);
        var (_, model) = await LoadCurrentAsync(variant, ct);
        var result = ValidationEngine.Validate(model);
        return new(result.IsValid, result.Warnings, result.Errors);
    }

    public async Task<IReadOnlyList<VersionDto>> ListVersionsAsync(Guid variantId, CancellationToken ct)
    {
        await OpenAsync(variantId, ct);
        return [..(await versions.ListAsync(variantId, ct)).Select(VersionDto.From)];
    }

    public async Task<CommandResultDto> ExecuteAsync(Guid variantId, ExecuteCommandRequest request, CancellationToken ct)
    {
        var (project, variant) = await OpenAsync(variantId, ct);
        if (await FindReplayAsync(variantId, request.ClientOperationId, ct) is { } replay)
            return replay;

        variant.EnsureEditable();
        variant.EnsureVersion(request.ExpectedVersion);

        var command = GeometryCommandCatalog.Parse(request.CommandType, request.Payload);
        var (parent, model) = await LoadCurrentAsync(variant, ct);
        var outcome = processor.Execute(model, command);
        if (!outcome.Success)
            throw new GeometryRejectedException(outcome.Failure!);

        return await CommitAsync(project, variant, parent, outcome.Model!, request.ClientOperationId, request.ExpectedVersion,
            request.CommandType, request.Payload.GetRawText(), request.DeviceId, outcome.Warnings, ct);
    }

    // Restoring never rewrites history: the old content becomes a new, latest version.
    public async Task<CommandResultDto> RestoreAsync(Guid variantId, Guid versionId, RestoreVersionRequest request, CancellationToken ct)
    {
        var (project, variant) = await OpenAsync(variantId, ct);
        if (await FindReplayAsync(variantId, request.ClientOperationId, ct) is { } replay)
            return replay;

        variant.EnsureEditable();
        variant.EnsureVersion(request.ExpectedVersion);

        var target = await versions.FindAsync(versionId, ct);
        if (target is null || target.VariantId != variantId)
            throw new DomainException("NOT_FOUND", "Version not found.");

        var (parent, _) = await LoadCurrentAsync(variant, ct);
        var model = await versions.LoadModelAsync(target.Id, ct);
        return await CommitAsync(project, variant, parent, model, request.ClientOperationId, request.ExpectedVersion,
            RestoreCommand, JsonSerializer.Serialize(new { versionId }), null, [], ct);
    }

    async Task<(Project Project, DesignVariant Variant)> OpenAsync(Guid variantId, CancellationToken ct)
    {
        var project = await projects.GetByVariantAsync(variantId, user.UserId, ct);
        return (project, project.GetVariant(variantId));
    }

    async Task<(GeometryVersion Version, GeometryModel Model)> LoadCurrentAsync(DesignVariant variant, CancellationToken ct)
    {
        var version = await versions.FindByNumberAsync(variant.Id, variant.CurrentVersionNumber, ct)
                      ?? throw new DomainException("NOT_FOUND", "Current geometry version not found.");
        return (version, await versions.LoadModelAsync(version.Id, ct));
    }

    async Task<CommandResultDto?> FindReplayAsync(Guid variantId, Guid clientOperationId, CancellationToken ct)
    {
        var done = await operations.FindByClientIdAsync(clientOperationId, ct);
        if (done is null)
            return null;
        return done.VariantId == variantId
            ? new(true, done.ResultVersionNumber, done.ClientOperationId, new(true, [], []))
            : throw new DomainException("OPERATION_ID_REUSED", "This operation id was already used for another variant.");
    }

    async Task<CommandResultDto> CommitAsync(
        Project project, DesignVariant variant, GeometryVersion parent, GeometryModel model,
        Guid clientOperationId, int expectedVersion, string commandType, string payload, string? deviceId,
        IReadOnlyList<ValidationIssue> warnings, CancellationToken ct)
    {
        project.EnsureFloorsExist(model.Nodes.Values.Select(n => n.FloorId).Concat(model.Rooms.Values.Select(r => r.FloorId)));

        var now = clock.GetUtcNow();
        var next = variant.AdvanceVersion(expectedVersion);
        var version = GeometryVersion.Create(variant.Id, next, parent.Id, model.ComputeHash(), now);
        versions.Add(version, model);
        operations.Add(GeometryOperation.Record(project.Id, version, clientOperationId, commandType, payload, deviceId, now));
        await unitOfWork.SaveChangesAsync(ct);

        return new(true, next, clientOperationId, new(true, warnings, []));
    }
}
