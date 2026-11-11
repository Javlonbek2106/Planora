using BuildingDraft.Application;

namespace BuildingDraft.Api.Endpoints;

public static class ScanEndpoints
{
    public static void MapScans(this RouteGroupBuilder api)
    {
        api.MapPost("/projects/{projectId:guid}/scan-sessions", async (Guid projectId, CreateScanSessionRequest r, ScanService s, CancellationToken ct) =>
        {
            var session = await s.CreateAsync(projectId, r, ct);
            return Results.Created($"/api/v1/scan-sessions/{session.Id}", session);
        });

        var sessions = api.MapGroup("/scan-sessions/{id:guid}");
        sessions.MapPost("/process", (Guid id, ProcessScanRequest r, ScanService s, CancellationToken ct) => s.ProcessAsync(id, r, ct));
        sessions.MapGet("/result", (Guid id, ScanService s, CancellationToken ct) => s.GetResultAsync(id, ct));
        sessions.MapPost("/apply", (Guid id, ApplyScanRequest r, ScanService s, CancellationToken ct) => s.ApplyAsync(id, r, ct));
    }
}
