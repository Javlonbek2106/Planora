using System.Net;
using System.Text.Json;

namespace BuildingDraft.Api.Tests;

public class SyncApiTests : IClassFixture<ApiFactory>
{
    readonly ApiClient api;
    readonly Guid floorId;
    readonly Guid variantId;
    readonly Guid projectId;

    public SyncApiTests(ApiFactory factory)
    {
        factory.UserId = Guid.NewGuid();
        api = new ApiClient(factory.CreateClient());
        (projectId, variantId, floorId) = api.CreateProjectWithFloor().GetAwaiter().GetResult();
    }

    object Wall(double x1, double y1, double x2, double y2, Guid? id = null) => new
    {
        wallId = id ?? Guid.NewGuid(), floorId, startNodeId = Guid.NewGuid(), startXMm = x1, startYMm = y1,
        endNodeId = Guid.NewGuid(), endXMm = x2, endYMm = y2
    };

    object Op(int expectedVersion, object payload, string type = "create_wall", Guid? id = null, Guid? variant = null) => new
    {
        operationId = id ?? Guid.NewGuid(), variantId = variant ?? variantId, expectedVersion, commandType = type, payload
    };

    async Task<JsonElement> Push(params object[] ops) =>
        await ApiClient.Body(await api.Post("/api/v1/sync/push", new { deviceId = "phone-1", operations = ops }));

