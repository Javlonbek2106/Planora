namespace BuildingDraft.Domain.Audit;

public static class AuditActions
{
    public const string ProjectCreated = "project.created";
    public const string ProjectUpdated = "project.updated";
    public const string ProjectArchived = "project.archived";
    public const string FloorAdded = "floor.added";
    public const string FloorUpdated = "floor.updated";
    public const string VariantCreated = "variant.created";
    public const string VariantRenamed = "variant.renamed";
    public const string VariantApproved = "variant.approved";
    public const string VariantDeleted = "variant.deleted";
    public const string GeometryRestored = "geometry.restored";
    public const string MeasurementRecorded = "measurement.recorded";
    public const string MeasurementStatusChanged = "measurement.status_changed";
    public const string MeasurementApplied = "measurement.applied";
    public const string ScanCreated = "scan.created";
    public const string ScanApplied = "scan.applied";
    public const string ExportRequested = "export.requested";
    public const string SyncConflictResolved = "sync.conflict_resolved";
}

// Who did what to which object. Geometry edits are not listed here: geometry_operations is their audit trail.
public sealed class AuditEntry
{
    AuditEntry()
    {
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid UserId { get; private set; }
    public string Action { get; private set; } = "";
    public string EntityType { get; private set; } = "";
    public Guid EntityId { get; private set; }
    public string? Details { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static AuditEntry Create(Guid projectId, Guid userId, string action, string entityType, Guid entityId, string? details, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            UserId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = details,
            CreatedAt = now
        };
}
