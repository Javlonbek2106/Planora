namespace BuildingDraft.Domain.Geometry.Constraints;

public enum ConstraintTarget { Wall, Node, Room }

// Strategy: one subclass per ConstraintType. The solver and the validator only talk to this contract.
public abstract class ConstraintRule
{
    public abstract ConstraintType Type { get; }
    public abstract ConstraintTarget TargetKind { get; }
    public virtual int TargetCount => 1;
    public virtual bool NeedsValue => false;

    public virtual void Prepare(GeometryConstraint constraint, SolverState state)
    {
    }

    public abstract double Residual(GeometryConstraint constraint, SolverState state);

    public abstract void Project(GeometryConstraint constraint, SolverState state);

    public virtual string? Problem(GeometryConstraint c, GeometryModel model)
    {
        if (c.Targets.Count != TargetCount)
            return $"{Type} requires {TargetCount} target(s).";
        if (NeedsValue && c.Value is null)
            return $"{Type} requires a value.";

        var exists = c.Targets.All(t => TargetKind switch
        {
            ConstraintTarget.Wall => model.Walls.ContainsKey(t),
            ConstraintTarget.Node => model.Nodes.ContainsKey(t),
            _ => model.Rooms.ContainsKey(t)
        });
        return exists ? null : "A constraint target does not exist.";
    }
}

public static class ConstraintRules
{
    static readonly IReadOnlyDictionary<ConstraintType, ConstraintRule> Rules = typeof(ConstraintRule).Assembly
        .GetTypes()
        .Where(t => t is { IsAbstract: false } && t.IsSubclassOf(typeof(ConstraintRule)))
        .Select(t => (ConstraintRule)Activator.CreateInstance(t)!)
        .ToDictionary(r => r.Type);

    public static ConstraintRule For(ConstraintType type) =>
        Rules.TryGetValue(type, out var rule) ? rule : throw new DomainException("UNKNOWN_CONSTRAINT", $"Unknown constraint type {type}.");

    public static string? Problem(GeometryConstraint c, GeometryModel model) =>
        Rules.TryGetValue(c.Type, out var rule) ? rule.Problem(c, model) : $"Unknown constraint type {c.Type}.";
}
