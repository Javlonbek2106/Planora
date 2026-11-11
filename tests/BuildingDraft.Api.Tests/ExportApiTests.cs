using System.Net;
using System.Text;
using System.Text.Json;
using BuildingDraft.Application;
using BuildingDraft.Api.Infrastructure;
using BuildingDraft.Domain.Export;
using BuildingDraft.Domain.Abstractions;
using BuildingDraft.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

    // Exports are asynchronous: create the job, then poll its status until the worker has finished it.
    static async Task<JsonElement> WaitForJob(ApiClient client, Guid jobId)
    {
        for (var i = 0; i < 200; i++)
        {
            var job = await ApiClient.Body(await client.Get($"/api/v1/exports/{jobId}"));
            if (job.GetProperty("status").GetString() is not ("pending" or "processing"))
                return job;
            await Task.Delay(50);
        }
        throw new TimeoutException("The export job did not finish in time.");
    }

    async Task<JsonElement> ExportAndWait(Guid versionId, string format, Guid? floorId = null)
    {
        var created = await Export(versionId, format, floorId);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        return await WaitForJob(api, (await ApiClient.Body(created)).GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("svg", "image/svg+xml", ".svg", "<svg")]
    [InlineData("dxf", "application/dxf", ".dxf", "ENTITIES")]
    [InlineData("pdf", "application/pdf", ".pdf", "%PDF-1.4")]
    [InlineData("obj", "model/obj", ".obj", "g walls")]
    [InlineData("png", "image/png", ".png", "PNG")]
    public async Task Exports_complete_and_download_with_the_right_content_type(string format, string contentType, string extension, string marker)
    {
        var (_, variantId, floorId) = await api.CreateProjectWithFloor("Flat 12");
        await api.DrawRoom(variantId, floorId, 4000, 3000);

        var created = await Export(await LatestVersionId(variantId), format);

        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        Assert.Contains("/api/v1/exports/", created.Headers.Location!.ToString());
        var accepted = await ApiClient.Body(created);
        Assert.Contains(accepted.GetProperty("status").GetString(), new[] { "pending", "processing", "completed" });

        var id = accepted.GetProperty("id").GetGuid();
        var job = await WaitForJob(api, id);
        Assert.Equal("completed", job.GetProperty("status").GetString());

        var download = await api.Get($"/api/v1/exports/{id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(contentType, download.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(extension, download.Content.Headers.ContentDisposition!.FileNameStar ?? download.Content.Headers.ContentDisposition.FileName);
        Assert.Contains(marker, Encoding.Latin1.GetString(await download.Content.ReadAsByteArrayAsync()));
    }

    [Theory]
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
            var job = await ExportAndWait(versionId, "svg");
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

        var job = await ExportAndWait(version, "svg");

        Assert.Equal("failed", job.GetProperty("status").GetString());
        Assert.Equal("EXPORT_FLOOR_REQUIRED", job.GetProperty("errorCode").GetString());
        var download = await api.Get($"/api/v1/exports/{job.GetProperty("id").GetGuid()}/download");
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);

        var ok = await ExportAndWait(version, "svg", upper);
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
        var job = await ExportAndWait(version, "svg");
        var id = job.GetProperty("id").GetGuid();

        factory.UserId = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/api/v1/exports/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/api/v1/exports/{id}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Export(version, "svg")).StatusCode);
    }

    [Fact]
    public async Task A_job_left_unfinished_by_a_restart_is_picked_up_again()
    {
        var (projectId, variantId, floorId) = await api.CreateProjectWithFloor();
        await api.DrawRoom(variantId, floorId, 4000, 3000);
        var version = await LatestVersionId(variantId);

        // Simulate a server that stopped right after saving the job but before running it.
        Guid jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BuildingDraftDbContext>();
            var job = ExportJob.Create(projectId, version, ExportFormat.Svg, "{}", DateTimeOffset.UtcNow);
            db.ExportJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        await factory.Services.GetServices<IHostedService>().OfType<ExportWorker>().Single().RecoverAsync(CancellationToken.None);

        Assert.Equal("completed", (await WaitForJob(api, jobId)).GetProperty("status").GetString());
    }
}

// Registers an exporter that crashes, to prove that one bad job neither takes the worker down nor blocks the next one.
public class ExplodingExporterFactory : ApiFactory
{
    sealed class ExplodingExporter : IGeometryExporter
    {
        public ExportFormat Format => ExportFormat.Fbx;
        public string ContentType => "application/octet-stream";
        public string Extension => "fbx";
        public byte[] Export(ExportContext context) => throw new InvalidOperationException("boom");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddSingleton<IGeometryExporter, ExplodingExporter>());
    }
}

public class ExportWorkerResilienceTests : IClassFixture<ExplodingExporterFactory>
{
    readonly ApiClient api;

    public ExportWorkerResilienceTests(ExplodingExporterFactory factory)
    {
        factory.UserId = Guid.NewGuid();
        api = new ApiClient(factory.CreateClient());
    }

    [Fact]
    public async Task An_unexpected_exporter_crash_fails_only_that_job()
    {
        var (_, variantId, floorId) = await api.CreateProjectWithFloor();
        await api.DrawRoom(variantId, floorId, 4000, 3000);
        var versions = await ApiClient.Body(await api.Get($"/api/v1/variants/{variantId}/geometry/versions"));
        var version = versions[versions.GetArrayLength() - 1].GetProperty("id").GetGuid();

        var crashed = await ApiClient.Body(await api.Post($"/api/v1/geometry-versions/{version}/exports", new { format = "fbx" }));
        var crashedJob = await Wait(crashed.GetProperty("id").GetGuid());
        var fine = await ApiClient.Body(await api.Post($"/api/v1/geometry-versions/{version}/exports", new { format = "svg" }));
        var fineJob = await Wait(fine.GetProperty("id").GetGuid());

        Assert.Equal("failed", crashedJob.GetProperty("status").GetString());
        Assert.Equal("EXPORT_FAILED", crashedJob.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("boom", crashedJob.GetProperty("errorMessage").GetString());
        Assert.Equal("completed", fineJob.GetProperty("status").GetString());
    }

    async Task<JsonElement> Wait(Guid jobId)
    {
        for (var i = 0; i < 200; i++)
        {
            var job = await ApiClient.Body(await api.Get($"/api/v1/exports/{jobId}"));
            if (job.GetProperty("status").GetString() is not ("pending" or "processing"))
                return job;
            await Task.Delay(50);
        }
        throw new TimeoutException();
    }
}
