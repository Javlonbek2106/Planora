namespace BuildingDraft.Domain.Geometry.Commands;

static class CommandHelpers
{
    public static void EnsureNode(GeometryModel model, Guid id, Guid floorId, double x, double y)
    {
        if (model.Nodes.TryGetValue(id, out var existing))
        {
            if (existing.FloorId != floorId)
                throw new DomainException("NODE_FLOOR_MISMATCH", $"Node {id} belongs to a different floor.");
            return;
        }
        model.Nodes[id] = new(id, floorId, Math.Round(x, 3), Math.Round(y, 3));
    }

    public static void RemoveConstraints(GeometryModel model, List<ValidationIssue> warnings, string reason, Func<GeometryConstraint, bool> match)
    {
        foreach (var c in model.Constraints.Where(match).ToList())
        {
            model.Constraints.Remove(c);
            warnings.Add(new("CONSTRAINT_REMOVED", $"{c.Type} constraint removed: {reason}.", c.Id));
        }
    }

    public static void ReplaceInRooms(GeometryModel model, Func<Room, IReadOnlyList<Guid>?> newBoundary)
    {
        foreach (var room in model.Rooms.Values.ToList())
            if (newBoundary(room) is { } ids)
            {
                var updated = room with { BoundaryWallIds = ids };
                model.Rooms[room.Id] = updated with { BoundaryWallIds = RoomGeometry.Reorder(model, updated) };
            }
    }
}
