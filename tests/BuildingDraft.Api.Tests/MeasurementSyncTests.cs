using System.Text.Json;

namespace BuildingDraft.Api.Tests;

// A measurement taken offline is sent as extra fields on apply_measurement; the server records it with the version.
public class MeasurementSyncTests : IClassFixture<ApiFactory>
{
    readonly ApiClient api;

    public MeasurementSyncTests(ApiFactory factory)
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

    object Payload(Guid wallId, Guid measurementId, double value = 3900, string source = "laser_manual", bool verified = false, string? notes = "kitchen wall") => new
    {
        wallId, valueMm = value, reference = "inner", measurementId, source, verified, notes, deviceName = "Bosch GLM", capturedAt = "2026-11-10T09:30:00+00:00"
    };

    async Task<JsonElement> Push(Guid variantId, int expected, object payload, Guid? operationId = null) =>
        (await ApiClient.Body(await api.Post("/api/v1/sync/push", new
        {
            operations = new[] { new { operationId = operationId ?? Guid.NewGuid(), variantId, expectedVersion = expected, commandType = "apply_measurement", payload } }
        }))).GetProperty("results")[0];

    async Task<List<JsonElement>> Measurements(Guid projectId) =>
        (await ApiClient.Body(await api.Get($"/api/v1/projects/{projectId}/measurements"))).EnumerateArray().ToList();

    [Fact]
    public async Task A_synced_measurement_is_recorded_with_its_source_notes_and_the_version_it_was_applied_at()
    {
        var (projectId, variantId, wallId, version) = await Room();
        var id = Guid.NewGuid();

        var result = await Push(variantId, version, Payload(wallId, id));

        Assert.Equal("acknowledged", result.GetProperty("status").GetString());
        var m = (await Measurements(projectId)).Single();
        Assert.Equal(id, m.GetProperty("id").GetGuid());
        Assert.Equal("laser_manual", m.GetProperty("source").GetString());
        Assert.Equal("unverified", m.GetProperty("status").GetString());
        Assert.Equal("kitchen wall", m.GetProperty("notes").GetString());
        Assert.Equal("Bosch GLM", m.GetProperty("deviceName").GetString());
        Assert.Equal(3900, m.GetProperty("valueMm").GetDouble());
        Assert.Equal("inner", m.GetProperty("reference").GetString());
        Assert.True(m.GetProperty("isApplied").GetBoolean());
        Assert.Equal(version + 1, m.GetProperty("appliedVersionNumber").GetInt32());
        Assert.Contains("2026-11-10T09:30", m.GetProperty("capturedAt").GetString());
        Assert.Equal(wallId, m.GetProperty("targetObjectId").GetGuid());
    }

