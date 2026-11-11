namespace BuildingDraft.Domain.Geometry.Commands;

public sealed record SplitWall(Guid WallId, Guid NewWallId, Guid NewNodeId, double AtMm) : IGeometryCommand
{
    public void Apply(GeometryModel model, List<ValidationIssue> warnings)
    {
        var wall = model.GetWall(WallId);
        if (model.Walls.ContainsKey(NewWallId) || model.Nodes.ContainsKey(NewNodeId))
            throw new DomainException("ID_EXISTS", "NewWallId or NewNodeId already exists.");

        var start = model.Pos(wall.StartNodeId);
        var end = model.Pos(wall.EndNodeId);
        var length = (end - start).Length;
        if (AtMm < 1 || AtMm > length - 1)
            throw new DomainException("INVALID_SPLIT_POSITION", "Split position must be inside the wall (at least 1 mm from each end).");

        var floorId = model.Nodes[wall.StartNodeId].FloorId;
        var point = start + (end - start) / length * AtMm;
        model.Nodes[NewNodeId] = new(NewNodeId, floorId, Math.Round(point.X, 3), Math.Round(point.Y, 3));
        model.Walls[WallId] = wall with { EndNodeId = NewNodeId };
        model.Walls[NewWallId] = wall with { Id = NewWallId, StartNodeId = NewNodeId };

        foreach (var opening in model.Openings.Values.Where(o => o.WallId == WallId).ToList())
        {
            if (opening.OffsetMm + opening.WidthMm <= AtMm)
                continue;
            if (opening.OffsetMm < AtMm)
                throw new DomainException("OPENING_STRADDLES_SPLIT", $"Opening {opening.Id} crosses the split position.");
            model.Openings[opening.Id] = opening with { WallId = NewWallId, OffsetMm = opening.OffsetMm - AtMm };
        }

        CommandHelpers.ReplaceInRooms(model, room => room.BoundaryWallIds.Contains(WallId) ? [..room.BoundaryWallIds, NewWallId] : null);

        CommandHelpers.RemoveConstraints(model, warnings, "the wall was split",
            c => c.Targets.Contains(WallId) && c.Type is ConstraintType.FixedLength or ConstraintType.FixedAngle);

        // Direction-only constraints stay valid for the collinear second half.
        foreach (var c in model.Constraints.Where(c => c.Targets.Contains(WallId)
                     && c.Type is ConstraintType.Horizontal or ConstraintType.Vertical
                         or ConstraintType.Parallel or ConstraintType.Perpendicular).ToList())
            model.Constraints.Add(c with
            {
                Id = DeterministicGuid.From(c.Id, NewWallId.ToString()),
                Targets = [..c.Targets.Select(t => t == WallId ? NewWallId : t)]
            });
    }
}
