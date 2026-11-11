using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BuildingDraft.Domain.Geometry;

// One geometry version's content. Elements are immutable records, so Clone() is a safe candidate copy.
public sealed class GeometryModel
{
    public Dictionary<Guid, Node> Nodes { get; init; } = [];
    public Dictionary<Guid, Wall> Walls { get; init; } = [];
    public Dictionary<Guid, Room> Rooms { get; init; } = [];
    public Dictionary<Guid, Opening> Openings { get; init; } = [];
    public List<GeometryConstraint> Constraints { get; init; } = [];

    public GeometryModel Clone() => new()
    {
        Nodes = new Dictionary<Guid, Node>(Nodes),
        Walls = new Dictionary<Guid, Wall>(Walls),
        Rooms = new Dictionary<Guid, Room>(Rooms),
        Openings = new Dictionary<Guid, Opening>(Openings),
        Constraints = [..Constraints]
    };

    public Wall GetWall(Guid id) =>
        Walls.TryGetValue(id, out var wall) ? wall : throw new DomainException("WALL_NOT_FOUND", $"Wall {id} not found.");

    public Room GetRoom(Guid id) =>
        Rooms.TryGetValue(id, out var room) ? room : throw new DomainException("ROOM_NOT_FOUND", $"Room {id} not found.");

    public Vec2 Pos(Guid nodeId)
    {
        var node = Nodes[nodeId];
        return new(node.X, node.Y);
    }

    public void SetPos(Guid nodeId, Vec2 p) =>
        Nodes[nodeId] = Nodes[nodeId] with { X = Math.Round(p.X, 3), Y = Math.Round(p.Y, 3) };

    public double Length(Wall wall) => (Pos(wall.EndNodeId) - Pos(wall.StartNodeId)).Length;

    public IEnumerable<Wall> WallsAt(Guid nodeId) =>
        Walls.Values.Where(w => w.StartNodeId == nodeId || w.EndNodeId == nodeId);

    public Guid? SharedNode(Guid wallA, Guid wallB)
    {
        var a = Walls[wallA];
        var b = Walls[wallB];
        var shared = new[] { a.StartNodeId, a.EndNodeId }.Intersect([b.StartNodeId, b.EndNodeId]).ToList();
        return shared.Count == 1 ? shared[0] : null;
    }

    public static Guid OtherNode(Wall wall, Guid nodeId) =>
        wall.StartNodeId == nodeId ? wall.EndNodeId : wall.StartNodeId;

    public string ComputeHash()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        foreach (var n in Nodes.Values.OrderBy(x => x.Id))
            sb.Append(ci, $"N|{n.Id}|{n.FloorId}|{n.X}|{n.Y};");
        foreach (var w in Walls.Values.OrderBy(x => x.Id))
            sb.Append(ci, $"W|{w.Id}|{w.StartNodeId}|{w.EndNodeId}|{w.ThicknessMm}|{w.HeightMm}|{w.Classification};");
        foreach (var r in Rooms.Values.OrderBy(x => x.Id))
            sb.Append(ci, $"R|{r.Id}|{r.FloorId}|{r.Name}|{r.RoomType}|{r.AreaType}|{string.Join(',', r.BoundaryWallIds)};");
        foreach (var o in Openings.Values.OrderBy(x => x.Id))
            sb.Append(ci, $"O|{o.Id}|{o.WallId}|{o.Kind}|{o.Subtype}|{o.OffsetMm}|{o.WidthMm}|{o.HeightMm}|{o.SillMm};");
        foreach (var c in Constraints.OrderBy(x => x.Id))
            sb.Append(ci, $"C|{c.Id}|{c.Type}|{string.Join(',', c.Targets)}|{c.Value}|{c.Value2};");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