    [Fact]
    public async Task Only_an_explicit_confirmation_makes_a_measurement_verified()
    {
        var (projectId, variantId, wallId, version) = await Room();

        await Push(variantId, version, Payload(wallId, Guid.NewGuid(), verified: true));

        Assert.Equal("verified", (await Measurements(projectId)).Single().GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_geometry_change_and_the_record_are_one_step_so_the_wall_really_is_fixed()
    {
        var (_, variantId, wallId, version) = await Room();

        await Push(variantId, version, Payload(wallId, Guid.NewGuid(), value: 3900));

        var geometry = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry"));
        Assert.Contains(geometry.GetProperty("constraints").EnumerateArray(),
            c => c.GetProperty("type").GetString() == "fixed_length" && c.GetProperty("value").GetDouble() == 4100);
        Assert.Equal(version + 1, geometry.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Sending_the_same_operation_again_records_nothing_new()
    {
        var (projectId, variantId, wallId, version) = await Room();
        var operation = Guid.NewGuid();
        var payload = Payload(wallId, Guid.NewGuid());

        await Push(variantId, version, payload, operation);
        var retry = await Push(variantId, version, payload, operation);

        Assert.Equal("acknowledged", retry.GetProperty("status").GetString());
        Assert.Single(await Measurements(projectId));
        Assert.Equal(version + 1, await api.Version(variantId));
    }

    [Fact]
    public async Task The_same_measurement_id_under_a_new_operation_is_not_recorded_twice()
    {
        var (projectId, variantId, wallId, version) = await Room();
        var id = Guid.NewGuid();

        await Push(variantId, version, Payload(wallId, id));
        await Push(variantId, version + 1, Payload(wallId, id));

        Assert.Single(await Measurements(projectId));
    }

    [Fact]
    public async Task A_newer_measurement_of_the_same_wall_supersedes_the_older_one_and_history_stays()
    {
        var (projectId, variantId, wallId, version) = await Room();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await Push(variantId, version, Payload(wallId, first, value: 3900));
        await Push(variantId, version + 1, Payload(wallId, second, value: 3950));

        var all = await Measurements(projectId);
        Assert.Equal(2, all.Count);
        Assert.Equal("superseded", all.Single(m => m.GetProperty("id").GetGuid() == first).GetProperty("status").GetString());
        Assert.Equal("unverified", all.Single(m => m.GetProperty("id").GetGuid() == second).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("ar_lidar")]
    [InlineData("ai_recognition")]
    public async Task An_estimate_can_never_arrive_as_an_exact_length(string source)
    {
        var (projectId, variantId, wallId, version) = await Room();

        var result = await Push(variantId, version, Payload(wallId, Guid.NewGuid(), source: source));

        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Empty(await Measurements(projectId));
        Assert.Equal(version, await api.Version(variantId));
    }

    [Fact]
    public async Task An_unknown_source_is_refused_without_changing_the_plan()
    {
        var (projectId, variantId, wallId, version) = await Room();

        var result = await Push(variantId, version, Payload(wallId, Guid.NewGuid(), source: "psychic"));

        Assert.Equal("INVALID_MEASUREMENT_SOURCE", result.GetProperty("errorCode").GetString());
        Assert.Empty(await Measurements(projectId));
        Assert.Equal(version, await api.Version(variantId));
    }

    [Fact]
    public async Task A_plain_apply_measurement_without_a_record_still_works_and_records_nothing()
    {
        var (projectId, variantId, wallId, version) = await Room();

        var response = await api.Command(variantId, version, "apply_measurement", new { wallId, valueMm = 3900 });

        Assert.True(response.IsSuccessStatusCode);
        Assert.Empty(await Measurements(projectId));
    }

    [Fact]
    public async Task The_direct_command_endpoint_records_too_and_the_audit_log_notes_it()
    {
        var (projectId, variantId, wallId, version) = await Room();

        var response = await api.Command(variantId, version, "apply_measurement", Payload(wallId, Guid.NewGuid()));

        Assert.True(response.IsSuccessStatusCode);
        Assert.Single(await Measurements(projectId));
        var audit = await ApiClient.Body(await api.Get($"/api/v1/projects/{projectId}/audit"));
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), e => e.GetProperty("action").GetString() == "measurement.applied");
    }

    [Fact]
    public async Task A_measurement_that_breaks_the_geometry_is_refused_and_leaves_no_record()
    {
        var (projectId, variantId, wallId, version) = await Room();
        await api.Command(variantId, version, "attach_opening", new { openingId = Guid.NewGuid(), wallId, kind = "door", offsetMm = 500, widthMm = 900, heightMm = 2100 });

        var result = await Push(variantId, version + 1, Payload(wallId, Guid.NewGuid(), value: 300));

        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Empty(await Measurements(projectId));
    }

    [Fact]
    public async Task Several_measurements_in_one_push_supersede_each_other_in_order()
    {
        var (projectId, variantId, wallId, version) = await Room();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var response = await ApiClient.Body(await api.Post("/api/v1/sync/push", new
        {
            operations = new[]
            {
                new { operationId = Guid.NewGuid(), variantId, expectedVersion = version, commandType = "apply_measurement", payload = Payload(wallId, first, value: 3800) },
                new { operationId = Guid.NewGuid(), variantId, expectedVersion = version + 1, commandType = "apply_measurement", payload = Payload(wallId, second, value: 3850, verified: true) }
            }
        }));

        Assert.All(response.GetProperty("results").EnumerateArray(), r => Assert.Equal("acknowledged", r.GetProperty("status").GetString()));
        var all = await Measurements(projectId);
        Assert.Equal("superseded", all.Single(m => m.GetProperty("id").GetGuid() == first).GetProperty("status").GetString());
        Assert.Equal("verified", all.Single(m => m.GetProperty("id").GetGuid() == second).GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_same_measurement_id_twice_in_one_push_is_recorded_once()
    {
        var (projectId, variantId, wallId, version) = await Room();
        var id = Guid.NewGuid();

        var response = await ApiClient.Body(await api.Post("/api/v1/sync/push", new
        {
            operations = new[]
            {
                new { operationId = Guid.NewGuid(), variantId, expectedVersion = version, commandType = "apply_measurement", payload = Payload(wallId, id) },
                new { operationId = Guid.NewGuid(), variantId, expectedVersion = version + 1, commandType = "apply_measurement", payload = Payload(wallId, id) }
            }
        }));

        Assert.All(response.GetProperty("results").EnumerateArray(), r => Assert.Equal("acknowledged", r.GetProperty("status").GetString()));
        Assert.Single(await Measurements(projectId));
    }
}
