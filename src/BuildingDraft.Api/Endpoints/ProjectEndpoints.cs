using BuildingDraft.Application;

namespace BuildingDraft.Api.Endpoints;

public static class ProjectEndpoints
{
    public static void MapProjects(this RouteGroupBuilder api)
    {
        var projects = api.MapGroup("/projects");

        projects.MapGet("/", (int? page, int? pageSize, ProjectService s, CancellationToken ct) =>
            s.ListAsync(page ?? 1, pageSize ?? 20, ct));

        projects.MapPost("/", async (CreateProjectRequest r, ProjectService s, CancellationToken ct) =>
        {
            var project = await s.CreateAsync(r, ct);
            return Results.Created($"/api/v1/projects/{project.Id}", project);
        });

        projects.MapGet("/{id:guid}", (Guid id, ProjectService s, CancellationToken ct) => s.GetAsync(id, ct));

        projects.MapPut("/{id:guid}", (Guid id, UpdateProjectRequest r, ProjectService s, CancellationToken ct) =>
            s.UpdateAsync(id, r, ct));

        projects.MapDelete("/{id:guid}", async (Guid id, ProjectService s, CancellationToken ct) =>
        {
            await s.ArchiveAsync(id, ct);
            return Results.NoContent();
        });

        projects.MapGet("/{id:guid}/floors", (Guid id, ProjectService s, CancellationToken ct) => s.ListFloorsAsync(id, ct));

        projects.MapPost("/{id:guid}/floors", async (Guid id, FloorRequest r, ProjectService s, CancellationToken ct) =>
        {
            var floor = await s.AddFloorAsync(id, r, ct);
            return Results.Created($"/api/v1/floors/{floor.Id}", floor);
        });

        api.MapPut("/floors/{id:guid}", (Guid id, FloorRequest r, ProjectService s, CancellationToken ct) =>
            s.UpdateFloorAsync(id, r, ct));
    }
}
