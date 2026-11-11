using BuildingDraft.Application;

namespace BuildingDraft.Api.Endpoints;

public static class GeometryEndpoints
{
    public static void MapGeometry(this RouteGroupBuilder api)
    {
        var geometry = api.MapGroup("/variants/{id:guid}/geometry");

        geometry.MapGet("/", (Guid id, int? version, GeometryService s, CancellationToken ct) => s.GetAsync(id, version, ct));

        geometry.MapPost("/commands", (Guid id, ExecuteCommandRequest r, GeometryService s, CancellationToken ct) =>
            s.ExecuteAsync(id, r, ct));

        geometry.MapPost("/validate", (Guid id, GeometryService s, CancellationToken ct) => s.ValidateAsync(id, ct));

        geometry.MapGet("/versions", (Guid id, GeometryService s, CancellationToken ct) => s.ListVersionsAsync(id, ct));

        geometry.MapPost("/versions/{versionId:guid}/restore", (Guid id, Guid versionId, RestoreVersionRequest r, GeometryService s, CancellationToken ct) =>
            s.RestoreAsync(id, versionId, r, ct));
    }
}
