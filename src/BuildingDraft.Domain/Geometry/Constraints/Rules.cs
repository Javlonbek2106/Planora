namespace BuildingDraft.Domain.Geometry.Constraints;

public sealed class FixedLengthRule : ConstraintRule
{
    public override ConstraintType Type => ConstraintType.FixedLength;
    public override ConstraintTarget TargetKind => ConstraintTarget.Wall;
    public override bool NeedsValue => true;

    public override double Residual(GeometryConstraint c, SolverState s) =>
        Math.Abs(s.Direction(c.Targets[0]).Length - c.Value!.Value);

    public override void Project(GeometryConstraint c, SolverState s)
    {
        var (start, end) = s.Ends(c.Targets[0]);
        s.SetDistance(start, end, c.Value!.Value);
    }
}

public sealed class DistanceRule : ConstraintRule
{
    public override ConstraintType Type => ConstraintType.Distance;
    public override ConstraintTarget TargetKind => ConstraintTarget.Node;
    public override int TargetCount => 2;
    public override bool NeedsValue => true;

    public override double Residual(GeometryConstraint c, SolverState s) =>
        Math.Abs((s[c.Targets[1]] - s[c.Targets[0]]).Length - c.Value!.Value);

    public override void Project(GeometryConstraint c, SolverState s) =>
        s.SetDistance(c.Targets[0], c.Targets[1], c.Value!.Value);
}

public sealed class HorizontalRule : ConstraintRule
{
    public override ConstraintType Type => ConstraintType.Horizontal;
    public override ConstraintTarget TargetKind => ConstraintTarget.Wall;

    public override double Residual(GeometryConstraint c, SolverState s) => Math.Abs(s.Direction(c.Targets[0]).Y);

    public override void Project(GeometryConstraint c, SolverState s)
    {
        var (start, end) = s.Ends(c.Targets[0]);
        s.AlignY(start, end);
    }
}

public sealed class VerticalRule : ConstraintRule
{
    public override ConstraintType Type => ConstraintType.Vertical;
    public override ConstraintTarget TargetKind => ConstraintTarget.Wall;

    public override double Residual(GeometryConstraint c, SolverState s) => Math.Abs(s.Direction(c.Targets[0]).X);

    public override void Project(GeometryConstraint c, SolverState s)
    {
        var (start, end) = s.Ends(c.Targets[0]);
        s.AlignX(start, end);
    }
}

public sealed class CoincidentRule : ConstraintRule
{
    public override ConstraintType Type => ConstraintType.Coincident;
    public override ConstraintTarget TargetKind => ConstraintTarget.Node;
    public override int TargetCount => 2;

    public override double Residual(GeometryConstraint c, SolverState s) => (s[c.Targets[1]] - s[c.Targets[0]]).Length;

    public override void Project(GeometryConstraint c, SolverState s)
    {
        s.AlignX(c.Targets[0], c.Targets[1]);
        s.AlignY(c.Targets[0], c.Targets[1]);
    }
}

public sealed class FixedPointRule : ConstraintRule
{
    public override ConstraintType Type => ConstraintType.FixedPoint;
    public override ConstraintTarget TargetKind => ConstraintTarget.Node;
    public override bool NeedsValue => true;

    public override string? Problem(GeometryConstraint c, GeometryModel model) =>
        base.Problem(c, model) ?? (c.Value2 is null ? "FixedPoint requires both value (X) and value2 (Y)." : null);

    public override void Prepare(GeometryConstraint c, SolverState s) => s.Pin(c.Targets[0], At(c));

    public override double Residual(GeometryConstraint c, SolverState s) => (s[c.Targets[0]] - At(c)).Length;

    public override void Project(GeometryConstraint c, SolverState s) => s[c.Targets[0]] = At(c);

    static Vec2 At(GeometryConstraint c) => new(c.Value!.Value, c.Value2!.Value);
}

