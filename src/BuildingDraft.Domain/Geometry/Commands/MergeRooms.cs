namespace BuildingDraft.Domain.Geometry.Commands;

// The shared walls stay in the model as interior walls; they only leave the merged room's boundary.
public sealed record MergeRooms(Guid RoomId, Guid OtherRoomId, string? Name = null) : IGeometryCommand
{
    public void Apply(GeometryModel model, List<ValidationIssue> warnings)
    {
        var a = model.GetRoom(RoomId);
        var b = model.GetRoom(OtherRoomId);
        if (a.FloorId != b.FloorId)
            throw new DomainException("ROOMS_ON_DIFFERENT_FLOORS", "Rooms must be on the same floor.");

        var shared = a.BoundaryWallIds.Intersect(b.BoundaryWallIds).ToList();
        if (shared.Count == 0)
            throw new DomainException("ROOMS_NOT_ADJACENT", "Rooms must share at least one wall.");

        var merged = a with
        {
            Name = string.IsNullOrWhiteSpace(Name) ? a.Name : Name.Trim(),
            BoundaryWallIds = [..a.BoundaryWallIds.Concat(b.BoundaryWallIds).Where(id => !shared.Contains(id))]
        };
        model.Rooms[a.Id] = merged with { BoundaryWallIds = RoomGeometry.Reorder(model, merged) };
        model.Rooms.Remove(b.Id);
        CommandHelpers.RemoveConstraints(model, warnings, "the room was merged", c => c.Targets.Contains(b.Id));
        foreach (var id in shared)
            warnings.Add(new("SHARED_WALL_KEPT", "Wall between the merged rooms was kept as an interior wall.", id));
    }
}
