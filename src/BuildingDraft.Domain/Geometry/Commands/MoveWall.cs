namespace BuildingDraft.Domain.Geometry.Commands;

public sealed record MoveWall(Guid WallId, double DxMm, double DyMm) : IGeometryCommand
{
    public void Apply(GeometryModel model, List<ValidationIssue> warnings)
    {
        var wall = model.GetWall(WallId);
        var delta = new Vec2(DxMm, DyMm);
        model.SetPos(wall.StartNodeId, model.Pos(wall.StartNodeId) + delta);
        model.SetPos(wall.EndNodeId, model.Pos(wall.EndNodeId) + delta);
    }
}
