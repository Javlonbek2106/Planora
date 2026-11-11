namespace BuildingDraft.Domain.Geometry.Commands;

public sealed record DeleteWall(Guid WallId) : IGeometryCommand
{
    public void Apply(GeometryModel model, List<ValidationIssue> warnings)
    {
        var wall = model.GetWall(WallId);
        if (model.Rooms.Values.Any(r => r.BoundaryWallIds.Contains(WallId)))
            throw new DomainException("WALL_USED_BY_ROOM", "Delete or change the rooms bounded by this wall first.");

        foreach (var o in model.Openings.Values.Where(o => o.WallId == WallId).ToList())
            model.Openings.Remove(o.Id);
        CommandHelpers.RemoveConstraints(model, warnings, "the wall was deleted", c => c.Targets.Contains(WallId));
        model.Walls.Remove(WallId);

        foreach (var nodeId in new[] { wall.StartNodeId, wall.EndNodeId })
            if (!model.WallsAt(nodeId).Any())
            {
                CommandHelpers.RemoveConstraints(model, warnings, "the node was removed", c => c.Targets.Contains(nodeId));
                model.Nodes.Remove(nodeId);
            }
    }
}
