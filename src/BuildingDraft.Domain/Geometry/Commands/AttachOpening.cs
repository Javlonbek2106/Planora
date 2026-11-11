namespace BuildingDraft.Domain.Geometry.Commands;

public sealed record AttachOpening(
    Guid OpeningId,
    Guid WallId,
    OpeningKind Kind,
    double OffsetMm,
    double WidthMm,
    double HeightMm,
    double SillMm = 0,
    string Subtype = "single") : IGeometryCommand
{
    public void Apply(GeometryModel model, List<ValidationIssue> warnings)
    {
        model.GetWall(WallId);
        if (model.Openings.ContainsKey(OpeningId))
            throw new DomainException("OPENING_EXISTS", $"Opening {OpeningId} already exists.");
        model.Openings[OpeningId] = new(OpeningId, WallId, Kind, Subtype, OffsetMm, WidthMm, HeightMm, SillMm);
    }
}
