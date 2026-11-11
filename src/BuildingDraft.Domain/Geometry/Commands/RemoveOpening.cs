namespace BuildingDraft.Domain.Geometry.Commands;

public sealed record RemoveOpening(Guid OpeningId) : IGeometryCommand
{
    public void Apply(GeometryModel model, List<ValidationIssue> warnings)
    {
        if (!model.Openings.Remove(OpeningId))
            throw new DomainException("OPENING_NOT_FOUND", $"Opening {OpeningId} not found.");
    }
}
