namespace BuildingDraft.Domain.Geometry.Commands;

public sealed record AddConstraint(
    Guid ConstraintId,
    ConstraintType Type,
    IReadOnlyList<Guid> Targets,
    double? Value = null,
    double? Value2 = null) : IGeometryCommand
{
    public void Apply(GeometryModel model, List<ValidationIssue> warnings)
    {
        if (model.Constraints.Any(c => c.Id == ConstraintId))
            throw new DomainException("CONSTRAINT_EXISTS", $"Constraint {ConstraintId} already exists.");

        var value = Value;
        var value2 = Value2;
        // A fixed point without coordinates pins the node where it is now.
        if (Type == ConstraintType.FixedPoint && value is null && Targets.Count == 1 && model.Nodes.ContainsKey(Targets[0]))
            (value, value2) = (model.Nodes[Targets[0]].X, model.Nodes[Targets[0]].Y);

        model.Constraints.Add(new(ConstraintId, Type, Targets, value, value2));
    }
}
