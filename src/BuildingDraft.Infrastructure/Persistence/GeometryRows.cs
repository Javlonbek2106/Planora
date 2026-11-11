using System.Text.Json;
using BuildingDraft.Domain.Geometry;

namespace BuildingDraft.Infrastructure.Persistence;

// Relational snapshot of one geometry version. Ids are stable across versions, so every key is
// (GeometryVersionId, Id) and every in-version reference is a composite foreign key.
public sealed class NodeRow
{
    public Guid GeometryVersionId { get; set; }
    public Guid Id { get; set; }
    public Guid FloorId { get; set; }
    public double XMm { get; set; }
    public double YMm { get; set; }
}

public sealed class WallRow
{
    public Guid GeometryVersionId { get; set; }
    public Guid Id { get; set; }
    public Guid StartNodeId { get; set; }
    public Guid EndNodeId { get; set; }
    public double ThicknessMm { get; set; }
    public double HeightMm { get; set; }
    public WallClassification Classification { get; set; }
}

public sealed class RoomRow
{
    public Guid GeometryVersionId { get; set; }
    public Guid Id { get; set; }
    public Guid FloorId { get; set; }
    public string Name { get; set; } = "";
    public string RoomType { get; set; } = "";
    public RoomAreaType AreaType { get; set; }
}

public sealed class RoomBoundaryRow
{
    public Guid GeometryVersionId { get; set; }
    public Guid RoomId { get; set; }
    public int SequenceNumber { get; set; }
    public Guid WallId { get; set; }
}

public sealed class OpeningRow
{
    public Guid GeometryVersionId { get; set; }
    public Guid Id { get; set; }
    public Guid WallId { get; set; }
    public OpeningKind Kind { get; set; }
    public string Subtype { get; set; } = "";
    public double OffsetMm { get; set; }
    public double WidthMm { get; set; }
    public double HeightMm { get; set; }
    public double SillMm { get; set; }
}

public sealed class ConstraintRow
{
    public Guid GeometryVersionId { get; set; }
    public Guid Id { get; set; }
    public int Position { get; set; }
    public ConstraintType Type { get; set; }
    public string Targets { get; set; } = "[]";
    public double? Value { get; set; }
    public double? Value2 { get; set; }
}

public sealed record GeometryRows(
    IReadOnlyList<NodeRow> Nodes,
    IReadOnlyList<WallRow> Walls,
    IReadOnlyList<RoomRow> Rooms,
    IReadOnlyList<RoomBoundaryRow> Boundaries,
    IReadOnlyList<OpeningRow> Openings,
    IReadOnlyList<ConstraintRow> Constraints);

public static class GeometryRowMapper
{
    public static GeometryRows ToRows(Guid versionId, GeometryModel m) => new(
        [..m.Nodes.Values.Select(n => new NodeRow { GeometryVersionId = versionId, Id = n.Id, FloorId = n.FloorId, XMm = n.X, YMm = n.Y })],
        [..m.Walls.Values.Select(w => new WallRow
        {
            GeometryVersionId = versionId, Id = w.Id, StartNodeId = w.StartNodeId, EndNodeId = w.EndNodeId,
            ThicknessMm = w.ThicknessMm, HeightMm = w.HeightMm, Classification = w.Classification
        })],
        [..m.Rooms.Values.Select(r => new RoomRow
        {
            GeometryVersionId = versionId, Id = r.Id, FloorId = r.FloorId, Name = r.Name, RoomType = r.RoomType, AreaType = r.AreaType
        })],
        [..m.Rooms.Values.SelectMany(r => r.BoundaryWallIds.Select((wallId, i) => new RoomBoundaryRow
        {
            GeometryVersionId = versionId, RoomId = r.Id, SequenceNumber = i, WallId = wallId
        }))],
        [..m.Openings.Values.Select(o => new OpeningRow
        {
            GeometryVersionId = versionId, Id = o.Id, WallId = o.WallId, Kind = o.Kind, Subtype = o.Subtype,
            OffsetMm = o.OffsetMm, WidthMm = o.WidthMm, HeightMm = o.HeightMm, SillMm = o.SillMm
        })],
        [..m.Constraints.Select((c, i) => new ConstraintRow
        {
            GeometryVersionId = versionId, Id = c.Id, Position = i, Type = c.Type,
            Targets = JsonSerializer.Serialize(c.Targets), Value = c.Value, Value2 = c.Value2
        })]);

    public static GeometryModel ToModel(GeometryRows rows)
    {
        var boundaries = rows.Boundaries.GroupBy(b => b.RoomId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)[..g.OrderBy(b => b.SequenceNumber).Select(b => b.WallId)]);

        return new GeometryModel
        {
            Nodes = rows.Nodes.ToDictionary(n => n.Id, n => new Node(n.Id, n.FloorId, n.XMm, n.YMm)),
            Walls = rows.Walls.ToDictionary(w => w.Id,
                w => new Wall(w.Id, w.StartNodeId, w.EndNodeId, w.ThicknessMm, w.HeightMm, w.Classification)),
            Rooms = rows.Rooms.ToDictionary(r => r.Id,
                r => new Room(r.Id, r.FloorId, r.Name, r.RoomType, r.AreaType, boundaries.GetValueOrDefault(r.Id, []))),
            Openings = rows.Openings.ToDictionary(o => o.Id,
                o => new Opening(o.Id, o.WallId, o.Kind, o.Subtype, o.OffsetMm, o.WidthMm, o.HeightMm, o.SillMm)),
            Constraints = [..rows.Constraints.OrderBy(c => c.Position).Select(c =>
                new GeometryConstraint(c.Id, c.Type, JsonSerializer.Deserialize<List<Guid>>(c.Targets)!, c.Value, c.Value2))]
        };
    }
}
