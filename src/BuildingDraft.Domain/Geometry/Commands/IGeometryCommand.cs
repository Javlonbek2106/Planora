namespace BuildingDraft.Domain.Geometry.Commands;

public interface IGeometryCommand
{
    void Apply(GeometryModel model, List<ValidationIssue> warnings);
}
