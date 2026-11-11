namespace BuildingDraft.Domain.Geometry.Commands;

public sealed record CreateWall(
    Guid WallId,
    Guid FloorId,
    Guid StartNodeId,
    double StartXMm,
    double StartYMm,
    Guid EndNodeId,
    double EndXMm,
    double EndYMm,
    double ThicknessMm = 200,
    double HeightMm = 2700,
    WallClassification Classification = WallClassification.Existing) : IGeometryCommand
{
    public void Apply(GeometryModel model, List<ValidationIssue> warnings)
    {
        if (model.Walls.ContainsKey(WallId))
            throw new DomainException("WALL_EXISTS", $"Wall {WallId} already exists.");
        if (StartNodeId == EndNodeId)
            throw new DomainException("WALL_ZERO_LENGTH", "A wall needs two different nodes.");

        CommandHelpers.EnsureNode(model, StartNodeId, FloorId, StartXMm, StartYMm);
        CommandHelpers.EnsureNode(model, EndNodeId, FloorId, EndXMm, EndYMm);
        model.Walls[WallId] = new(WallId, StartNodeId, EndNodeId, ThicknessMm, HeightMm, Classification);
    }
}
