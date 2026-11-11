namespace BuildingDraft.Domain.Geometry.Commands;

public sealed record RemoveConstraint(Guid ConstraintId) : IGeometryCommand
{
    public void Apply(GeometryModel model, List<ValidationIssue> warnings)
    {
        if (model.Constraints.RemoveAll(c => c.Id == ConstraintId) == 0)
            throw new DomainException("CONSTRAINT_NOT_FOUND", $"Constraint {ConstraintId} not found.");
    }
}
