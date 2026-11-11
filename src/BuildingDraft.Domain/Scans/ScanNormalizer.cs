using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Geometry.Commands;

namespace BuildingDraft.Domain.Scans;

public sealed record CapturedWall(double StartXMm, double StartYMm, double EndXMm, double EndYMm, double? ThicknessMm = null, double? HeightMm = null);

public sealed record CapturedOpening(int WallIndex, OpeningKind Kind, double OffsetMm, double WidthMm, double HeightMm, double SillMm = 0);

// What a native scan adapter (ARKit/RoomPlan, ARCore) reports, already reduced to plan coordinates in mm.
public sealed record ScanCapture(
    IReadOnlyList<CapturedWall> Walls,
    IReadOnlyList<CapturedOpening>? Openings = null,
    double? Confidence = null,
    string? DeviceModel = null,
    string? ScanQuality = null,
    double? SnapToleranceMm = null);

public sealed record ScanIssue(string Code, string Message);

public sealed record ScanProposal(ImportScan Command, IReadOnlyList<ScanIssue> Issues);

// Scan Import Layer: turns raw captured surfaces into canonical geometry the user can review.
public static class ScanNormalizer
{
    public const double DefaultSnapToleranceMm = 50;
    public const double MinWallLengthMm = 100;
    public const double DefaultThicknessMm = 200;

    public static ScanProposal Normalize(ScanCapture capture, Guid floorId, double defaultHeightMm)
    {
        var tolerance = capture.SnapToleranceMm ?? DefaultSnapToleranceMm;
        if (tolerance < 0)
            throw new DomainException("INVALID_SCAN", "Snap tolerance cannot be negative.");

        var issues = new List<ScanIssue>();
        var nodes = new List<(Guid Id, Vec2 Position, int Count)>();
        var walls = new List<ScanWall>();
        var wallByCaptureIndex = new Dictionary<int, Guid>();

        for (var i = 0; i < capture.Walls.Count; i++)
        {
            var c = capture.Walls[i];
            var (start, end) = (new Vec2(c.StartXMm, c.StartYMm), new Vec2(c.EndXMm, c.EndYMm));
            if ((end - start).Length < MinWallLengthMm)
            {
                issues.Add(new("WALL_TOO_SHORT", $"Wall {i} is shorter than {MinWallLengthMm} mm and was dropped."));
                continue;
            }

            var (startId, endId) = (SnapNode(nodes, start, tolerance), SnapNode(nodes, end, tolerance));
            if (startId == endId)
            {
                issues.Add(new("WALL_COLLAPSED", $"Wall {i} collapsed to a point after snapping and was dropped."));
                continue;
            }
            if (walls.Any(w => (w.StartNodeId == startId && w.EndNodeId == endId) || (w.StartNodeId == endId && w.EndNodeId == startId)))
            {
                issues.Add(new("WALL_DUPLICATE", $"Wall {i} duplicates another wall and was dropped."));
                continue;
            }

            var id = Guid.CreateVersion7();
            wallByCaptureIndex[i] = id;
            walls.Add(new(id, startId, endId, c.ThicknessMm ?? DefaultThicknessMm, c.HeightMm ?? defaultHeightMm));
        }

        var openings = new List<ScanOpening>();
        foreach (var (o, index) in (capture.Openings ?? []).Select((o, index) => (o, index)))
        {
            if (!wallByCaptureIndex.TryGetValue(o.WallIndex, out var wallId))
                issues.Add(new("OPENING_WITHOUT_WALL", $"Opening {index} refers to wall {o.WallIndex}, which is not part of the scan."));
            else
                openings.Add(new(Guid.CreateVersion7(), wallId, o.Kind, o.OffsetMm, o.WidthMm, o.HeightMm, o.SillMm));
        }

        var command = new ImportScan(floorId, [..nodes.Select(n => new ScanNode(n.Id, Math.Round(n.Position.X, 1), Math.Round(n.Position.Y, 1)))], walls, openings);
        return new(command, issues);
    }

    // Endpoints within the tolerance of an existing node join it; the node moves to the average of its endpoints.
    static Guid SnapNode(List<(Guid Id, Vec2 Position, int Count)> nodes, Vec2 point, double tolerance)
    {
        var nearest = nodes.Select((n, i) => (n, i)).Where(x => (x.n.Position - point).Length <= tolerance)
            .OrderBy(x => (x.n.Position - point).Length).Select(x => (int?)x.i).FirstOrDefault();
        if (nearest is { } index)
        {
            var (id, position, count) = nodes[index];
            nodes[index] = (id, (position * count + point) / (count + 1), count + 1);
            return id;
        }

        var node = (Guid.CreateVersion7(), point, 1);
        nodes.Add(node);
        return node.Item1;
    }
}