// Shared by Parallel and Perpendicular: the angle between two wall lines must equal a target.
public abstract class WallAngleRule : ConstraintRule
{
    protected abstract double TargetAngle { get; }

    public override ConstraintTarget TargetKind => ConstraintTarget.Wall;
    public override int TargetCount => 2;

    public override double Residual(GeometryConstraint c, SolverState s) =>
        Math.Abs(Delta(c, s)) * Math.Min(s.Direction(c.Targets[0]).Length, s.Direction(c.Targets[1]).Length);

    public override void Project(GeometryConstraint c, SolverState s)
    {
        var delta = Delta(c, s);
        s.RotateWallAboutMidpoint(c.Targets[0], delta / 2);
        s.RotateWallAboutMidpoint(c.Targets[1], -delta / 2);
    }

    double Delta(GeometryConstraint c, SolverState s)
    {
        var a = s.Direction(c.Targets[0]);
        var b = s.Direction(c.Targets[1]);
        return SolverState.Wrap(Math.Atan2(b.Y, b.X) - Math.Atan2(a.Y, a.X) - TargetAngle, Math.PI);
    }
}

public sealed class ParallelRule : WallAngleRule
{
    public override ConstraintType Type => ConstraintType.Parallel;
    protected override double TargetAngle => 0;
}

public sealed class PerpendicularRule : WallAngleRule
{
    public override ConstraintType Type => ConstraintType.Perpendicular;
    protected override double TargetAngle => Math.PI / 2;
}

public sealed class FixedAngleRule : ConstraintRule
{
    public override ConstraintType Type => ConstraintType.FixedAngle;
    public override ConstraintTarget TargetKind => ConstraintTarget.Wall;
    public override int TargetCount => 2;
    public override bool NeedsValue => true;

    public override string? Problem(GeometryConstraint c, GeometryModel model) =>
        base.Problem(c, model)
        ?? (model.SharedNode(c.Targets[0], c.Targets[1]) is null ? "FixedAngle requires two walls that share a node." : null);

    public override double Residual(GeometryConstraint c, SolverState s)
    {
        var (u, v, _, _, _) = Vectors(c, s);
        return Math.Abs(Error(c, u, v)) * Math.Min(u.Length, v.Length);
    }

    public override void Project(GeometryConstraint c, SolverState s)
    {
        var (u, v, a, b, shared) = Vectors(c, s);
        var error = Error(c, u, v);
        s.Blend(a, s[shared] + u.Rotate(error / 2));
        s.Blend(b, s[shared] + v.Rotate(-error / 2));
    }

    static (Vec2 U, Vec2 V, Guid A, Guid B, Guid Shared) Vectors(GeometryConstraint c, SolverState s)
    {
        var shared = s.Model.SharedNode(c.Targets[0], c.Targets[1])!.Value;
        var a = GeometryModel.OtherNode(s.Model.Walls[c.Targets[0]], shared);
        var b = GeometryModel.OtherNode(s.Model.Walls[c.Targets[1]], shared);
        return (s[a] - s[shared], s[b] - s[shared], a, b, shared);
    }

    // The sign of the current angle is kept, so the wall pair is never flipped to the other side.
    static double Error(GeometryConstraint c, Vec2 u, Vec2 v)
    {
        var current = Math.Atan2(Vec2.Cross(u, v), Vec2.Dot(u, v));
        var target = (current < 0 ? -1 : 1) * c.Value!.Value * Math.PI / 180;
        return SolverState.Wrap(current - target, 2 * Math.PI);
    }
}

// Closure is a topological rule checked by the validator; it has nothing to project.
public sealed class RoomClosedRule : ConstraintRule
{
    public override ConstraintType Type => ConstraintType.RoomClosed;
    public override ConstraintTarget TargetKind => ConstraintTarget.Room;

    public override double Residual(GeometryConstraint c, SolverState s) => 0;

    public override void Project(GeometryConstraint c, SolverState s)
    {
    }
}
