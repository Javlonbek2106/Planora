using BuildingDraft.Application;

namespace BuildingDraft.Api.Endpoints;

public static class SyncEndpoints
{
    public static void MapSync(this RouteGroupBuilder api)
    {
        var sync = api.MapGroup("/sync");

        sync.MapPost("/push", (SyncPushRequest r, SyncService s, CancellationToken ct) => s.PushAsync(r, ct));
        sync.MapPost("/pull", (SyncPullRequest r, SyncService s, CancellationToken ct) => s.PullAsync(r, ct));
        sync.MapPost("/resolve-conflict", (ResolveConflictRequest r, SyncService s, CancellationToken ct) => s.ResolveAsync(r, ct));
    }
}
