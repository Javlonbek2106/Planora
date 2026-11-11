using BuildingDraft.Domain.Geometry.Commands;

namespace BuildingDraft.Domain.Geometry.Pipeline;

public sealed record ConstraintConflict(Guid ConstraintId, ConstraintType Type, string Message);

public sealed record GeometryFailure(
    string Code,
    string Message,
    IReadOnlyList<ValidationIssue>? Errors = null,
    IReadOnlyList<ConstraintConflict>? Conflicts = null);

public sealed record CommandOutcome(GeometryModel? Model, IReadOnlyList<ValidationIssue> Warnings, GeometryFailure? Failure)
{
    public bool Success => Failure is null;
}

public sealed class StageContext(GeometryModel before, GeometryModel candidate, List<ValidationIssue> warnings)
{
    public GeometryModel Before => before;
    public GeometryModel Candidate => candidate;
    public List<ValidationIssue> Warnings => warnings;
}

// One step of the geometry processing pipeline. Returning a failure stops the pipeline.
public interface IGeometryStage
{
    GeometryFailure? Run(StageContext context);
}

// Applies a command to a candidate copy, then runs the stages in order.
// The current model is never touched; only a fully valid candidate is returned.
public sealed class GeometryProcessor(IReadOnlyList<IGeometryStage> stages)
{
    public static GeometryProcessor Default { get; } =
        new([new SolveConstraintsStage(), new ValidateStage(), new LoadBearingWarningStage()]);

    public CommandOutcome Execute(GeometryModel current, IGeometryCommand command)
    {
        var candidate = current.Clone();
        var warnings = new List<ValidationIssue>();

        try
        {
            command.Apply(candidate, warnings);
        }
        catch (DomainException ex)
        {
            return new(null, warnings, new(ex.Code, ex.Message));
        }

        var context = new StageContext(current, candidate, warnings);
        foreach (var stage in stages)
            if (stage.Run(context) is { } failure)
                return new(null, warnings, failure);

        return new(candidate, warnings, null);
    }
}
