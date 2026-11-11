namespace BuildingDraft.Domain.Geometry.Commands;

// Lengths are centerline lengths; ApplyMeasurement converts inner dimensions first.
public sealed record ResizeWall(
    Guid WallId,
    double NewLengthMm,
    DimensionReference DimensionReference = DimensionReference.StartFixed) : IGeometryCommand
{
    public void Apply(GeometryModel model, List<ValidationIssue> warnings)
    {
        var wall = model.GetWall(WallId);
        var start = model.Pos(wall.StartNodeId);
        var end = model.Pos(wall.EndNodeId);
        var current = (end - start).Length;
        if (NewLengthMm <= 0)
            throw new DomainException("INVALID_LENGTH", "Wall length must be positive.");
        if (current < 1e-9)
            throw new DomainException("WALL_ZERO_LENGTH", "Cannot resize a zero-length wall.");

        var dir = (end - start) / current;
        switch (DimensionReference)
        {
            case DimensionReference.StartFixed:
                model.SetPos(wall.EndNodeId, start + dir * NewLengthMm);
                break;
            case DimensionReference.EndFixed:
                model.SetPos(wall.StartNodeId, end - dir * NewLengthMm);
                break;
            default:
                var mid = (start + end) / 2;
                model.SetPos(wall.StartNodeId, mid - dir * (NewLengthMm / 2));
                model.SetPos(wall.EndNodeId, mid + dir * (NewLengthMm / 2));
                break;
        }
    }
}
