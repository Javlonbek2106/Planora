using System.Net;
using System.Text;
using System.Text.Json;

namespace BuildingDraft.Api.Tests;

public class ExportApiTests : IClassFixture<ApiFactory>
{
    readonly ApiFactory factory;
    readonly ApiClient api;

    public ExportApiTests(ApiFactory factory)
    {
        this.factory = factory;
        factory.UserId = Guid.NewGuid();
        api = new ApiClient(factory.CreateClient());
    }

    async Task<Guid> LatestVersionId(Guid variantId)
    {
        var versions = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry/versions"));
        return versions[versions.GetArrayLength() - 1].GetProperty("id").GetGuid();
    }

    Task<HttpResponseMessage> Export(Guid versionId, string format, Guid? floorId = null) =>
        api.Post($"/api/v1/geometry-versions/{versionId}/exports", new { format, floorId });

    [Theory]
    [InlineData("svg", "image/svg+xml", ".svg", "<svg")]
    [InlineData("dxf", "application/dxf", ".dxf", "ENTITIES")]
    [InlineData("pdf", "application/pdf", ".pdf", "%PDF-1.4")]
    [InlineData("obj", "model/obj", ".obj", "g walls")]
    public async Task Exports_complete_and_download_with_the_right_content_type(string format, string contentType, string extension, string marker)
    {
        var (_, variantId, floorId) = await api.CreateProjectWithFloor("Flat 12");
        await api.DrawRoom(variantId, floorId, 4000, 3000);

        var created = await Export(await LatestVersionId(variantId), format);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var job = await ApiClient.Body(created);
        Assert.Equal("completed", job.GetProperty("status").GetString());

        var id = job.GetProperty("id").GetGuid();
        Assert.Equal("completed", (await ApiClient.Body(await api.Get($"/api/v1/exports/{id}"))).GetProperty("status").GetString());

        var download = await api.Get($"/api/v1/exports/{id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(contentType, download.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(extension, download.Content.Headers.ContentDisposition!.FileNameStar ?? download.Content.Headers.ContentDisposition.FileName);
        Assert.Contains(marker, Encoding.Latin1.GetString(await download.Content.ReadAsByteArrayAsync()));
    }

    [Theory]
    [InlineData("png")]
    [InlineData("fbx")]
    [InlineData("dwg")]
    [InlineData("ifc")]
    public async Task Formats_that_are_not_implemented_are_rejected_clearly(string format)
    {
        var (_, variantId, floorId) = await api.CreateProjectWithFloor();
        await api.DrawRoom(variantId, floorId, 4000, 3000);

        var response = await Export(await LatestVersionId(variantId), format);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("EXPORT_FORMAT_NOT_SUPPORTED", (await ApiClient.Body(response)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task An_export_is_bound_to_the_version_it_was_requested_for()
    {
        var (_, variantId, floorId) = await api.CreateProjectWithFloor();
        await api.DrawRoom(variantId, floorId, 4000, 3000);
        var first = await LatestVersionId(variantId);
        await api.DrawRoom(variantId, floorId, 2000, 2000, "Second room", originX: 8000);
        var second = await LatestVersionId(variantId);

        async Task<string> Svg(Guid versionId)
        {
            var job = await ApiClient.Body(await Export(versionId, "svg"));
            var download = await api.Get($"/api/v1/exports/{job.GetProperty("id").GetGuid()}/download");
            return await download.Content.ReadAsStringAsync();
        }

        var (older, newer) = (await Svg(first), await Svg(second));

        Assert.DoesNotContain("Second room", older);
        Assert.Contains("Second room", newer);
    }

    [Fact]
    public async Task A_multi_floor_plan_needs_a_floor_choice_and_a_failed_job_has_no_download()
    {
        var (projectId, variantId, floorId) = await api.CreateProjectWithFloor();
        var upper = (await ApiClient.Body(await api.Post($"/api/v1/projects/{projectId}/floors", new { floorNumber = 2, elevationMm = 2700 }))).GetProperty("id").GetGuid();
        await api.DrawRoom(variantId, floorId, 4000, 3000);
        await api.DrawRoom(variantId, upper, 3000, 3000, "Loft", originX: 0);
        var version = await LatestVersionId(variantId);

        var job = await ApiClient.Body(await Export(version, "svg"));

        Assert.Equal("failed", job.GetProperty("status").GetString());
        Assert.Equal("EXPORT_FLOOR_REQUIRED", job.GetProperty("errorCode").GetString());
        var download = await api.Get($"/api/v1/exports/{job.GetProperty("id").GetGuid()}/download");
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);

        var ok = await ApiClient.Body(await Export(version, "svg", upper));
        Assert.Equal("completed", ok.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Exporting_with_a_floor_of_another_project_is_rejected()
    {
        var (_, variantId, floorId) = await api.CreateProjectWithFloor();
        await api.DrawRoom(variantId, floorId, 4000, 3000);

        var response = await Export(await LatestVersionId(variantId), "svg", Guid.NewGuid());

        Assert.Equal("FLOOR_NOT_FOUND", (await ApiClient.Body(response)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Another_user_cannot_read_or_download_the_export()
    {
        var (_, variantId, floorId) = await api.CreateProjectWithFloor();
        await api.DrawRoom(variantId, floorId, 4000, 3000);
        var version = await LatestVersionId(variantId);
        var job = await ApiClient.Body(await Export(version, "svg"));
        var id = job.GetProperty("id").GetGuid();

        factory.UserId = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/api/v1/exports/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/api/v1/exports/{id}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Export(version, "svg")).StatusCode);
    }
}
