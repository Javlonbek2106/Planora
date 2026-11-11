using System.Net;
using System.Text.Json;

namespace BuildingDraft.Api.Tests;

public class ScanApiTests : IClassFixture<ApiFactory>
{
    readonly ApiFactory factory;
    readonly ApiClient api;

    public ScanApiTests(ApiFactory factory)
    {
        this.factory = factory;
        factory.UserId = Guid.NewGuid();
        api = new ApiClient(factory.CreateClient());
    }

    static object NoisyRoom(double confidence = 0.82) => new
    {
        walls = new object[]
        {
            new { startXMm = 0, startYMm = 0, endXMm = 4003, endYMm = 4 },
            new { startXMm = 3998, startYMm = -3, endXMm = 4001, endYMm = 3002 },
            new { startXMm = 4000, startYMm = 2999, endXMm = 5, endYMm = 3004 },
            new { startXMm = -2, startYMm = 3001, endXMm = 2, endYMm = -1 }
        },
        openings = new object[] { new { wallIndex = 0, kind = "door", offsetMm = 500, widthMm = 900, heightMm = 2100 } },
        confidence,
        deviceModel = "iPhone 15 Pro"
    };

    async Task<(Guid ProjectId, Guid VariantId, Guid FloorId, Guid SessionId)> Session(string source = "ar_lidar")
    {
        var (projectId, variantId, floorId) = await api.CreateProjectWithFloor();
        var created = await api.Post($"/api/v1/projects/{projectId}/scan-sessions", new { variantId, floorId, source });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (projectId, variantId, floorId, (await ApiClient.Body(created)).GetProperty("id").GetGuid());
    }

    Task<HttpResponseMessage> Process(Guid sessionId, object capture) =>
        api.Post($"/api/v1/scan-sessions/{sessionId}/process", new { capture });

    [Fact]
    public async Task A_scan_is_processed_reviewed_and_applied_as_one_version()
    {
        var (projectId, variantId, _, sessionId) = await Session();

        var processed = await ApiClient.Body(await Process(sessionId, NoisyRoom()));
        Assert.Equal("processed", processed.GetProperty("status").GetString());

        var result = await ApiClient.Body(await api.Get($"/api/v1/scan-sessions/{sessionId}/result"));
        Assert.True(result.GetProperty("isEstimate").GetBoolean());
        Assert.Equal(4, result.GetProperty("wallCount").GetInt32());
        Assert.Equal(1, result.GetProperty("openingCount").GetInt32());
        Assert.Equal(4, result.GetProperty("proposal").GetProperty("nodes").GetArrayLength());
        Assert.True(result.GetProperty("preview").GetProperty("isValid").GetBoolean());
        Assert.Equal(1, await api.Version(variantId));

        var applied = await api.Post($"/api/v1/scan-sessions/{sessionId}/apply", new { clientOperationId = Guid.NewGuid(), expectedVersion = 1 });

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(2, (await ApiClient.Body(applied)).GetProperty("version").GetInt32());
        var geometry = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry"));
        Assert.Equal(4, geometry.GetProperty("walls").GetArrayLength());
        Assert.Equal(1, geometry.GetProperty("openings").GetArrayLength());

        var measurements = await ApiClient.Body(await api.Get($"/api/v1/projects/{projectId}/measurements"));
        Assert.Equal(4, measurements.GetArrayLength());
        Assert.All(measurements.EnumerateArray(), m =>
        {
            Assert.Equal("ar_lidar", m.GetProperty("source").GetString());
            Assert.True(m.GetProperty("isEstimate").GetBoolean());
            Assert.Equal("unverified", m.GetProperty("status").GetString());
            Assert.Equal(0.82, m.GetProperty("confidence").GetDouble(), 3);
            Assert.False(m.GetProperty("isApplied").GetBoolean());
        });
    }

