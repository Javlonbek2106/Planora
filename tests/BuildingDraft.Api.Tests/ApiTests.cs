using System.Net;
using System.Text.Json;

namespace BuildingDraft.Api.Tests;

public class ApiTests : IClassFixture<ApiFactory>
{
    readonly ApiFactory factory;
    readonly ApiClient api;

    public ApiTests(ApiFactory factory)
    {
        this.factory = factory;
        factory.UserId = Guid.NewGuid();
        api = new ApiClient(factory.CreateClient());
    }

    static async Task<string> ErrorCode(HttpResponseMessage response) =>
        (await ApiClient.Body(response)).GetProperty("errorCode").GetString()!;

    [Fact]
    public async Task New_project_has_an_original_layout_at_version_one()
    {
        var response = await api.Post("/api/v1/projects", new { name = "Flat 12", address = "Tashkent" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var project = await ApiClient.Body(response);
        var variant = project.GetProperty("variants")[0];
        Assert.Equal("original", variant.GetProperty("type").GetString());
        Assert.Equal(1, variant.GetProperty("currentVersion").GetInt32());
        Assert.False(variant.GetProperty("isLocked").GetBoolean());
    }

    [Fact]
    public async Task Blank_project_name_is_rejected()
    {
        var response = await api.Post("/api/v1/projects", new { name = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_NAME", await ErrorCode(response));
    }

    [Fact]
    public async Task Duplicate_floor_number_is_a_conflict()
    {
        var (projectId, _, _) = await api.CreateProjectWithFloor();

        var response = await api.Post($"/api/v1/projects/{projectId}/floors", new { floorNumber = 1 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("FLOOR_NUMBER_EXISTS", await ErrorCode(response));
    }

    [Fact]
    public async Task Drawing_a_room_persists_geometry_and_reports_inner_area()
    {
        var (_, variantId, floorId) = await api.CreateProjectWithFloor();

        var version = await api.DrawRoom(variantId, floorId, 4000, 3000);

        Assert.Equal(6, version);
        var geometry = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry"));
        Assert.Equal(4, geometry.GetProperty("walls").GetArrayLength());
        Assert.Equal(4, geometry.GetProperty("nodes").GetArrayLength());
        Assert.Equal(10.64, geometry.GetProperty("rooms")[0].GetProperty("innerAreaM2").GetDouble(), 3);
        Assert.Equal(10.64, geometry.GetProperty("summary").GetProperty("enclosedAreaM2").GetDouble(), 3);
        Assert.Equal(4, geometry.GetProperty("rooms")[0].GetProperty("room").GetProperty("boundaryWallIds").GetArrayLength());
    }

    [Fact]
    public async Task Balcony_area_is_reported_separately_from_enclosed_area()
    {
        var (_, variantId, floorId) = await api.CreateProjectWithFloor();
        await api.DrawRoom(variantId, floorId, 4000, 3000, "Living room");
        await api.DrawRoom(variantId, floorId, 2000, 1000, "Balcony", areaType: "open_area", originX: 6000);

        var summary = (await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry"))).GetProperty("summary");

        Assert.Equal(10.64, summary.GetProperty("enclosedAreaM2").GetDouble(), 3);
        Assert.Equal(1.8 * 0.8, summary.GetProperty("openAreaM2").GetDouble(), 3);
    }

    [Fact]
    public async Task Repeating_a_client_operation_id_does_not_apply_it_twice()
    {
        var (_, variantId, floorId) = await api.CreateProjectWithFloor();
        var operationId = Guid.NewGuid();
        var wall = new
        {
            wallId = Guid.NewGuid(), floorId, startNodeId = Guid.NewGuid(), startXMm = 0, startYMm = 0,
            endNodeId = Guid.NewGuid(), endXMm = 3000, endYMm = 0
        };

        var first = await ApiClient.Body(await api.Command(variantId, 1, "create_wall", wall, operationId));
        var retry = await ApiClient.Body(await api.Command(variantId, 1, "create_wall", wall, operationId));

        Assert.Equal(2, first.GetProperty("version").GetInt32());
        Assert.Equal(2, retry.GetProperty("version").GetInt32());
        Assert.Equal(2, await api.Version(variantId));
    }

    [Fact]
    public async Task Stale_expected_version_is_a_conflict()
    {
        var (_, variantId, floorId) = await api.CreateProjectWithFloor();
        await api.DrawRoom(variantId, floorId, 4000, 3000);

        var response = await api.Command(variantId, 2, "delete_room", new { roomId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("VERSION_CONFLICT", await ErrorCode(response));
    }

    [Fact]
    public async Task Rejected_geometry_is_not_saved_and_explains_why()
    {
        var (_, variantId, floorId) = await api.CreateProjectWithFloor();
        await api.DrawRoom(variantId, floorId, 4000, 3000);
        var version = await api.Version(variantId);

        var response = await api.Command(variantId, version, "create_wall", new
        {
            wallId = Guid.NewGuid(), floorId, startNodeId = Guid.NewGuid(), startXMm = -500, startYMm = 1500,
            endNodeId = Guid.NewGuid(), endXMm = 4500, endYMm = 1500
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await ApiClient.Body(response);
        Assert.Equal("GEOMETRY_VALIDATION_FAILED", body.GetProperty("errorCode").GetString());
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetProperty("code").GetString() == "WALL_INTERSECTION");
        Assert.Equal(version, await api.Version(variantId));
    }

    [Fact]
    public async Task Geometry_on_a_foreign_floor_is_rejected()
    {
        var (_, variantId, _) = await api.CreateProjectWithFloor();

        var response = await api.Command(variantId, 1, "create_wall", new
        {
            wallId = Guid.NewGuid(), floorId = Guid.NewGuid(), startNodeId = Guid.NewGuid(), startXMm = 0, startYMm = 0,
            endNodeId = Guid.NewGuid(), endXMm = 3000, endYMm = 0
        });

        Assert.Equal("FLOOR_NOT_FOUND", await ErrorCode(response));
    }

    [Fact]
    public async Task Unknown_command_and_bad_payload_are_bad_requests()
    {
        var (_, variantId, _) = await api.CreateProjectWithFloor();

        var unknown = await api.Command(variantId, 1, "explode_wall", new { });
        var missingIds = await api.Command(variantId, 1, "create_wall", new { startXMm = 1 });

        Assert.Equal("UNKNOWN_COMMAND", await ErrorCode(unknown));
        Assert.Equal(HttpStatusCode.BadRequest, missingIds.StatusCode);
    }

    [Fact]
    public async Task Original_layout_is_protected_after_approval_and_variants_are_independent()
    {
        var (projectId, originalId, floorId) = await api.CreateProjectWithFloor();
        await api.DrawRoom(originalId, floorId, 4000, 3000);

        var tooEarly = await api.Post($"/api/v1/projects/{projectId}/variants", new { name = "Option A" });
        Assert.Equal("ORIGINAL_NOT_APPROVED", await ErrorCode(tooEarly));

        Assert.Equal(HttpStatusCode.OK, (await api.Post($"/api/v1/variants/{originalId}/approve")).StatusCode);

        var blocked = await api.Command(originalId, 6, "delete_room", new { roomId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("ORIGINAL_LAYOUT_PROTECTED", await ErrorCode(blocked));
        Assert.Equal("ORIGINAL_LAYOUT_PROTECTED", await ErrorCode(await api.Delete($"/api/v1/variants/{originalId}")));

        var a = (await ApiClient.Body(await api.Post($"/api/v1/projects/{projectId}/variants", new { name = "Option A" }))).GetProperty("id").GetGuid();
        var b = (await ApiClient.Body(await api.Post($"/api/v1/projects/{projectId}/variants", new { name = "Option B" }))).GetProperty("id").GetGuid();

        await api.DrawRoom(a, floorId, 2000, 2000, "Extra room", originX: 8000);

        Assert.Equal(1, await WallCount(originalId, 4));
        Assert.Equal(1, await WallCount(b, 4));
        Assert.Equal(1, await WallCount(a, 8));
    }

    async Task<int> WallCount(Guid variantId, int expected)
    {
        var geometry = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry"));
        return geometry.GetProperty("walls").GetArrayLength() == expected ? 1 : 0;
    }

    [Fact]
    public async Task Restoring_an_old_version_adds_a_new_latest_version()
    {
        var (_, variantId, floorId) = await api.CreateProjectWithFloor();
        var drawn = await api.DrawRoom(variantId, floorId, 4000, 3000);
        var versions = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry/versions"));
        var versionOne = versions[0].GetProperty("id").GetGuid();

        var restored = await api.Post($"/api/v1/variants/{variantId}/geometry/versions/{versionOne}/restore",
            new { clientOperationId = Guid.NewGuid(), expectedVersion = drawn });

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(drawn + 1, (await ApiClient.Body(restored)).GetProperty("version").GetInt32());
        var geometry = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry"));
        Assert.Equal(0, geometry.GetProperty("walls").GetArrayLength());
        var history = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry/versions"));
        Assert.Equal(drawn + 1, history.GetArrayLength());
    }

    [Fact]
    public async Task Another_user_cannot_see_the_project()
    {
        var (projectId, variantId, _) = await api.CreateProjectWithFloor();
        factory.UserId = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/api/v1/projects/{projectId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/api/v1/variants/{variantId}/geometry")).StatusCode);
    }

    [Fact]
    public async Task Archived_projects_leave_the_list_and_projects_are_paged()
    {
        var first = (await api.CreateProjectWithFloor("One")).ProjectId;
        await api.CreateProjectWithFloor("Two");
        await api.CreateProjectWithFloor("Three");

        Assert.Equal(HttpStatusCode.NoContent, (await api.Delete($"/api/v1/projects/{first}")).StatusCode);

        var page = await ApiClient.Body(await api.Get("/api/v1/projects?page=1&pageSize=1"));
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
        Assert.Equal("Three", page.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Solver_keeps_constraints_through_the_api_round_trip()
    {
        var (_, variantId, floorId) = await api.CreateProjectWithFloor();
        var version = await api.DrawRoom(variantId, floorId, 4000, 3000);
        var geometry = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry"));
        var wallId = geometry.GetProperty("walls")[0].GetProperty("id").GetGuid();

        var response = await api.Command(variantId, version, "apply_measurement", new { wallId, valueMm = 3900 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry"));
        Assert.Contains(after.GetProperty("constraints").EnumerateArray(),
            c => c.GetProperty("type").GetString() == "fixed_length" && c.GetProperty("value").GetDouble() == 4100);
    }

    [Fact]
    public async Task A_constraint_conflict_names_the_conflicting_constraints_with_type_and_message()
    {
        var (_, variantId, floorId) = await api.CreateProjectWithFloor();
        var version = await api.DrawRoom(variantId, floorId, 4000, 3000);
        var geometry = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry"));
        // Boundary order: bottom, right, top, left.
        var walls = geometry.GetProperty("rooms")[0].GetProperty("room").GetProperty("boundaryWallIds").EnumerateArray().Select(w => w.GetGuid()).ToList();

        foreach (var (type, wall) in new[] { ("horizontal", walls[0]), ("vertical", walls[1]), ("horizontal", walls[2]), ("vertical", walls[3]) })
            version = (await ApiClient.Body(await api.Command(variantId, version, "add_constraint",
                new { constraintId = Guid.NewGuid(), type, targets = new[] { wall } }))).GetProperty("version").GetInt32();
        version = (await ApiClient.Body(await api.Command(variantId, version, "add_constraint",
            new { constraintId = Guid.NewGuid(), type = "fixed_length", targets = new[] { walls[0] }, value = 4000 }))).GetProperty("version").GetInt32();

        var response = await api.Command(variantId, version, "add_constraint",
            new { constraintId = Guid.NewGuid(), type = "fixed_length", targets = new[] { walls[2] }, value = 5000 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await ApiClient.Body(response);
        Assert.Equal("GEOMETRY_CONSTRAINT_CONFLICT", body.GetProperty("errorCode").GetString());
        var conflicts = body.GetProperty("conflicts").EnumerateArray().ToList();
        Assert.NotEmpty(conflicts);
        Assert.All(conflicts, c =>
        {
            Assert.NotEqual(Guid.Empty, c.GetProperty("constraintId").GetGuid());
            Assert.Matches("^[a-z_]+$", c.GetProperty("type").GetString()!);
            Assert.Contains("conflicts with the connected geometry", c.GetProperty("message").GetString());
        });
    }
}
