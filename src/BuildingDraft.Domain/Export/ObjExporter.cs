using BuildingDraft.Domain.Geometry;
using System.Globalization;
using System.Text;

namespace BuildingDraft.Domain.Export;

// Rough massing mesh: walls (split around door/window openings), floors and ceilings. Units: metres, Y up.
public sealed class ObjExporter : IGeometryExporter
{
    public ExportFormat Format => ExportFormat.Obj;
    public string ContentType => "model/obj";
    public string Extension => "obj";

    public byte[] Export(ExportContext context)
    {
        var model = context.Model;
        if (model.Walls.Count == 0)
            throw new DomainException("EXPORT_EMPTY", "There is nothing to export.");

        var mesh = new Mesh();
        var floors = context.Floors.ToDictionary(f => f.Id);

        mesh.Group("walls");
        foreach (var wall in model.Walls.Values.OrderBy(w => w.Id))
        {
            var floor = floors[model.Nodes[wall.StartNodeId].FloorId];
            AddWall(mesh, model, wall, floor.ElevationMm);
        }

        foreach (var (name, atTop) in new[] { ("floors", false), ("ceilings", true) })
        {
            mesh.Group(name);
            foreach (var room in model.Rooms.Values.OrderBy(r => r.Id))
                if (RoomGeometry.InnerPolygon(model, room) is { } outline)
                {
                    var floor = floors[room.FloorId];
                    mesh.Polygon(outline, floor.ElevationMm + (atTop ? floor.HeightMm : 0), facingUp: !atTop);
                }
        }

        return Encoding.UTF8.GetBytes(mesh.ToObj($"Building Draft: {context.ProjectName}, version {context.VersionNumber}"));
    }

    static void AddWall(Mesh mesh, GeometryModel model, Wall wall, double baseMm)
    {
        var start = model.Pos(wall.StartNodeId);
        var length = model.Length(wall);
        var dir = (model.Pos(wall.EndNodeId) - start) / length;
        var (z0, z1) = (baseMm, baseMm + wall.HeightMm);

        void Box(double from, double to, double bottom, double top) =>
            mesh.Box(start + dir * from, dir, to - from, wall.ThicknessMm, bottom, top);

        var half = wall.ThicknessMm / 2;
        var from = model.WallsAt(wall.StartNodeId).Count() > 1 ? -half : 0;
        var to = length + (model.WallsAt(wall.EndNodeId).Count() > 1 ? half : 0);

        var cursor = 0.0;
        foreach (var o in model.Openings.Values.Where(o => o.WallId == wall.Id).OrderBy(o => o.OffsetMm))
        {
            if (o.OffsetMm > cursor)
                Box(cursor == 0 ? from : cursor, o.OffsetMm, z0, z1);
            if (o.SillMm > 0)
                Box(o.OffsetMm, o.OffsetMm + o.WidthMm, z0, z0 + o.SillMm);
            if (o.SillMm + o.HeightMm < wall.HeightMm)
                Box(o.OffsetMm, o.OffsetMm + o.WidthMm, z0 + o.SillMm + o.HeightMm, z1);
            cursor = o.OffsetMm + o.WidthMm;
        }
        if (cursor < length)
            Box(cursor == 0 ? from : cursor, to, z0, z1);
        else if (to > length)
            Box(length, to, z0, z1);
    }

    sealed class Mesh
    {
        readonly StringBuilder vertices = new();
        readonly StringBuilder faces = new();
        int count;

        public void Group(string name) => faces.Append($"g {name}\n");

        // Plan (x, y, elevation) in mm -> OBJ (x, elevation, -y) in metres, a proper rotation that keeps winding.
        int Vertex(Vec2 p, double z)
        {
            vertices.Append(string.Create(CultureInfo.InvariantCulture, $"v {p.X / 1000:0.####} {z / 1000:0.####} {-p.Y / 1000:0.####}\n"));
            return ++count;
        }

        void Face(params int[] indices) => faces.Append("f ").Append(string.Join(' ', indices)).Append('\n');

        public void Box(Vec2 origin, Vec2 dir, double length, double thickness, double bottom, double top)
        {
            var normal = new Vec2(-dir.Y, dir.X) * (thickness / 2);
            Vec2[] corners = [origin + normal, origin + dir * length + normal, origin + dir * length - normal, origin - normal];
            var b = corners.Select(c => Vertex(c, bottom)).ToArray();
            var t = corners.Select(c => Vertex(c, top)).ToArray();
            Face(b[0], b[1], b[2], b[3]);
            Face(t[3], t[2], t[1], t[0]);
            Face(b[0], t[0], t[1], b[1]);
            Face(b[3], b[2], t[2], t[3]);
            Face(b[1], t[1], t[2], b[2]);
            Face(b[0], b[3], t[3], t[0]);
        }

        public void Polygon(IReadOnlyList<Vec2> outline, double z, bool facingUp)
        {
            var ccw = RoomGeometry.SignedArea(outline) >= 0 ? outline.ToList() : outline.Reverse().ToList();
            var ids = ccw.Select(p => Vertex(p, z)).ToArray();
            foreach (var (a, b, c) in Triangulate(ccw))
                if (facingUp)
                    Face(ids[a], ids[b], ids[c]);
                else
                    Face(ids[c], ids[b], ids[a]);
        }

        public string ToObj(string comment) => $"# {comment}\n# units: metres, Y up\n{vertices}{faces}";

        // Ear clipping for a counter-clockwise polygon.
        static List<(int, int, int)> Triangulate(List<Vec2> p)
        {
            var remaining = Enumerable.Range(0, p.Count).ToList();
            var triangles = new List<(int, int, int)>();
            var guard = p.Count * p.Count;
            while (remaining.Count > 3 && guard-- > 0)
                for (var i = 0; i < remaining.Count; i++)
                {
                    var (ia, ib, ic) = (remaining[(i + remaining.Count - 1) % remaining.Count], remaining[i], remaining[(i + 1) % remaining.Count]);
                    if (Vec2.Cross(p[ib] - p[ia], p[ic] - p[ib]) <= 1e-9 || remaining.Any(k => k != ia && k != ib && k != ic && Inside(p[k], p[ia], p[ib], p[ic])))
                        continue;
                    triangles.Add((ia, ib, ic));
                    remaining.RemoveAt(i);
                    break;
                }
            if (remaining.Count == 3)
                triangles.Add((remaining[0], remaining[1], remaining[2]));
            return triangles;
        }

        static bool Inside(Vec2 q, Vec2 a, Vec2 b, Vec2 c) =>
            Vec2.Cross(b - a, q - a) >= 0 && Vec2.Cross(c - b, q - b) >= 0 && Vec2.Cross(a - c, q - c) >= 0;
    }
}
