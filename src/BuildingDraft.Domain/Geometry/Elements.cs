namespace BuildingDraft.Domain.Geometry;

public enum WallClassification { Existing, Partition, Planned, Demolition, LoadBearing, Virtual }

public enum RoomAreaType { Enclosed, OpenArea }

public enum OpeningKind { Door, Window }

public enum DimensionReference { StartFixed, EndFixed, Center }

public enum MeasurementReference { Inner, Centerline }

public enum ConstraintType
{
    FixedLength,
    Horizontal,
    Vertical,
    Parallel,
    Perpendicular,
    FixedAngle,
    Coincident,
    FixedPoint,
    Distance,
    RoomClosed
}

public sealed record Node(Guid Id, Guid FloorId, double X, double Y);

public sealed record Wall(
    Guid Id,
    Guid StartNodeId,
    Guid EndNodeId,
    double ThicknessMm,
    double HeightMm,
    WallClassification Classification);

public sealed record Room(
    Guid Id,
    Guid FloorId,
    string Name,
    string RoomType,
    RoomAreaType AreaType,
    IReadOnlyList<Guid> BoundaryWallIds);

public sealed record Opening(
    Guid Id,
    Guid WallId,
    OpeningKind Kind,
    string Subtype,
    double OffsetMm,
    double WidthMm,
    double HeightMm,
    double SillMm);

// Targets are wall ids, node ids or a room id depending on Type (see ConstraintRule).
// Value: length/distance in mm, angle in degrees, or X of a fixed point. Value2: Y of a fixed point.
public sealed record GeometryConstraint(
    Guid Id,
    ConstraintType Type,
    IReadOnlyList<Guid> Targets,
    double? Value = null,
    double? Value2 = null);
