namespace BuildingDraft.Domain.Geometry.Commands;

public sealed record ScanNode(Guid Id, double XMm, double YMm);

public sealed record ScanWall(Guid Id, Guid StartNodeId, Guid EndNodeId, double ThicknessMm, double HeightMm);

public sealed record ScanOpening(Guid Id, Guid WallId, OpeningKind Kind, double OffsetMm, double WidthMm, double HeightMm, double SillMm = 0);

// Adds a whole reviewed scan to a floor in one step, so it becomes a single geometry version.
public sealed record ImportScan(
    Guid FloorId,
    IReadOnlyList<ScanNode> Nodes,
    IReadOnlyList<ScanWall> Walls,
    IReadOnlyList<ScanOpening> Openings) : IGeometryCommand
{
    public void Apply(GeometryModel model, List<ValidationIssue> warnings)
    {
        if (Walls.Count == 0)
            throw new DomainException("EMPTY_SCAN", "The scan contains no walls.");

        foreach (var n in Nodes)
        {
            if (model.Nodes.ContainsKey(n.Id))
                throw new DomainException("ID_EXISTS", $"Node {n.Id} already exists.");
            model.Nodes[n.Id] = new(n.Id, FloorId, Math.Round(n.XMm, 3), Math.Round(n.YMm, 3));
        }

        foreach (var w in Walls)
        {
            if (model.Walls.ContainsKey(w.Id))
                throw new DomainException("WALL_EXISTS", $"Wall {w.Id} already exists.");
            model.Walls[w.Id] = new(w.Id, w.StartNodeId, w.EndNodeId, w.ThicknessMm, w.HeightMm, WallClassification.Existing);
        }

        foreach (var o in Openings)
        {
            if (model.Openings.ContainsKey(o.Id))
                throw new DomainException("OPENING_EXISTS", $"Opening {o.Id} already exists.");
            model.Openings[o.Id] = new(o.Id, o.WallId, o.Kind, "single", o.OffsetMm, o.WidthMm, o.HeightMm, o.SillMm);
        }
    }
}
