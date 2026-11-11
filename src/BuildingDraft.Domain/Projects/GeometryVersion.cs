namespace BuildingDraft.Domain.Projects;

// An immutable snapshot of one variant's geometry; the content itself lives in the geometry store.
public sealed class GeometryVersion
{
    GeometryVersion()
    {
    }

    public Guid Id { get; private set; }
    public Guid VariantId { get; private set; }
    public int VersionNumber { get; private set; }
    public Guid? ParentVersionId { get; private set; }
    public string Hash { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    public static GeometryVersion Create(Guid variantId, int versionNumber, Guid? parentVersionId, string hash, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            VariantId = variantId,
            VersionNumber = versionNumber,
            ParentVersionId = parentVersionId,
            Hash = hash,
            CreatedAt = now
        };
}

// The log of applied commands. ClientOperationId makes retries and offline sync idempotent.
public sealed class GeometryOperation
{
    GeometryOperation()
    {
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid VariantId { get; private set; }
    public Guid VersionId { get; private set; }
    public int ResultVersionNumber { get; private set; }
    public Guid ClientOperationId { get; private set; }
    public string Command { get; private set; } = "";
    public string Payload { get; private set; } = "{}";
    public string? DeviceId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static GeometryOperation Record(Guid projectId, GeometryVersion version, Guid clientOperationId, string command, string payload, string? deviceId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            VariantId = version.VariantId,
            VersionId = version.Id,
            ResultVersionNumber = version.VersionNumber,
            ClientOperationId = clientOperationId,
            Command = command,
            Payload = payload,
            DeviceId = deviceId,
            CreatedAt = now
        };
}
