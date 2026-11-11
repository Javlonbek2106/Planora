namespace BuildingDraft.Domain.Geometry;

public sealed record RoomEdge(Wall Wall, Guid From, Guid To);

public sealed record RoomMetrics(double InnerAreaSqMm, double InnerPerimeterMm, double CenterlineAreaSqMm);

public static class RoomGeometry
{
    // Walks the boundary walls as one closed loop, starting with the first wall.
    public static bool TryGetLoop(GeometryModel model, Room room, out List<RoomEdge> edges, out string error)
    {
        edges = [];
        var ids = room.BoundaryWallIds;
        if (ids.Count < 3)
            return Fail("A room needs at least 3 boundary walls.", out error);
        if (ids.Distinct().Count() != ids.Count)
            return Fail("A wall appears more than once in the room boundary.", out error);
        if (ids.Any(id => !model.Walls.ContainsKey(id)))
            return Fail("A boundary wall does not exist.", out error);

        var walls = ids.Select(id => model.Walls[id]).ToList();
        if (walls.Any(w => w.StartNodeId == w.EndNodeId || !model.Nodes.ContainsKey(w.StartNodeId) || !model.Nodes.ContainsKey(w.EndNodeId)))
            return Fail("A boundary wall is degenerate.", out error);

        var adjacency = new Dictionary<Guid, List<Wall>>();
        foreach (var w in walls)
            foreach (var node in new[] { w.StartNodeId, w.EndNodeId })
                (adjacency.TryGetValue(node, out var list) ? list : adjacency[node] = []).Add(w);
        if (adjacency.Values.Any(l => l.Count != 2))
            return Fail("The boundary is not a closed loop.", out error);

        var current = walls[0];
        var from = current.StartNodeId;
        for (var i = 0; i < walls.Count; i++)
        {
            var to = GeometryModel.OtherNode(current, from);
            edges.Add(new(current, from, to));
            from = to;
            current = adjacency[to].First(w => w.Id != current.Id);
            if (i < walls.Count - 1 && current.Id == walls[0].Id)
                return Fail("The boundary consists of several separate loops.", out error);
        }

        if (from != walls[0].StartNodeId)
            return Fail("The boundary is not a closed loop.", out error);
        if (edges.Any(e => model.Nodes[e.From].FloorId != room.FloorId))
            return Fail("Boundary walls are on a different floor than the room.", out error);

        error = "";
        return true;

        static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }
    }

    public static IReadOnlyList<Guid> Reorder(GeometryModel model, Room room) =>
        TryGetLoop(model, room, out var edges, out _) ? edges.Select(e => e.Wall.Id).ToList() : room.BoundaryWallIds;

    public static RoomMetrics? Metrics(GeometryModel model, Room room)
    {
        if (!TryGetLoop(model, room, out var edges, out _))
            return null;

        var centerline = edges.Select(e => model.Pos(e.From)).ToList();
        var signed = SignedArea(centerline);
        var inner = OffsetInward(centerline, edges.Select(e => e.Wall.ThicknessMm).ToList(), signed >= 0);
        return new(Math.Abs(SignedArea(inner)), Perimeter(inner), Math.Abs(signed));
    }

    // The room outline on the inner wall faces, or null when the boundary is not a closed loop.
    public static IReadOnlyList<Vec2>? InnerPolygon(GeometryModel model, Room room)
    {
        if (!TryGetLoop(model, room, out var edges, out _))
            return null;
        var centerline = edges.Select(e => model.Pos(e.From)).ToList();
        return OffsetInward(centerline, edges.Select(e => e.Wall.ThicknessMm).ToList(), SignedArea(centerline) >= 0);
    }

    public static double SignedArea(IReadOnlyList<Vec2> p)
    {
        var sum = 0.0;
        for (var i = 0; i < p.Count; i++)
            sum += Vec2.Cross(p[i], p[(i + 1) % p.Count]);
        return sum / 2;
    }

    static double Perimeter(IReadOnlyList<Vec2> p) =>
        Enumerable.Range(0, p.Count).Sum(i => (p[(i + 1) % p.Count] - p[i]).Length);

    // Offsets every centerline edge inward by half its wall thickness and mitres the corners.
    static List<Vec2> OffsetInward(IReadOnlyList<Vec2> p, IReadOnlyList<double> thickness, bool counterClockwise)
    {
        var n = p.Count;
        var dirs = new Vec2[n];
        var anchors = new Vec2[n];
        for (var i = 0; i < n; i++)
        {
            var d = p[(i + 1) % n] - p[i];
            dirs[i] = d;
            var normal = counterClockwise ? new Vec2(-d.Y, d.X) : new Vec2(d.Y, -d.X);
            anchors[i] = p[i] + normal / d.Length * (thickness[i] / 2);
        }

        var result = new List<Vec2>(n);
        for (var i = 0; i < n; i++)
        {
            var prev = (i + n - 1) % n;
            var cross = Vec2.Cross(dirs[prev], dirs[i]);
            if (Math.Abs(cross) < 1e-9 * dirs[prev].Length * dirs[i].Length)
            {
                result.Add(anchors[i]);
                continue;
            }
            var s = Vec2.Cross(anchors[i] - anchors[prev], dirs[i]) / cross;
            result.Add(anchors[prev] + dirs[prev] * s);
        }
        return result;
    }
}