    static string[] Statuses(JsonElement push) =>
        push.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("status").GetString()!).ToArray();

    [Fact]
    public async Task An_offline_chain_is_applied_in_order()
    {
        var push = await Push(Op(1, Wall(0, 0, 3000, 0)), Op(2, Wall(0, 1000, 3000, 1000)), Op(3, Wall(0, 2000, 3000, 2000)));

        Assert.Equal(["acknowledged", "acknowledged", "acknowledged"], Statuses(push));
        Assert.Equal([2, 3, 4], push.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("version").GetInt32()));
        Assert.Equal(4, await api.Version(variantId));
    }

    [Fact]
    public async Task Pushing_the_same_operations_again_changes_nothing()
    {
        var ops = new[] { Op(1, Wall(0, 0, 3000, 0)), Op(2, Wall(0, 1000, 3000, 1000)) };
        await Push(ops);

        var retry = await Push(ops);

        Assert.Equal(["acknowledged", "acknowledged"], Statuses(retry));
        Assert.Equal([2, 3], retry.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("version").GetInt32()));
        Assert.Equal(3, await api.Version(variantId));
    }

    [Fact]
    public async Task A_duplicate_operation_id_inside_one_push_is_applied_once()
    {
        var id = Guid.NewGuid();
        var wall = Wall(0, 0, 3000, 0);

        var push = await Push(Op(1, wall, id: id), Op(1, wall, id: id));

        Assert.Equal(["acknowledged", "acknowledged"], Statuses(push));
        Assert.Equal(2, await api.Version(variantId));
    }

    [Fact]
    public async Task Stale_operations_conflict_and_block_the_rest_of_the_variant()
    {
        await api.Command(variantId, 1, "create_wall", Wall(0, 5000, 3000, 5000));

        var push = await Push(Op(1, Wall(0, 0, 3000, 0)), Op(2, Wall(0, 1000, 3000, 1000)));

        Assert.Equal(["conflict", "conflict"], Statuses(push));
        var results = push.GetProperty("results");
        Assert.Equal(2, results[0].GetProperty("currentVersion").GetInt32());
        Assert.Equal("VERSION_CONFLICT", results[0].GetProperty("errorCode").GetString());
        Assert.Equal("BLOCKED_BY_PREVIOUS_OPERATION", results[1].GetProperty("errorCode").GetString());
        Assert.Equal(2, await api.Version(variantId));
    }

    [Fact]
    public async Task A_failed_operation_keeps_the_earlier_ones_and_blocks_the_later_ones()
    {
        var push = await Push(
            Op(1, Wall(0, 0, 3000, 0)),
            Op(2, Wall(1500, -500, 1500, 500)),
            Op(3, Wall(0, 2000, 3000, 2000)));

        Assert.Equal(["acknowledged", "failed", "failed"], Statuses(push));
        var results = push.GetProperty("results");
        Assert.Equal("GEOMETRY_VALIDATION_FAILED", results[1].GetProperty("errorCode").GetString());
        Assert.Contains(results[1].GetProperty("errors").EnumerateArray(), e => e.GetProperty("code").GetString() == "WALL_INTERSECTION");
        Assert.Equal(2, await api.Version(variantId));
    }

    [Fact]
    public async Task The_approved_original_layout_rejects_synced_changes()
    {
        await api.Post($"/api/v1/variants/{variantId}/approve");

        var push = await Push(Op(1, Wall(0, 0, 3000, 0)));

        Assert.Equal(["failed"], Statuses(push));
        Assert.Equal("ORIGINAL_LAYOUT_PROTECTED", push.GetProperty("results")[0].GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Operations_for_an_unknown_variant_fail_without_affecting_others()
    {
        var push = await Push(Op(1, Wall(0, 0, 3000, 0), variant: Guid.NewGuid()), Op(1, Wall(0, 0, 3000, 0)));

        Assert.Equal(["failed", "acknowledged"], Statuses(push));
        Assert.Equal("NOT_FOUND", push.GetProperty("results")[0].GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Pull_returns_the_missed_operations_in_order()
    {
        await Push(Op(1, Wall(0, 0, 3000, 0)), Op(2, Wall(0, 1000, 3000, 1000)), Op(3, Wall(0, 2000, 3000, 2000)));

        var pull = await ApiClient.Body(await api.Post("/api/v1/sync/pull", new { variants = new[] { new { variantId, version = 2 } } }));

        var changes = pull.GetProperty("variants")[0];
        Assert.Equal(4, changes.GetProperty("currentVersion").GetInt32());
        Assert.False(changes.GetProperty("snapshotRequired").GetBoolean());
        var ops = changes.GetProperty("operations");
        Assert.Equal([3, 4], ops.EnumerateArray().Select(o => o.GetProperty("resultVersion").GetInt32()));
        Assert.Equal("create_wall", ops[0].GetProperty("commandType").GetString());
        Assert.True(ops[0].GetProperty("payload").TryGetProperty("wallId", out _));
    }

    [Fact]
    public async Task Pull_is_empty_when_up_to_date_and_flags_unknown_variants()
    {
        var pull = await ApiClient.Body(await api.Post("/api/v1/sync/pull",
            new { variants = new[] { new { variantId, version = 1 }, new { variantId = Guid.NewGuid(), version = 1 } } }));

        var variants = pull.GetProperty("variants");
        Assert.Equal(0, variants[0].GetProperty("operations").GetArrayLength());
        Assert.True(variants[0].GetProperty("found").GetBoolean());
        Assert.False(variants[1].GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task Pull_asks_for_a_snapshot_after_a_restore()
    {
        await Push(Op(1, Wall(0, 0, 3000, 0)), Op(2, Wall(0, 1000, 3000, 1000)));
        var versions = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry/versions"));
        await api.Post($"/api/v1/variants/{variantId}/geometry/versions/{versions[0].GetProperty("id").GetGuid()}/restore",
            new { clientOperationId = Guid.NewGuid(), expectedVersion = 3 });

        var pull = await ApiClient.Body(await api.Post("/api/v1/sync/pull", new { variants = new[] { new { variantId, version = 2 } } }));

        var changes = pull.GetProperty("variants")[0];
        Assert.True(changes.GetProperty("snapshotRequired").GetBoolean());
        Assert.Equal(0, changes.GetProperty("operations").GetArrayLength());
    }

    [Fact]
    public async Task Keep_mine_replays_local_work_on_top_of_the_server_version()
    {
        await api.Command(variantId, 1, "create_wall", Wall(0, 5000, 3000, 5000));
        var mine = new[] { Op(1, Wall(0, 0, 3000, 0)), Op(2, Wall(0, 1000, 3000, 1000)) };
        Assert.Equal(["conflict", "conflict"], Statuses(await Push(mine)));

        var resolved = await ApiClient.Body(await api.Post("/api/v1/sync/resolve-conflict",
            new { variantId, resolution = "keep_mine", operations = mine }));

        Assert.Equal(["acknowledged", "acknowledged"], Statuses(resolved));
        Assert.Equal(4, resolved.GetProperty("currentVersion").GetInt32());
        var geometry = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry"));
        Assert.Equal(3, geometry.GetProperty("walls").GetArrayLength());
    }

    [Fact]
    public async Task Keep_mine_is_all_or_nothing()
    {
        await api.Command(variantId, 1, "create_wall", Wall(0, 5000, 3000, 5000));
        var mine = new[] { Op(1, Wall(0, 0, 3000, 0)), Op(2, Wall(1500, -500, 1500, 500)) };

        var resolved = await ApiClient.Body(await api.Post("/api/v1/sync/resolve-conflict",
            new { variantId, resolution = "keep_mine", operations = mine }));

        Assert.Equal(["failed", "failed"], Statuses(resolved));
        Assert.Equal("ROLLED_BACK", resolved.GetProperty("results")[0].GetProperty("errorCode").GetString());
        Assert.Equal(2, await api.Version(variantId));
    }

    [Fact]
    public async Task Keep_server_discards_nothing_on_the_server_and_reports_the_version()
    {
        await api.Command(variantId, 1, "create_wall", Wall(0, 5000, 3000, 5000));

        var resolved = await ApiClient.Body(await api.Post("/api/v1/sync/resolve-conflict", new { variantId, resolution = "keep_server" }));

        Assert.Equal(2, resolved.GetProperty("currentVersion").GetInt32());
        Assert.Equal(0, resolved.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task Keep_mine_without_operations_is_a_bad_request()
    {
        var response = await api.Post("/api/v1/sync/resolve-conflict", new { variantId, resolution = "keep_mine" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
