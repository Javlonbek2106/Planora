using BuildingDraft.Domain.Geometry.Constraints;

namespace BuildingDraft.Domain.Geometry.Pipeline;

public sealed class SolveConstraintsStage : IGeometryStage
{
    public GeometryFailure? Run(StageContext context)
    {
        var result = ConstraintSolver.Solve(context.Candidate, context.Before);
        if (result.Converged)
            return null;

        var conflicts = result.ConflictingConstraintIds
            .Select(id => context.Candidate.Constraints.First(c => c.Id == id))
            .Select(c => new ConstraintConflict(c.Id, c.Type, $"The {c.Type} constraint conflicts with the connected geometry."))
            .ToList();
        return new("GEOMETRY_CONSTRAINT_CONFLICT", "Geometry constraints cannot be satisfied.", Conflicts: conflicts);
    }
}

public sealed class ValidateStage : IGeometryStage
{
    public GeometryFailure? Run(StageContext context)
    {
        var result = ValidationEngine.Validate(context.Candidate);
        return result.IsValid
            ? null
            : new("GEOMETRY_VALIDATION_FAILED", "The resulting geometry is not valid.", result.Errors);
    }
}

// The app never claims a structural change is approved, it only warns about it.
public sealed class LoadBearingWarningStage : IGeometryStage
{
    public GeometryFailure? Run(StageContext context)
    {
        foreach (var before in context.Before.Walls.Values.Where(w => w.Classification == WallClassification.LoadBearing))
            if (!context.Candidate.Walls.TryGetValue(before.Id, out var after) || HasChanged(context, before, after))
                context.Warnings.Add(new("LOAD_BEARING_WALL_MODIFIED",
                    "A load-bearing wall was changed or removed. This is not structurally verified; consult an engineer.", before.Id));
        return null;
    }

    static bool HasChanged(StageContext c, Wall before, Wall after) =>
        before.Classification != after.Classification
        || before.StartNodeId != after.StartNodeId || before.EndNodeId != after.EndNodeId
        || c.Before.Pos(before.StartNodeId) != c.Candidate.Pos(after.StartNodeId)
        || c.Before.Pos(before.EndNodeId) != c.Candidate.Pos(after.EndNodeId);
}
