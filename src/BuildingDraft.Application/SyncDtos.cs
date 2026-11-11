using System.Text.Json;
using BuildingDraft.Domain.Geometry;

namespace BuildingDraft.Application;

public enum SyncStatus { Acknowledged, Conflict, Failed }

public enum ConflictResolution { KeepServer, KeepMine }

public sealed record SyncOperation(Guid OperationId, Guid VariantId, int ExpectedVersion, string CommandType, JsonElement Payload, DateTimeOffset? CreatedAt = null);

public sealed record SyncPushRequest(IReadOnlyList<SyncOperation> Operations, string? DeviceId = null);

public sealed record SyncOperationResult(
    Guid OperationId,
    SyncStatus Status,
    int? Version = null,
    int? CurrentVersion = null,
    string? ErrorCode = null,
    string? Message = null,
    IReadOnlyList<ValidationIssue>? Warnings = null,
    IReadOnlyList<ValidationIssue>? Errors = null);

public sealed record SyncPushResponse(IReadOnlyList<SyncOperationResult> Results);

public sealed record VariantCursor(Guid VariantId, int Version);

public sealed record SyncPullRequest(IReadOnlyList<VariantCursor> Variants);

public sealed record SyncOperationDto(Guid OperationId, string CommandType, JsonElement Payload, int ResultVersion, DateTimeOffset CreatedAt);

// SnapshotRequired: the client cannot replay the changes (history was restored or is incomplete) and must reload the geometry.
public sealed record VariantChanges(
    Guid VariantId,
    bool Found,
    int CurrentVersion,
    bool SnapshotRequired,
    IReadOnlyList<SyncOperationDto> Operations);

public sealed record SyncPullResponse(IReadOnlyList<VariantChanges> Variants);

public sealed record ResolveConflictRequest(
    Guid VariantId,
    ConflictResolution Resolution,
    IReadOnlyList<SyncOperation>? Operations = null,
    string? DeviceId = null);

public sealed record ResolveConflictResponse(ConflictResolution Resolution, int CurrentVersion, IReadOnlyList<SyncOperationResult> Results);
