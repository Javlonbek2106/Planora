namespace BuildingDraft.Domain.Geometry.Commands;

public sealed record MergeWalls(Guid WallId, Guid OtherWallId) : IGeometryCommand
{
    public void Apply(GeometryModel model, List<ValidationIssue> warnings)
    {
        var a = model.GetWall(WallId);
        var b = model.GetWall(OtherWallId);
        var s = model.SharedNode(a.Id, b.Id)
                ?? throw new DomainException("WALLS_NOT_CONNECTED", "Walls must share exactly one node.");
        if (model.WallsAt(s).Count() != 2)
            throw new DomainException("JUNCTION_NOT_MERGEABLE", "Other walls are connected at the shared node.");
        if (a.ThicknessMm != b.ThicknessMm || a.HeightMm != b.HeightMm || a.Classification != b.Classification)
            throw new DomainException("WALL_PROPERTIES_DIFFER", "Walls must have the same thickness, height and classification.");

        var x = GeometryModel.OtherNode(a, s);
        var y = GeometryModel.OtherNode(b, s);
        var u = model.Pos(x) - model.Pos(s);
        var v = model.Pos(y) - model.Pos(s);
        if (Math.Abs(Vec2.Cross(u, v)) > 1e-6 * u.Length * v.Length || Vec2.Dot(u, v) >= 0)
            throw new DomainException("WALLS_NOT_COLLINEAR", "Walls must be collinear to be merged.");

        var lenA = u.Length;
        var lenB = v.Length;
        foreach (var o in model.Openings.Values.Where(o => o.WallId == a.Id || o.WallId == b.Id).ToList())
        {
            // Position along the merged wall, measured from node x.
            var fromX = o.WallId == a.Id
                ? (a.StartNodeId == x ? o.OffsetMm : lenA - o.OffsetMm - o.WidthMm)
                : lenA + (b.StartNodeId == s ? o.OffsetMm : lenB - o.OffsetMm - o.WidthMm);
            model.Openings[o.Id] = o with { WallId = a.Id, OffsetMm = fromX };
        }

        var dropped = new HashSet<Guid> { b.Id, s };
        CommandHelpers.RemoveConstraints(model, warnings, "the walls were merged",
            c => c.Targets.Any(dropped.Contains) || (c.Targets.Contains(a.Id) && c.Type is ConstraintType.FixedLength or ConstraintType.FixedAngle));

        model.Walls[a.Id] = a with { StartNodeId = x, EndNodeId = y };
        model.Walls.Remove(b.Id);
        model.Nodes.Remove(s);
        CommandHelpers.ReplaceInRooms(model, room => room.BoundaryWallIds.Contains(b.Id) ? [..room.BoundaryWallIds.Where(id => id != b.Id)] : null);
    }
}
