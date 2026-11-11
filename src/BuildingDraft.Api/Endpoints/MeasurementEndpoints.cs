using BuildingDraft.Application;

namespace BuildingDraft.Api.Endpoints;

public static class MeasurementEndpoints
{
    public static void MapMeasurements(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/{projectId:guid}/measurements",
            (Guid projectId, Guid? variantId, Guid? targetObjectId, MeasurementService s, CancellationToken ct) =>
                s.ListAsync(projectId, variantId, targetObjectId, ct));

        api.MapPost("/projects/{projectId:guid}/measurements", async (Guid projectId, CreateMeasurementRequest r, MeasurementService s, CancellationToken ct) =>
        {
            var measurement = await s.CreateAsync(projectId, r, ct);
            return Results.Created($"/api/v1/measurements/{measurement.Id}", measurement);
        });

        api.MapPost("/measurements/{id:guid}/apply", (Guid id, ApplyMeasurementRequest r, MeasurementService s, CancellationToken ct) =>
            s.ApplyAsync(id, r, ct));

        api.MapPut("/measurements/{id:guid}/status", (Guid id, ChangeMeasurementStatusRequest r, MeasurementService s, CancellationToken ct) =>
            s.ChangeStatusAsync(id, r, ct));
    }
}
