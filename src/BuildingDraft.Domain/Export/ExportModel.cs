using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Projects;

namespace BuildingDraft.Domain.Export;

public enum ExportFormat { Pdf, Png, Svg, Dxf, Dwg, Obj, Fbx, Ifc }

public sealed record ExportContext(GeometryModel Model, IReadOnlyList<Floor> Floors, Guid? FloorId, string ProjectName, int VersionNumber)
{
    // Plan formats draw one floor at a time; with a single used floor no choice is needed.
    public Floor ResolvePlanFloor()
    {
        if (FloorId is { } id)
            return Floors.FirstOrDefault(f => f.Id == id) ?? throw new DomainException("FLOOR_NOT_FOUND", "The floor does not belong to this project.");

        var used = Model.Nodes.Values.Select(n => n.FloorId).Distinct().ToList();
        return used.Count == 1
            ? Floors.First(f => f.Id == used[0])
            : throw new DomainException("EXPORT_FLOOR_REQUIRED", "This geometry spans several floors (or none); choose a floor to export.");
    }
}

// Strategy: one implementation per supported format, discovered by the application layer.
public interface IGeometryExporter
{
    ExportFormat Format { get; }
    string ContentType { get; }
    string Extension { get; }
    byte[] Export(ExportContext context);
}

// Outline is the drawn strip (ends extended at connected corners); Start/End stay on the centerline. Label sits outside the wall.
public sealed record PlanWall(Guid Id, Vec2 Start, Vec2 End, double ThicknessMm, IReadOnlyList<Vec2> Outline, Vec2 Label)
{
    public double LengthMm => (End - Start).Length;
    public Vec2 Mid => (Start + End) / 2;
}

public sealed record PlanOpening(OpeningKind Kind, IReadOnlyList<Vec2> Outline);

public sealed record PlanRoom(string Name, RoomAreaType AreaType, IReadOnlyList<Vec2> Outline, double AreaM2, Vec2 Label);

// What every plan format (SVG, PDF, DXF) draws for one floor, so the formats only differ in how they write it.
public sealed class PlanDrawing
{
    PlanDrawing(IReadOnlyList<PlanWall> walls, IReadOnlyList<PlanOpening> openings, IReadOnlyList<PlanRoom> rooms)
    {
        Walls = walls;
        Openings = openings;
        Rooms = rooms;
        var points = walls.SelectMany(w => w.Outline).ToList();
        Min = new(points.Min(p => p.X), points.Min(p => p.Y));
        Max = new(points.Max(p => p.X), points.Max(p => p.Y));
    }

    public IReadOnlyList<PlanWall> Walls { get; }
    public IReadOnlyList<PlanOpening> Openings { get; }
    public IReadOnlyList<PlanRoom> Rooms { get; }
    public Vec2 Min { get; }
    public Vec2 Max { get; }

    public static PlanDrawing Build(GeometryModel model, Guid floorId)
    {
        var floorWallList = model.Walls.Values.OrderBy(w => w.Id).Where(w => model.Nodes[w.StartNodeId].FloorId == floorId).ToList();
        if (floorWallList.Count == 0)
            throw new DomainException("EXPORT_EMPTY", "There is nothing to export on this floor.");

        var center = floorWallList.Aggregate(new Vec2(0, 0), (sum, w) => sum + (model.Pos(w.StartNodeId) + model.Pos(w.EndNodeId)) / 2) / floorWallList.Count;
        var walls = floorWallList.Select(w => ToPlanWall(model, w, center)).ToList();

        var floorWalls = walls.ToDictionary(w => w.Id);
        var openings = model.Openings.Values.OrderBy(o => o.Id)
            .Where(o => floorWalls.ContainsKey(o.WallId))
            .Select(o => new PlanOpening(o.Kind, Rectangle(floorWalls[o.WallId].Start, floorWalls[o.WallId].End, o.OffsetMm, o.WidthMm, floorWalls[o.WallId].ThicknessMm)))
            .ToList();

        var rooms = new List<PlanRoom>();
        foreach (var room in model.Rooms.Values.Where(r => r.FloorId == floorId).OrderBy(r => r.Id))
            if (RoomGeometry.InnerPolygon(model, room) is { } outline)
                rooms.Add(new(room.Name, room.AreaType, outline, Math.Abs(RoomGeometry.SignedArea(outline)) / 1e6, Centroid(outline)));

        return new(walls, openings, rooms);
    }

    static PlanWall ToPlanWall(GeometryModel model, Wall wall, Vec2 planCenter)
    {
        var (start, end) = (model.Pos(wall.StartNodeId), model.Pos(wall.EndNodeId));
        var dir = (end - start) / (end - start).Length;
        var half = wall.ThicknessMm / 2;

        // Walls that meet another wall are extended by half a thickness so outer corners are filled.
        var from = model.WallsAt(wall.StartNodeId).Count() > 1 ? -half : 0;
        var to = model.WallsAt(wall.EndNodeId).Count() > 1 ? half : 0;
        var outline = Rectangle(start, end, from, (end - start).Length + to - from, wall.ThicknessMm);

        var mid = (start + end) / 2;
        var normal = new Vec2(-dir.Y, dir.X);
        if (Vec2.Dot(normal, mid - planCenter) < 0)
            normal = normal * -1;
        return new(wall.Id, start, end, wall.ThicknessMm, outline, mid + normal * (half + 180));
    }

    // A strip along a wall: the whole wall, or the part from offset to offset + width.
    static Vec2[] Rectangle(Vec2 start, Vec2 end, double offset, double? width, double thickness)
    {
        var length = (end - start).Length;
        var dir = (end - start) / length;
        var normal = new Vec2(-dir.Y, dir.X) * (thickness / 2);
        var from = start + dir * offset;
        var to = start + dir * (offset + (width ?? length));
        return [from + normal, to + normal, to - normal, from - normal];
    }

    static Vec2 Centroid(IReadOnlyList<Vec2> p)
    {
        var area = RoomGeometry.SignedArea(p) * 2;
        if (Math.Abs(area) < 1e-9)
            return p[0];
        double cx = 0, cy = 0;
        for (var i = 0; i < p.Count; i++)
        {
            var (a, b) = (p[i], p[(i + 1) % p.Count]);
            var cross = Vec2.Cross(a, b);
            cx += (a.X + b.X) * cross;
            cy += (a.Y + b.Y) * cross;
        }
        return new(cx / (3 * area), cy / (3 * area));
    }
}
