using System.Text.Json;
using BuildingDraft.Domain;
using BuildingDraft.Domain.Abstractions;
using BuildingDraft.Domain.Export;
using BuildingDraft.Domain.Projects;
using Microsoft.Extensions.Logging;

namespace BuildingDraft.Application;

public interface IExportJobRunner
{
    Task<IReadOnlyList<Guid>> UnfinishedJobIdsAsync(CancellationToken ct);
    Task RunAsync(Guid jobId, CancellationToken ct);
}

// Executes one export job outside any request, so it has no current user: the job already carries its project.
public sealed class ExportRunner(
    IExportJobRepository jobs,
    IProjectRepository projects,
    IGeometryVersionRepository versions,
    IProjectFileRepository files,
    IFileStorage storage,
    IEnumerable<IGeometryExporter> exporters,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<ExportRunner> logger) : IExportJobRunner
{
    public Task<IReadOnlyList<Guid>> UnfinishedJobIdsAsync(CancellationToken ct) => jobs.ListUnfinishedIdsAsync(ct);

    public async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        var job = await jobs.FindByIdAsync(jobId, ct);
        if (job is null || job.Status is ExportStatus.Completed or ExportStatus.Failed)
            return;

        job.Start();
        await unitOfWork.SaveChangesAsync(ct);

        try
        {
            await ExportAsync(job, ct);
        }
        catch (DomainException ex)
        {
            job.Fail(ex.Code, ex.Message, clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Export job {JobId} failed unexpectedly", jobId);
            job.Fail("EXPORT_FAILED", "The export failed unexpectedly.", clock.GetUtcNow());
        }

        await unitOfWork.SaveChangesAsync(ct);
        Telemetry.Export(job.Format, job.Status);
    }

    async Task ExportAsync(ExportJob job, CancellationToken ct)
    {
        var project = await projects.FindByIdAsync(job.ProjectId, ct) ?? throw new DomainException("NOT_FOUND", "Project not found.");
        var version = await versions.FindAsync(job.VersionId, ct) ?? throw new DomainException("NOT_FOUND", "Version not found.");
        var exporter = exporters.FirstOrDefault(e => e.Format == job.Format)
                       ?? throw new DomainException("EXPORT_FORMAT_NOT_SUPPORTED", $"Export to {job.Format} is not supported yet.");
        var floorId = JsonSerializer.Deserialize<ExportOptions>(job.Options, GeometryJson.Options)?.FloorId;

        var model = await versions.LoadModelAsync(version.Id, ct);
        var content = exporter.Export(new(model, project.Floors, floorId, project.Name, version.VersionNumber));
        var file = ProjectFile.Create(project.Id, version.Id, ExportFileName.For(project, version, exporter), exporter.ContentType, content, clock.GetUtcNow());
        await storage.SaveAsync(file.StorageKey, content, ct);
        files.Add(file);
        job.Complete(file.Id, clock.GetUtcNow());
    }
}

public sealed record ExportOptions(Guid? FloorId);

static class GeometryJson
{
    public static System.Text.Json.JsonSerializerOptions Options => BuildingDraft.Domain.Geometry.Commands.GeometryCommandCatalog.Json;
}

static class ExportFileName
{
    public static string For(Project project, GeometryVersion version, IGeometryExporter exporter)
    {
        var name = new string(project.Name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
        return $"{name}_v{version.VersionNumber}.{exporter.Extension}";
    }
}
