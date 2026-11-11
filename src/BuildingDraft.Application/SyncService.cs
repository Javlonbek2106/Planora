using System.Text.Json;
using BuildingDraft.Domain;
using BuildingDraft.Domain.Abstractions;
using BuildingDraft.Domain.Geometry.Commands;
using BuildingDraft.Domain.Geometry.Pipeline;
using BuildingDraft.Domain.Projects;

namespace BuildingDraft.Application;

public sealed class SyncService(
    IProjectRepository projects,
    IGeometryVersionRepository versions,
    IGeometryOperationRepository operations,
    GeometryVersionWriter writer,
    GeometryProcessor processor,
    IUnitOfWork unitOfWork,
    ICurrentUser user)
{
    const string Blocked = "BLOCKED_BY_PREVIOUS_OPERATION";
    const string RolledBack = "ROLLED_BACK";

    // Operations of one variant are applied in order; the first conflict or failure stops that variant,
    // because later operations were recorded on top of a state the server never reached.
    public async Task<SyncPushResponse> PushAsync(SyncPushRequest request, CancellationToken ct)
    {
        var (results, applied) = await ApplyAsync(request.Operations, request.DeviceId, rebase: false, atomic: false, ct);
        if (applied.Count > 0)
            await unitOfWork.SaveChangesAsync(ct);
        return new(results);
    }

    public async Task<SyncPullResponse> PullAsync(SyncPullRequest request, CancellationToken ct)
    {
        var changes = new List<VariantChanges>();
        foreach (var cursor in request.Variants)
        {
            var project = await projects.FindByVariantAsync(cursor.VariantId, user.UserId, ct);
            var variant = project?.Variants.FirstOrDefault(v => v.Id == cursor.VariantId && !v.IsDeleted);
            if (variant is null)
            {
                changes.Add(new(cursor.VariantId, false, 0, false, []));
                continue;
            }

            var current = variant.CurrentVersionNumber;
            if (cursor.Version == current)
            {
                changes.Add(new(variant.Id, true, current, false, []));
                continue;
            }

            var log = await operations.ListSinceAsync(variant.Id, cursor.Version, ct);
            var replayable = cursor.Version < current
                             && log.Count == current - cursor.Version
                             && log.All(o => o.Command != GeometryOperation.RestoreCommand);
            changes.Add(new(variant.Id, true, current, !replayable,
                replayable ? [..log.Select(o => new SyncOperationDto(o.ClientOperationId, o.Command, JsonDocument.Parse(o.Payload).RootElement.Clone(), o.ResultVersionNumber, o.CreatedAt))] : []));
        }
        return new(changes);
    }

    public async Task<ResolveConflictResponse> ResolveAsync(ResolveConflictRequest request, CancellationToken ct)
    {
        var project = await projects.GetByVariantAsync(request.VariantId, user.UserId, ct);
        var variant = project.GetVariant(request.VariantId);

        if (request.Resolution == ConflictResolution.KeepServer)
            return new(request.Resolution, variant.CurrentVersionNumber, []);

        var operationsToApply = request.Operations ?? [];
        if (operationsToApply.Count == 0 || operationsToApply.Any(o => o.VariantId != request.VariantId))
            throw new DomainException("INVALID_RESOLUTION", "keep_mine needs at least one operation, all for the given variant.");

        // The user chose to keep their work: replay it on top of the server version, all or nothing.
        var (results, applied) = await ApplyAsync(operationsToApply, request.DeviceId, rebase: true, atomic: true, ct);
        if (applied.Count > 0 && results.All(r => r.Status == SyncStatus.Acknowledged))
            await unitOfWork.SaveChangesAsync(ct);
        return new(request.Resolution, results.LastOrDefault(r => r.Version is not null)?.Version ?? variant.CurrentVersionNumber, results);
    }

    async Task<(SyncOperationResult[] Results, HashSet<int> Applied)> ApplyAsync(
        IReadOnlyList<SyncOperation> ops, string? deviceId, bool rebase, bool atomic, CancellationToken ct)
    {
        var results = new SyncOperationResult[ops.Count];
        var applied = new HashSet<int>();
        var seenInBatch = new Dictionary<Guid, SyncOperationResult>();

        foreach (var group in ops.Select((op, index) => (op, index)).GroupBy(x => x.op.VariantId))
            await ApplyToVariantAsync(group.Key, [..group], deviceId, rebase, results, applied, seenInBatch, ct);

        if (atomic && results.Any(r => r.Status != SyncStatus.Acknowledged))
            foreach (var index in applied)
                results[index] = new(results[index].OperationId, SyncStatus.Failed, ErrorCode: RolledBack,
                    Message: "Another operation in the batch failed, so nothing was applied.");

        return (results, applied);
    }

    async Task ApplyToVariantAsync(
        Guid variantId, IReadOnlyList<(SyncOperation Op, int Index)> items, string? deviceId, bool rebase,
        SyncOperationResult[] results, HashSet<int> applied, Dictionary<Guid, SyncOperationResult> seenInBatch, CancellationToken ct)
    {
        Project project;
        DesignVariant variant;
        try
        {
            project = await projects.GetByVariantAsync(variantId, user.UserId, ct);
            variant = project.GetVariant(variantId);
        }
        catch (DomainException ex)
        {
            foreach (var (op, index) in items)
                results[index] = new(op.OperationId, SyncStatus.Failed, ErrorCode: ex.Code, Message: ex.Message);
            return;
        }

        var parent = await versions.FindByNumberAsync(variant.Id, variant.CurrentVersionNumber, ct)
                     ?? throw new DomainException("NOT_FOUND", "Current geometry version not found.");
        var model = await versions.LoadModelAsync(parent.Id, ct);
        SyncStatus? blockedWith = null;

        foreach (var (op, index) in items)
        {
            if (blockedWith is { } status)
            {
                results[index] = new(op.OperationId, status, CurrentVersion: variant.CurrentVersionNumber, ErrorCode: Blocked,
                    Message: "A previous operation for this variant was not applied.");
                continue;
            }

            if (seenInBatch.TryGetValue(op.OperationId, out var earlier))
            {
                results[index] = earlier;
                continue;
            }

            var done = await operations.FindByClientIdAsync(op.OperationId, ct);
            if (done is not null)
            {
                results[index] = done.VariantId == variantId
                    ? new(op.OperationId, SyncStatus.Acknowledged, Version: done.ResultVersionNumber)
                    : new(op.OperationId, SyncStatus.Failed, ErrorCode: "OPERATION_ID_REUSED", Message: "This operation id was already used for another variant.");
                if (results[index].Status != SyncStatus.Acknowledged)
                    blockedWith = results[index].Status;
                seenInBatch[op.OperationId] = results[index];
                continue;
            }

            try
            {
                variant.EnsureEditable();
                var expected = rebase ? variant.CurrentVersionNumber : op.ExpectedVersion;
                if (expected != variant.CurrentVersionNumber)
                {
                    results[index] = new(op.OperationId, SyncStatus.Conflict, CurrentVersion: variant.CurrentVersionNumber,
                        ErrorCode: "VERSION_CONFLICT", Message: $"Expected version {expected}, but the server is at {variant.CurrentVersionNumber}.");
                    blockedWith = SyncStatus.Conflict;
                    continue;
                }

                var outcome = processor.Execute(model, GeometryCommandCatalog.Parse(op.CommandType, op.Payload));
                if (!outcome.Success)
                    throw new GeometryRejectedException(outcome.Failure!);

                parent = writer.Stage(project, variant, parent, outcome.Model!,
                    new(op.OperationId, expected, op.CommandType, op.Payload.GetRawText(), deviceId));
                model = outcome.Model!;
                results[index] = new(op.OperationId, SyncStatus.Acknowledged, Version: parent.VersionNumber, Warnings: outcome.Warnings);
                applied.Add(index);
            }
            catch (DomainException ex)
            {
                results[index] = new(op.OperationId, SyncStatus.Failed, CurrentVersion: variant.CurrentVersionNumber, ErrorCode: ex.Code,
                    Message: ex.Message, Errors: (ex as GeometryRejectedException)?.Failure.Errors);
                blockedWith = SyncStatus.Failed;
            }

            seenInBatch[op.OperationId] = results[index];
        }
    }
}
