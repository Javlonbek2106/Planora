using BuildingDraft.Domain.Geometry.Constraints;

namespace BuildingDraft.Domain.Geometry;

public sealed record ValidationIssue(string Code, string Message, Guid? ObjectId = null);

public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Errors, IReadOnlyList<ValidationIssue> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

public static class ValidationEngine
{
    const double EpsilonMm = 1e-6;
    const double MinWallLengthMm = 1;

    public static ValidationResult Validate(GeometryModel model)
    {
        var errors = new List<ValidationIssue>();
        var walls = model.Walls.Values.OrderBy(w => w.Id).ToList();

        var valid = new List<Wall>();
        foreach (var w in walls)
        {
            if (!model.Nodes.ContainsKey(w.StartNodeId) || !model.Nodes.ContainsKey(w.EndNodeId))
                errors.Add(new("WALL_NODE_MISSING", "Wall refers to a node that does not exist.", w.Id));
            else if (w.StartNodeId == w.EndNodeId || model.Length(w) < MinWallLengthMm)
                errors.Add(new("WALL_ZERO_LENGTH", "Wall length must be at least 1 mm.", w.Id));
            else if (w.ThicknessMm <= 0 || w.HeightMm <= 0)
                errors.Add(new("WALL_INVALID_SIZE", "Wall thickness and height must be positive.", w.Id));
            else if (model.Nodes[w.StartNodeId].FloorId != model.Nodes[w.EndNodeId].FloorId)
                errors.Add(new("WALL_CROSS_FLOOR", "Wall nodes must be on the same floor.", w.Id));
            else
                valid.Add(w);
        }

        ValidateWallPairs(model, valid, errors);
        ValidateConstraints(model, errors);
        ValidateRooms(model, errors);
        ValidateOpenings(model, errors);
        return new(errors, []);
    }

    static void ValidateWallPairs(GeometryModel model, List<Wall> walls, List<ValidationIssue> errors)
    {
        for (var i = 0; i < walls.Count; i++)
            for (var j = i + 1; j < walls.Count; j++)
            {
                var a = walls[i];
                var b = walls[j];
                var shared = new[] { a.StartNodeId, a.EndNodeId }.Intersect([b.StartNodeId, b.EndNodeId]).ToList();

                if (shared.Count == 2)
                {
                    errors.Add(new("WALL_DUPLICATE", "Two walls connect the same pair of nodes.", b.Id));
                    continue;
                }

                if (shared.Count == 1)
                {
                    var s = model.Pos(shared[0]);
                    var u = model.Pos(GeometryModel.OtherNode(a, shared[0])) - s;
                    var v = model.Pos(GeometryModel.OtherNode(b, shared[0])) - s;
                    if (Math.Abs(Vec2.Cross(u, v)) <= EpsilonMm * u.Length * v.Length && Vec2.Dot(u, v) > 0)
                        errors.Add(new("WALL_OVERLAP", "Two connected walls overlap each other.", b.Id));
                    continue;
                }

                if (model.Nodes[a.StartNodeId].FloorId != model.Nodes[b.StartNodeId].FloorId)
                    continue;
                if (SegmentsTouch(model.Pos(a.StartNodeId), model.Pos(a.EndNodeId), model.Pos(b.StartNodeId), model.Pos(b.EndNodeId)))
                    errors.Add(new("WALL_INTERSECTION",
                        "Walls intersect or touch without a shared node. Split the wall to connect them.", b.Id));
            }
    }

    static bool SegmentsTouch(Vec2 p1, Vec2 p2, Vec2 q1, Vec2 q2)
    {
        var d1 = p2 - p1;
        var d2 = q2 - q1;
        var diff = q1 - p1;
        var denom = Vec2.Cross(d1, d2);
        var len1 = d1.Length;

        if (Math.Abs(denom) < 1e-9 * len1 * d2.Length)
        {
            if (Math.Abs(Vec2.Cross(diff, d1)) > EpsilonMm * len1)
                return false;
            var squared = Vec2.Dot(d1, d1);
            var t0 = Vec2.Dot(diff, d1) / squared;
            var t1 = Vec2.Dot(q2 - p1, d1) / squared;
            var tolerance = EpsilonMm / len1;
            return Math.Max(t0, t1) >= -tolerance && Math.Min(t0, t1) <= 1 + tolerance;
        }

        var t = Vec2.Cross(diff, d2) / denom;
        var u = Vec2.Cross(diff, d1) / denom;
        var et = EpsilonMm / len1;
        var eu = EpsilonMm / d2.Length;
        return t >= -et && t <= 1 + et && u >= -eu && u <= 1 + eu;
    }

    static void ValidateConstraints(GeometryModel model, List<ValidationIssue> errors)
    {
        foreach (var c in model.Constraints)
            if (ConstraintRules.Problem(c, model) is { } problem)
                errors.Add(new("CONSTRAINT_INVALID", problem, c.Id));
    }

    static void ValidateRooms(GeometryModel model, List<ValidationIssue> errors)
    {
        foreach (var room in model.Rooms.Values.OrderBy(r => r.Id))
            if (!RoomGeometry.TryGetLoop(model, room, out _, out var error))
                errors.Add(new("ROOM_NOT_CLOSED", error, room.Id));
    }

    static void ValidateOpenings(GeometryModel model, List<ValidationIssue> errors)
    {
        foreach (var group in model.Openings.Values.OrderBy(o => o.Id).GroupBy(o => o.WallId))
        {
            if (!model.Walls.TryGetValue(group.Key, out var wall) || !model.Nodes.ContainsKey(wall.StartNodeId)
                || !model.Nodes.ContainsKey(wall.EndNodeId))
            {
                errors.AddRange(group.Select(o => new ValidationIssue("OPENING_WALL_MISSING", "Opening is not attached to an existing wall.", o.Id)));
                continue;
            }

            var length = model.Length(wall);
            var fits = new List<Opening>();
            foreach (var o in group)
            {
                if (o.WidthMm <= 0 || o.HeightMm <= 0 || o.SillMm < 0)
                    errors.Add(new("OPENING_INVALID_SIZE", "Opening width and height must be positive and sill non-negative.", o.Id));
                else if (o.OffsetMm < 0 || o.OffsetMm + o.WidthMm > length + EpsilonMm)
                    errors.Add(new("OPENING_OUT_OF_BOUNDS", "Opening does not fit within the wall length.", o.Id));
                else if (o.SillMm + o.HeightMm > wall.HeightMm + EpsilonMm)
                    errors.Add(new("OPENING_OUT_OF_BOUNDS", "Opening does not fit within the wall height.", o.Id));
                else
                    fits.Add(o);
            }

            var sorted = fits.OrderBy(o => o.OffsetMm).ToList();
            for (var i = 1; i < sorted.Count; i++)
                if (sorted[i].OffsetMm < sorted[i - 1].OffsetMm + sorted[i - 1].WidthMm - EpsilonMm)
                    errors.Add(new("OPENING_OVERLAP", "Openings on the same wall overlap.", sorted[i].Id));
        }
    }
}
