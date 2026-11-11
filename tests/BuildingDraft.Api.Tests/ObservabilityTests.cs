using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using BuildingDraft.Application;

namespace BuildingDraft.Api.Tests;

public class ObservabilityTests : IClassFixture<ApiFactory>
{
    readonly ApiClient api;
    readonly HttpClient http;

    public ObservabilityTests(ApiFactory factory)
    {
        factory.UserId = Guid.NewGuid();
        http = factory.CreateClient();
        api = new ApiClient(http);
    }

    [Fact]
    public async Task Liveness_and_readiness_report_healthy_with_a_working_database()
    {
        var live = await http.GetAsync("/health/live");
        var ready = await http.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_openapi_document_lists_the_public_endpoints()
    {
        var response = await http.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paths = (await ApiClient.Body(response)).GetProperty("paths");
        foreach (var path in new[] { "/api/v1/projects", "/api/v1/variants/{id}/geometry/commands", "/api/v1/sync/push", "/api/v1/geometry-versions/{versionId}/exports", "/api/v1/scan-sessions/{id}/apply" })
            Assert.True(paths.TryGetProperty(path, out _), $"{path} is missing from the OpenAPI document");
    }

    [Fact]
    public async Task Geometry_commands_and_exports_are_counted_by_outcome_with_bounded_tags()
    {
        var measurements = new ConcurrentBag<(string Instrument, Dictionary<string, string?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == Telemetry.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString()))));
        listener.Start();

        var (_, variantId, floorId) = await api.CreateProjectWithFloor();
        var version = await api.DrawRoom(variantId, floorId, 4000, 3000);
        await api.Command(variantId, version - 1, "delete_room", new { roomId = Guid.NewGuid() });
        await api.Command(variantId, version, "explode_wall", new { });
        var versions = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry/versions"));
        var job = await ApiClient.Body(await api.Post($"/api/v1/geometry-versions/{versions[versions.GetArrayLength() - 1].GetProperty("id").GetGuid()}/exports", new { format = "svg" }));
        // The listener sees the whole process, so wait for exactly the measurement this test expects.
        bool Has(string instrument, params (string Key, string Value)[] tags) =>
            measurements.Any(m => m.Instrument == instrument && tags.All(t => m.Tags.GetValueOrDefault(t.Key) == t.Value));

        for (var i = 0; i < 200 && !Has("building_draft.exports", ("format", "svg"), ("status", "completed")); i++)
            await Task.Delay(50);

        var dump = string.Join("; ", measurements.Select(m => $"{m.Instrument}[{string.Join(",", m.Tags.Select(t => $"{t.Key}={t.Value}"))}]").Distinct());
        Assert.True(Has("building_draft.geometry.commands", ("command_type", "create_wall"), ("outcome", "applied")), dump);
        Assert.True(Has("building_draft.geometry.commands", ("command_type", "delete_room"), ("outcome", "conflict")), dump);
        // A client-chosen command name must never become a metric tag value.
        Assert.True(Has("building_draft.geometry.commands", ("command_type", "unknown"), ("outcome", "rejected")), dump);
        Assert.DoesNotContain(measurements, m => m.Tags.GetValueOrDefault("command_type") == "explode_wall");
        Assert.True(Has("building_draft.exports", ("format", "svg"), ("status", "completed")), dump);
        Assert.NotEqual(Guid.Empty, job.GetProperty("id").GetGuid());
    }
}

public class HealthWithAuthenticationTests : IClassFixture<JwtApiFactory>
{
    readonly JwtApiFactory factory;

    public HealthWithAuthenticationTests(JwtApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task Health_endpoints_need_no_token_but_the_api_does()
    {
        var client = factory.ClientFor(null);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/projects")).StatusCode);
    }
}
