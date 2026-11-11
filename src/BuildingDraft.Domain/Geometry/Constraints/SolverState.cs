namespace BuildingDraft.Domain.Geometry.Constraints;

// Working positions of one solve run plus the primitive moves that constraint rules compose.
public sealed class SolverState(GeometryModel model, Dictionary<Guid, Vec2> pos)
{
    readonly HashSet<Guid> pinned = [];

    public GeometryModel Model => model;

    public Vec2 this[Guid node]
    {
        get => pos[node];
        set => pos[node] = value;
    }

    public IEnumerable<KeyValuePair<Guid, Vec2>> Positions => pos;

    public static SolverState From(GeometryModel model) =>
        new(model, model.Nodes.ToDictionary(n => n.Key, n => new Vec2(n.Value.X, n.Value.Y)));

    public void Pin(Guid node, Vec2 at)
    {
        pos[node] = at;
        pinned.Add(node);
    }

    public (Guid Start, Guid End) Ends(Guid wallId)
    {
        var wall = model.Walls[wallId];
        return (wall.StartNodeId, wall.EndNodeId);
    }

    public Vec2 Direction(Guid wallId)
    {
        var (s, e) = Ends(wallId);
        return pos[e] - pos[s];
    }

    public static double Wrap(double angle, double period) => angle - period * Math.Round(angle / period);

    public void SetDistance(Guid a, Guid b, double distance)
    {
        var (wa, wb) = (Weight(a), Weight(b));
        if (wa + wb == 0)
            return;
        var delta = pos[b] - pos[a];
        var length = delta.Length;
        var dir = length < 1e-9 ? new Vec2(1, 0) : delta / length;
        var diff = length - distance;
        pos[a] += dir * (diff * wa / (wa + wb));
        pos[b] -= dir * (diff * wb / (wa + wb));
    }

    public void AlignY(Guid a, Guid b)
    {
        var (wa, wb) = (Weight(a), Weight(b));
        if (wa + wb == 0)
            return;
        var d = pos[b].Y - pos[a].Y;
        pos[a] = pos[a] with { Y = pos[a].Y + d * wa / (wa + wb) };
        pos[b] = pos[b] with { Y = pos[b].Y - d * wb / (wa + wb) };
    }

    public void AlignX(Guid a, Guid b)
    {
        var (wa, wb) = (Weight(a), Weight(b));
        if (wa + wb == 0)
            return;
        var d = pos[b].X - pos[a].X;
        pos[a] = pos[a] with { X = pos[a].X + d * wa / (wa + wb) };
        pos[b] = pos[b] with { X = pos[b].X - d * wb / (wa + wb) };
    }

    public void RotateWallAboutMidpoint(Guid wallId, double angle)
    {
        var (s, e) = Ends(wallId);
        var mid = (pos[s] + pos[e]) / 2;
        Blend(s, mid + (pos[s] - mid).Rotate(angle));
        Blend(e, mid + (pos[e] - mid).Rotate(angle));
    }

    public void Blend(Guid node, Vec2 target) => pos[node] += (target - pos[node]) * Weight(node);

    double Weight(Guid node) => pinned.Contains(node) ? 0 : 1;
}
