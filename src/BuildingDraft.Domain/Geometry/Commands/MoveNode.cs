namespace BuildingDraft.Domain.Geometry.Commands;

// Drags one corner to an absolute position. Every wall that uses the node follows; constraints re-solve around it.
public sealed record MoveNode(Guid NodeId, double XMm, double YMm) : IGeometryCommand
{
    public void Apply(GeometryModel model, List<ValidationIssue> warnings)
    {
        if (!model.Nodes.ContainsKey(NodeId))
            throw new DomainException("NODE_NOT_FOUND", $"Node {NodeId} not found.");
        model.SetPos(NodeId, new Vec2(XMm, YMm));
    }
}
