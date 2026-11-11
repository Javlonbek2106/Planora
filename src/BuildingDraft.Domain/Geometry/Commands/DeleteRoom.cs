namespace BuildingDraft.Domain.Geometry.Commands;

public sealed record DeleteRoom(Guid RoomId) : IGeometryCommand
{
    public void Apply(GeometryModel model, List<ValidationIssue> warnings)
    {
        model.GetRoom(RoomId);
        CommandHelpers.RemoveConstraints(model, warnings, "the room was deleted", c => c.Targets.Contains(RoomId));
        model.Rooms.Remove(RoomId);
    }
}
