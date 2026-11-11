namespace BuildingDraft.Domain.Geometry.Constraints;

public sealed record SolveResult(bool Converged, IReadOnlyList<Guid> ConflictingConstraintIds);

// Deterministic position-based projection: constraints are projected in list order with a fixed
// iteration cap and no randomness. Non-convergence is reported, never hidden.
public static class ConstraintSolver
{
    const int MaxIterations = 2000;
    const double ToleranceMm = 0.01;
    const double ConvergenceMm = 0.0001;

    // Nodes the command just moved or created are anchored: the user's edit wins and the rest adapts.
    public static SolveResult Solve(GeometryModel model, GeometryModel before)
    {
        var active = model.Constraints.Where(c => ConstraintRules.Problem(c, model) is null).ToList();
        if (active.Count == 0)
            return new(true, []);

        var state = SolverState.From(model);
        var anchored = model.Nodes.Values
            .Where(n => !before.Nodes.TryGetValue(n.Id, out var old) || old.X != n.X || old.Y != n.Y)
            .ToList();

        var contradictions = active
            .Where(c => c.Type == ConstraintType.FixedPoint
                        && anchored.Any(n => n.Id == c.Targets[0] && Math.Abs(n.X - c.Value!.Value) + Math.Abs(n.Y - c.Value2!.Value) > ToleranceMm))
            .Select(c => c.Id).ToList();
        if (contradictions.Count > 0)
            return new(false, contradictions);

        foreach (var n in anchored)
            state.Pin(n.Id, state[n.Id]);
        foreach (var c in active)
            ConstraintRules.For(c.Type).Prepare(c, state);

        for (var i = 0; i < MaxIterations && !AllSatisfied(active, state, ConvergenceMm); i++)
            foreach (var c in active)
                ConstraintRules.For(c.Type).Project(c, state);

        var conflicts = active.Where(c => ConstraintRules.For(c.Type).Residual(c, state) >= ToleranceMm)
            .Select(c => c.Id).ToList();
        if (conflicts.Count > 0)
            return new(false, conflicts);

        foreach (var (id, p) in state.Positions)
            model.SetPos(id, p);
        return new(true, []);
    }

    static bool AllSatisfied(List<GeometryConstraint> active, SolverState state, double tolerance) =>
        active.All(c => ConstraintRules.For(c.Type).Residual(c, state) < tolerance);
}
