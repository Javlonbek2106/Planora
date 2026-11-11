using BuildingDraft.Application;

namespace BuildingDraft.Api.Endpoints;

public static class VariantEndpoints
{
    public static void MapVariants(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/{projectId:guid}/variants", (Guid projectId, VariantService s, CancellationToken ct) =>
            s.ListAsync(projectId, ct));

        api.MapPost("/projects/{projectId:guid}/variants", async (Guid projectId, CreateVariantRequest r, VariantService s, CancellationToken ct) =>
        {
            var variant = await s.CreateAsync(projectId, r, ct);
            return Results.Created($"/api/v1/variants/{variant.Id}", variant);
        });

        var variants = api.MapGroup("/variants/{id:guid}");

        variants.MapPost("/duplicate", async (Guid id, NameRequest r, VariantService s, CancellationToken ct) =>
        {
            var variant = await s.DuplicateAsync(id, r, ct);
            return Results.Created($"/api/v1/variants/{variant.Id}", variant);
        });

        variants.MapPut("/", (Guid id, NameRequest r, VariantService s, CancellationToken ct) => s.RenameAsync(id, r, ct));

        variants.MapPost("/approve", (Guid id, VariantService s, CancellationToken ct) => s.ApproveAsync(id, ct));

        variants.MapDelete("/", async (Guid id, VariantService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
    }
}
