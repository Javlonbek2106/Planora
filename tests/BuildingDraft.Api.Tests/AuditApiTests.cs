using System.Net;
using System.Text.Json;

namespace BuildingDraft.Api.Tests;

public class AuditApiTests : IClassFixture<ApiFactory>
{
    readonly ApiFactory factory;
    readonly ApiClient api;

    public AuditApiTests(ApiFactory factory)
    {
        this.factory = factory;
        factory.UserId = Guid.NewGuid();
        api = new ApiClient(factory.CreateClient());
    }

    async Task<List<JsonElement>> Entries(Guid projectId, string query = "")
    {
        var page = await ApiClient.Body(await api.Get($"/api/v1/projects/{projectId}/audit{query}"));
        return page.GetProperty("items").EnumerateArray().ToList();
    }

    static string[] Actions(IEnumerable<JsonElement> entries) => entries.Select(e => e.GetProperty("action").GetString()!).ToArray();

    [Fact]
    public async Task Important_changes_are_recorded_with_the_user_newest_first()
    {
        var (projectId, variantId, floorId) = await api.CreateProjectWithFloor("Flat 12");
        await api.Put($"/api/v1/projects/{projectId}", new { name = "Flat 12 renamed" });
        await api.DrawRoom(variantId, floorId, 4000, 3000);
        await api.Post($"/api/v1/variants/{variantId}/approve");
        await api.Post($"/api/v1/projects/{projectId}/variants", new { name = "Option A" });

        var entries = await Entries(projectId);

        Assert.Equal(["variant.created", "variant.approved", "project.updated", "floor.added", "project.created"], Actions(entries));
        Assert.All(entries, e => Assert.Equal(factory.UserId, e.GetProperty("userId").GetGuid()));
        Assert.Equal("Option A", entries[0].GetProperty("details").GetProperty("name").GetString());
        Assert.Equal("Flat 12 renamed", entries[2].GetProperty("details").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Measurements_scans_and_exports_are_recorded()
    {
        var (projectId, variantId, floorId) = await api.CreateProjectWithFloor();
        var version = await api.DrawRoom(variantId, floorId, 4000, 3000);
        var geometry = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry"));
        var wallId = geometry.GetProperty("walls")[0].GetProperty("id").GetGuid();

        var measurement = (await ApiClient.Body(await api.Post($"/api/v1/projects/{projectId}/measurements",
            new { variantId, targetObjectId = wallId, valueMm = 3900, source = "laser_manual" }))).GetProperty("id").GetGuid();
        await api.Put($"/api/v1/measurements/{measurement}/status", new { status = "verified" });
        await api.Post($"/api/v1/measurements/{measurement}/apply", new { clientOperationId = Guid.NewGuid(), expectedVersion = version });
        var versions = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry/versions"));
        await api.Post($"/api/v1/geometry-versions/{versions[versions.GetArrayLength() - 1].GetProperty("id").GetGuid()}/exports", new { format = "svg" });

        var actions = Actions(await Entries(projectId));

        Assert.Equal(["export.requested", "measurement.applied", "measurement.status_changed", "measurement.recorded"], actions.Take(4));
    }

    [Fact]
    public async Task A_rejected_change_leaves_no_audit_entry()
    {
        var (projectId, variantId, floorId) = await api.CreateProjectWithFloor();
        var version = await api.DrawRoom(variantId, floorId, 4000, 3000);
        var geometry = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry"));
        var wallId = geometry.GetProperty("walls")[0].GetProperty("id").GetGuid();
        await api.Command(variantId, version, "attach_opening",
            new { openingId = Guid.NewGuid(), wallId, kind = "door", offsetMm = 500, widthMm = 900, heightMm = 2100 });
        var measurement = (await ApiClient.Body(await api.Post($"/api/v1/projects/{projectId}/measurements",
            new { variantId, targetObjectId = wallId, valueMm = 300, source = "manual" }))).GetProperty("id").GetGuid();
        var before = (await Entries(projectId)).Count;

        var apply = await api.Post($"/api/v1/measurements/{measurement}/apply", new { clientOperationId = Guid.NewGuid(), expectedVersion = version + 1 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, apply.StatusCode);
        Assert.Equal(before, (await Entries(projectId)).Count);
        Assert.DoesNotContain("measurement.applied", Actions(await Entries(projectId)));
    }

    [Fact]
    public async Task Restore_and_conflict_resolution_are_recorded()
    {
        var (projectId, variantId, floorId) = await api.CreateProjectWithFloor();
        var drawn = await api.DrawRoom(variantId, floorId, 4000, 3000);
        var versions = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry/versions"));
        await api.Post($"/api/v1/variants/{variantId}/geometry/versions/{versions[0].GetProperty("id").GetGuid()}/restore",
            new { clientOperationId = Guid.NewGuid(), expectedVersion = drawn });
        await api.Post("/api/v1/sync/resolve-conflict", new { variantId, resolution = "keep_server" });

        var actions = Actions(await Entries(projectId));

        Assert.Equal(["sync.conflict_resolved", "geometry.restored"], actions.Take(2));
    }

    [Fact]
    public async Task The_audit_log_is_paged_and_private_to_the_owner()
    {
        var (projectId, variantId, _) = await api.CreateProjectWithFloor();
        await api.Post($"/api/v1/variants/{variantId}/approve");

        var page = await ApiClient.Body(await api.Get($"/api/v1/projects/{projectId}/audit?page=1&pageSize=2"));
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.Equal(3, page.GetProperty("total").GetInt32());

        factory.UserId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/api/v1/projects/{projectId}/audit")).StatusCode);
    }
}
