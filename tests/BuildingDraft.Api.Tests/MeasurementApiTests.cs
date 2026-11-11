using System.Net;
using System.Text.Json;

namespace BuildingDraft.Api.Tests;

public class MeasurementApiTests : IClassFixture<ApiFactory>
{
    readonly ApiClient api;

    public MeasurementApiTests(ApiFactory factory)
    {
        factory.UserId = Guid.NewGuid();
        api = new ApiClient(factory.CreateClient());
    }

    async Task<(Guid ProjectId, Guid VariantId, Guid WallId, int Version)> Room()
    {
        var (projectId, variantId, floorId) = await api.CreateProjectWithFloor();
        var version = await api.DrawRoom(variantId, floorId, 4000, 3000);
        var geometry = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry"));
        return (projectId, variantId, geometry.GetProperty("walls")[0].GetProperty("id").GetGuid(), version);
    }

    Task<HttpResponseMessage> Record(Guid projectId, Guid variantId, Guid wallId, double value, string source = "laser_manual", double? confidence = null) =>
        api.Post($"/api/v1/projects/{projectId}/measurements",
            new { variantId, targetObjectId = wallId, valueMm = value, source, confidence, deviceName = "Laser X" });

    static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    [Fact]
    public async Task Recording_a_laser_value_keeps_source_and_starts_unverified()
    {
        var (projectId, variantId, wallId, _) = await Room();

        var response = await Record(projectId, variantId, wallId, 4250);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var m = await ApiClient.Body(response);
        Assert.Equal("laser_manual", m.GetProperty("source").GetString());
        Assert.Equal("unverified", m.GetProperty("status").GetString());
        Assert.Equal("inner", m.GetProperty("reference").GetString());
        Assert.False(m.GetProperty("isEstimate").GetBoolean());
    }

    [Fact]
    public async Task Measuring_a_wall_that_does_not_exist_is_rejected()
    {
        var (projectId, variantId, _, _) = await Room();

        var response = await Record(projectId, variantId, Guid.NewGuid(), 4250);

        Assert.Equal("TARGET_NOT_FOUND", (await ApiClient.Body(response)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task An_ai_estimate_cannot_be_verified_or_applied()
    {
        var (projectId, variantId, wallId, version) = await Room();
        var estimate = Id(await ApiClient.Body(await Record(projectId, variantId, wallId, 4100, "ai_recognition", 0.7)));

        var verify = await api.Put($"/api/v1/measurements/{estimate}/status", new { status = "verified" });
        var apply = await api.Post($"/api/v1/measurements/{estimate}/apply", new { clientOperationId = Guid.NewGuid(), expectedVersion = version });

        Assert.Equal("MEASUREMENT_NOT_VERIFIABLE", (await ApiClient.Body(verify)).GetProperty("errorCode").GetString());
        Assert.Equal("MEASUREMENT_NOT_EXACT", (await ApiClient.Body(apply)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Applying_a_measurement_resizes_the_wall_and_pins_its_length()
    {
        var (projectId, variantId, wallId, version) = await Room();
        var measurement = Id(await ApiClient.Body(await Record(projectId, variantId, wallId, 4250)));

        var response = await api.Post($"/api/v1/measurements/{measurement}/apply", new { clientOperationId = Guid.NewGuid(), expectedVersion = version });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(version + 1, (await ApiClient.Body(response)).GetProperty("version").GetInt32());

        var geometry = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry"));
        Assert.Contains(geometry.GetProperty("constraints").EnumerateArray(),
            c => c.GetProperty("type").GetString() == "fixed_length" && c.GetProperty("value").GetDouble() == 4450);

        var list = await ApiClient.Body(await api.Get($"/api/v1/projects/{projectId}/measurements?targetObjectId={wallId}"));
        Assert.True(list[0].GetProperty("isApplied").GetBoolean());
        Assert.Equal(version + 1, list[0].GetProperty("appliedVersionNumber").GetInt32());
    }

    [Fact]
    public async Task A_newer_applied_measurement_supersedes_the_older_one_and_history_stays()
    {
        var (projectId, variantId, wallId, version) = await Room();
        var first = Id(await ApiClient.Body(await Record(projectId, variantId, wallId, 4250)));
        await api.Post($"/api/v1/measurements/{first}/apply", new { clientOperationId = Guid.NewGuid(), expectedVersion = version });
        var second = Id(await ApiClient.Body(await Record(projectId, variantId, wallId, 4300)));

        var response = await api.Post($"/api/v1/measurements/{second}/apply", new { clientOperationId = Guid.NewGuid(), expectedVersion = version + 1 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await ApiClient.Body(await api.Get($"/api/v1/projects/{projectId}/measurements"));
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal("superseded", list.EnumerateArray().Single(m => Id(m) == first).GetProperty("status").GetString());
        Assert.Equal("unverified", list.EnumerateArray().Single(m => Id(m) == second).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_rejected_apply_leaves_the_measurement_unapplied()
    {
        var (projectId, variantId, wallId, version) = await Room();
        await api.Command(variantId, version, "attach_opening",
            new { openingId = Guid.NewGuid(), wallId, kind = "door", offsetMm = 500, widthMm = 900, heightMm = 2100 });
        version++;
        // A 500 mm wall cannot hold the door that is already attached to it.
        var tooShort = Id(await ApiClient.Body(await Record(projectId, variantId, wallId, 300)));

        var response = await api.Post($"/api/v1/measurements/{tooShort}/apply", new { clientOperationId = Guid.NewGuid(), expectedVersion = version });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var list = await ApiClient.Body(await api.Get($"/api/v1/projects/{projectId}/measurements"));
        Assert.False(list[0].GetProperty("isApplied").GetBoolean());
        Assert.Equal(version, await api.Version(variantId));
    }

    [Fact]
    public async Task Verified_then_rejected_is_allowed_but_rejected_is_final()
    {
        var (projectId, variantId, wallId, _) = await Room();
        var m = Id(await ApiClient.Body(await Record(projectId, variantId, wallId, 4250)));

        Assert.Equal(HttpStatusCode.OK, (await api.Put($"/api/v1/measurements/{m}/status", new { status = "verified" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Put($"/api/v1/measurements/{m}/status", new { status = "rejected" })).StatusCode);

        var again = await api.Put($"/api/v1/measurements/{m}/status", new { status = "verified" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }
}
