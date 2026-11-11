namespace BuildingDraft.Domain.Geometry.Pipeline;

public sealed class GeometryRejectedException(GeometryFailure failure) : DomainException(failure.Code, failure.Message)
{
    public GeometryFailure Failure { get; } = failure;
}
