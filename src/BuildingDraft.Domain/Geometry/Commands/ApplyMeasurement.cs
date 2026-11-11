namespace BuildingDraft.Domain.Geometry.Commands;

// Inner dimensions are converted to centerline assuming square corners and equal wall thickness at both ends.
public sealed record ApplyMeasurement(
    Guid WallId,
    double ValueMm,
    MeasurementReference Reference = MeasurementReference.Inner) : IGeometryCommand
{
    public void Apply(GeometryModel model, List<ValidationIssue> warnings)
    {
        var wall = model.GetWall(WallId);
        if (ValueMm <= 0)
            throw new DomainException("INVALID_MEASUREMENT", "Measurement must be positive.");

        var centerline = Reference == MeasurementReference.Inner ? ValueMm + wall.ThicknessMm : ValueMm;
        new ResizeWall(WallId, centerline).Apply(model, warnings);

        var constraintId = DeterministicGuid.From(WallId, "fixed_length");
        model.Constraints.RemoveAll(c => c.Id == constraintId);
        model.Constraints.Add(new(constraintId, ConstraintType.FixedLength, [WallId], Math.Round(centerline, 3)));
    }
}
