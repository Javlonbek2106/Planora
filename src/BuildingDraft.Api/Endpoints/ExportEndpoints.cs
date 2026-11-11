using BuildingDraft.Application;

namespace BuildingDraft.Api.Endpoints;

public static class ExportEndpoints
{
    public static void MapExports(this RouteGroupBuilder api)
    {
        api.MapPost("/geometry-versions/{versionId:guid}/exports", async (Guid versionId, CreateExportRequest r, ExportService s, CancellationToken ct) =>
        {
            var job = await s.CreateAsync(versionId, r, ct);
            return Results.Accepted($"/api/v1/exports/{job.Id}", job);
        });

        api.MapGet("/exports/{id:guid}", (Guid id, ExportService s, CancellationToken ct) => s.GetAsync(id, ct));

        api.MapGet("/exports/{id:guid}/download", async (Guid id, ExportService s, CancellationToken ct) =>
        {
            var file = await s.DownloadAsync(id, ct);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });
    }
}
