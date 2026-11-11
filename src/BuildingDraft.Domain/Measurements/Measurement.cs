using BuildingDraft.Domain.Geometry;

namespace BuildingDraft.Domain.Measurements;

public enum MeasurementSource { Manual, LaserManual, LaserBluetooth, ArLidar, AiRecognition }

public enum MeasurementStatus { Unverified, Verified, Rejected, Superseded }

// A recorded wall length. The value never changes; only its status does (history is immutable).
public sealed class Measurement
{
    Measurement()
    {
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid VariantId { get; private set; }
    public Guid TargetObjectId { get; private set; }
    public string TargetProperty { get; private set; } = "length";
    public double ValueMm { get; private set; }
    public MeasurementReference Reference { get; private set; }
    public MeasurementSource Source { get; private set; }
    public MeasurementStatus Status { get; private set; }
    public double? Confidence { get; private set; }
    public string? DeviceName { get; private set; }
    public string? Notes { get; private set; }
    public DateTimeOffset CapturedAt { get; private set; }
    public bool IsApplied { get; private set; }
    public int? AppliedVersionNumber { get; private set; }

    // AR/LiDAR and AI results are estimates, never exact measurements.
    public bool IsEstimate => Source is MeasurementSource.ArLidar or MeasurementSource.AiRecognition;

    public static Measurement Record(
        Guid projectId, Guid variantId, Guid wallId, double valueMm, MeasurementReference reference,
        MeasurementSource source, double? confidence, string? deviceName, string? notes, DateTimeOffset capturedAt)
    {
        if (valueMm <= 0)
            throw new DomainException("INVALID_MEASUREMENT", "Measurement must be positive.");
        if (confidence is < 0 or > 1)
            throw new DomainException("INVALID_CONFIDENCE", "Confidence must be between 0 and 1.");

        var estimate = source is MeasurementSource.ArLidar or MeasurementSource.AiRecognition;
        if (estimate && confidence is null)
            throw new DomainException("INVALID_CONFIDENCE", "AR/LiDAR and AI measurements require a confidence value.");

        return new()
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            VariantId = variantId,
            TargetObjectId = wallId,
            ValueMm = valueMm,
            Reference = reference,
            Source = source,
            Status = MeasurementStatus.Unverified,
            Confidence = confidence,
            DeviceName = deviceName?.Trim(),
            Notes = notes?.Trim(),
            CapturedAt = capturedAt
        };
    }

    public void ChangeStatus(MeasurementStatus next)
    {
        if (next == Status)
            return;
        if (Status is MeasurementStatus.Rejected or MeasurementStatus.Superseded)
            throw new DomainException("MEASUREMENT_STATUS_FINAL", $"A {Status} measurement cannot change status.");
        if (next == MeasurementStatus.Unverified)
            throw new DomainException("INVALID_STATUS_TRANSITION", "A measurement cannot go back to unverified.");
        if (next == MeasurementStatus.Verified && IsEstimate)
            throw new DomainException("MEASUREMENT_NOT_VERIFIABLE", "AR/LiDAR and AI estimates cannot be verified. Measure with a laser or enter the value manually.");
        Status = next;
    }

    // Only exact measurements fix a length; estimates go through scan review instead.
    public void EnsureApplicable()
    {
        if (IsEstimate)
            throw new DomainException("MEASUREMENT_NOT_EXACT", "Estimates cannot be applied as exact lengths.");
        if (Status is MeasurementStatus.Rejected or MeasurementStatus.Superseded)
            throw new DomainException("MEASUREMENT_NOT_APPLICABLE", $"A {Status} measurement cannot be applied.");
    }

    public void MarkApplied(int versionNumber)
    {
        IsApplied = true;
        AppliedVersionNumber = versionNumber;
    }

    public void Supersede()
    {
        Status = MeasurementStatus.Superseded;
    }
}
