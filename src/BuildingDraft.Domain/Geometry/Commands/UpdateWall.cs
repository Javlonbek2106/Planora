namespace BuildingDraft.Domain.Geometry.Commands;

public sealed record UpdateWall(
    Guid WallId,
    double? ThicknessMm = null,
    double? HeightMm = null,
    WallClassification? Classification = null) : IGeometryCommand
{
    public void Apply(GeometryModel model, List<ValidationIssue> warnings)
    {
        var wall = model.GetWall(WallId);
        model.Walls[WallId] = wall with
        {
            ThicknessMm = ThicknessMm ?? wall.ThicknessMm,
            HeightMm = HeightMm ?? wall.HeightMm,
            Classification = Classification ?? wall.Classification
        };
    }
}
