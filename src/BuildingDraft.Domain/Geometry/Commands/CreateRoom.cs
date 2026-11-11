namespace BuildingDraft.Domain.Geometry.Commands;

public sealed record CreateRoom(
    Guid RoomId,
    Guid FloorId,
    string Name,
    IReadOnlyList<Guid> WallIds,
    string RoomType = "other",
    RoomAreaType AreaType = RoomAreaType.Enclosed) : IGeometryCommand
{
    public void Apply(GeometryModel model, List<ValidationIssue> warnings)
    {
        if (model.Rooms.ContainsKey(RoomId))
            throw new DomainException("ROOM_EXISTS", $"Room {RoomId} already exists.");
        if (string.IsNullOrWhiteSpace(Name))
            throw new DomainException("INVALID_ROOM_NAME", "Room name is required.");

        var room = new Room(RoomId, FloorId, Name.Trim(), RoomType, AreaType, WallIds);
        model.Rooms[RoomId] = room with { BoundaryWallIds = RoomGeometry.Reorder(model, room) };
    }
}