    [Fact]
    public async Task Applying_before_processing_or_twice_is_a_conflict()
    {
        var (_, variantId, _, sessionId) = await Session();

        var early = await api.Post($"/api/v1/scan-sessions/{sessionId}/apply", new { clientOperationId = Guid.NewGuid(), expectedVersion = 1 });
        Assert.Equal("SCAN_NOT_PROCESSED", (await ApiClient.Body(early)).GetProperty("errorCode").GetString());

        await Process(sessionId, NoisyRoom());
        await api.Post($"/api/v1/scan-sessions/{sessionId}/apply", new { clientOperationId = Guid.NewGuid(), expectedVersion = 1 });
        var again = await api.Post($"/api/v1/scan-sessions/{sessionId}/apply", new { clientOperationId = Guid.NewGuid(), expectedVersion = 2 });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("SCAN_ALREADY_APPLIED", (await ApiClient.Body(again)).GetProperty("errorCode").GetString());
        Assert.Equal(2, await api.Version(variantId));
    }

    [Fact]
    public async Task Reprocessing_replaces_the_proposal_until_it_is_applied()
    {
        var (_, _, _, sessionId) = await Session();
        await Process(sessionId, NoisyRoom());

        await Process(sessionId, new
        {
            walls = new object[] { new { startXMm = 0, startYMm = 0, endXMm = 2000, endYMm = 0 } },
            confidence = 0.5
        });

        var result = await ApiClient.Body(await api.Get($"/api/v1/scan-sessions/{sessionId}/result"));
        Assert.Equal(1, result.GetProperty("wallCount").GetInt32());
        Assert.Equal(0.5, result.GetProperty("session").GetProperty("confidence").GetDouble());
    }

    [Fact]
    public async Task A_scan_that_would_break_the_geometry_is_flagged_in_the_preview_and_rejected_on_apply()
    {
        var (_, variantId, _, sessionId) = await Session();
        await Process(sessionId, new
        {
            walls = new object[]
            {
                new { startXMm = 0, startYMm = 0, endXMm = 3000, endYMm = 3000 },
                new { startXMm = 0, startYMm = 3000, endXMm = 3000, endYMm = 0 }
            },
            confidence = 0.7
        });

        var result = await ApiClient.Body(await api.Get($"/api/v1/scan-sessions/{sessionId}/result"));
        Assert.False(result.GetProperty("preview").GetProperty("isValid").GetBoolean());
        Assert.Contains(result.GetProperty("preview").GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("code").GetString() == "WALL_INTERSECTION");

        var applied = await api.Post($"/api/v1/scan-sessions/{sessionId}/apply", new { clientOperationId = Guid.NewGuid(), expectedVersion = 1 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, applied.StatusCode);
        Assert.Equal(1, await api.Version(variantId));
        var session = await ApiClient.Body(await api.Get($"/api/v1/scan-sessions/{sessionId}/result"));
        Assert.Equal("processed", session.GetProperty("session").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Only_estimate_sources_can_open_a_scan_session()
    {
        var (projectId, variantId, floorId) = await api.CreateProjectWithFloor();

        var response = await api.Post($"/api/v1/projects/{projectId}/scan-sessions", new { variantId, floorId, source = "laser_manual" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_SCAN_SOURCE", (await ApiClient.Body(response)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task A_capture_without_confidence_is_rejected()
    {
        var (_, _, _, sessionId) = await Session();

        var response = await Process(sessionId, new { walls = new object[] { new { startXMm = 0, startYMm = 0, endXMm = 2000, endYMm = 0 } } });

        Assert.Equal("INVALID_CONFIDENCE", (await ApiClient.Body(response)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task An_approved_original_layout_cannot_receive_a_scan()
    {
        var (_, variantId, _, sessionId) = await Session();
        await Process(sessionId, NoisyRoom());
        await api.Post($"/api/v1/variants/{variantId}/approve");

        var applied = await api.Post($"/api/v1/scan-sessions/{sessionId}/apply", new { clientOperationId = Guid.NewGuid(), expectedVersion = 1 });

        Assert.Equal("ORIGINAL_LAYOUT_PROTECTED", (await ApiClient.Body(applied)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task A_scan_session_of_another_project_floor_is_rejected_and_other_users_see_nothing()
    {
        var (projectId, variantId, _, sessionId) = await Session();

        var foreignFloor = await api.Post($"/api/v1/projects/{projectId}/scan-sessions", new { variantId, floorId = Guid.NewGuid(), source = "ar_lidar" });
        Assert.Equal("FLOOR_NOT_FOUND", (await ApiClient.Body(foreignFloor)).GetProperty("errorCode").GetString());

        factory.UserId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/api/v1/scan-sessions/{sessionId}/result")).StatusCode);
    }
}
