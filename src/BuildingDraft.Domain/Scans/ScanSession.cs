using BuildingDraft.Domain.Measurements;

namespace BuildingDraft.Domain.Scans;

public enum ScanStatus { Created, Processed, Applied }

// One AR/LiDAR or AI capture on its way into a variant: created, processed into a proposal, then
// applied only after the user confirmed it. Everything it produces is an estimate.
public sealed class ScanSession
{
    ScanSession()
    {
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid VariantId { get; private set; }
    public Guid FloorId { get; private set; }
    public MeasurementSource Source { get; private set; }
    public ScanStatus Status { get; private set; }
    public string? DeviceModel { get; private set; }
    public double? Confidence { get; private set; }
    public string? Proposal { get; private set; }
    public int? AppliedVersionNumber { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static ScanSession Create(Guid projectId, Guid variantId, Guid floorId, MeasurementSource source, DateTimeOffset now)
    {
        if (source is not (MeasurementSource.ArLidar or MeasurementSource.AiRecognition))
            throw new DomainException("INVALID_SCAN_SOURCE", "A scan session must come from AR/LiDAR or AI recognition.");

        return new()
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            VariantId = variantId,
            FloorId = floorId,
            Source = source,
            Status = ScanStatus.Created,
            CreatedAt = now
        };
    }

    // Processing again replaces the proposal until it has been applied.
    public void Process(string proposalJson, double confidence, string? deviceModel)
    {
        if (Status == ScanStatus.Applied)
            throw new DomainException("SCAN_ALREADY_APPLIED", "This scan has already been applied.");
        if (confidence is < 0 or > 1)
            throw new DomainException("INVALID_CONFIDENCE", "Confidence must be between 0 and 1.");

        (Proposal, Confidence, DeviceModel, Status) = (proposalJson, confidence, deviceModel?.Trim(), ScanStatus.Processed);
    }

    public void EnsureApplicable()
    {
        if (Status == ScanStatus.Applied)
            throw new DomainException("SCAN_ALREADY_APPLIED", "This scan has already been applied.");
        if (Status != ScanStatus.Processed)
            throw new DomainException("SCAN_NOT_PROCESSED", "Process the scan and review the result before applying it.");
    }

    public void MarkApplied(int versionNumber) => (Status, AppliedVersionNumber) = (ScanStatus.Applied, versionNumber);
}
