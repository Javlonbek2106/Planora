using System.Text.Json;
using BuildingDraft.Domain;
using BuildingDraft.Domain.Abstractions;
using BuildingDraft.Domain.Export;
using BuildingDraft.Domain.Projects;

namespace BuildingDraft.Application;

public sealed class ExportService(
    IProjectRepository projects,
    IGeometryVersionRepository versions,
    IExportJobRepository jobs,
    IProjectFileRepository files,
    IFileStorage storage,
    IEnumerable<IGeometryExporter> exporters,
    IUnitOfWork unitOfWork,
    ICurrentUser user,
    TimeProvider clock)
{
    public async Task<ExportJobDto> CreateAsync(Guid versionId, CreateExportRequest request, CancellationToken ct)
    {
        var version = await versions.FindAsync(versionId, ct) ?? throw new DomainException("NOT_FOUND", "Version not found.");
        var project = await projects.GetByVariantAsync(version.VariantId, user.UserId, ct);

        var exporter = exporters.FirstOrDefault(e => e.Format == request.Format)
                       ?? throw new DomainException("EXPORT_FORMAT_NOT_SUPPORTED", $"Export to {request.Format} is not supported yet.");
        if (request.FloorId is { } floorId)
            project.EnsureFloorsExist([floorId]);

        var now = clock.GetUtcNow();
        var job = ExportJob.Create(project.Id, version.Id, request.Format, JsonSerializer.Serialize(new { floorId = request.FloorId }), now);
        jobs.Add(job);

        try
        {
            var model = await versions.LoadModelAsync(version.Id, ct);
            var content = exporter.Export(new(model, project.Floors, request.FloorId, project.Name, version.VersionNumber));
            var file = ProjectFile.Create(project.Id, version.Id, FileName(project, version, exporter), exporter.ContentType, content, now);
            await storage.SaveAsync(file.StorageKey, content, ct);
            files.Add(file);
            job.Complete(file.Id, clock.GetUtcNow());
        }
        catch (DomainException ex)
        {
            job.Fail(ex.Code, ex.Message, clock.GetUtcNow());
        }

        await unitOfWork.SaveChangesAsync(ct);
        return ExportJobDto.From(job);
    }

    public async Task<ExportJobDto> GetAsync(Guid jobId, CancellationToken ct) =>
        ExportJobDto.From(await jobs.GetAsync(jobId, user.UserId, ct));

    public async Task<ExportDownload> DownloadAsync(Guid jobId, CancellationToken ct)
    {
        var job = await jobs.GetAsync(jobId, user.UserId, ct);
        if (job.Status != ExportStatus.Completed || job.ResultFileId is not { } fileId)
            throw new DomainException("EXPORT_NOT_READY", $"The export is {job.Status}.");

        var file = await files.FindAsync(fileId, ct) ?? throw new DomainException("NOT_FOUND", "File not found.");
        var content = await storage.ReadAsync(file.StorageKey, ct) ?? throw new DomainException("NOT_FOUND", "File content not found.");
        return new(content, file.ContentType, file.FileName);
    }

    static string FileName(Project project, GeometryVersion version, IGeometryExporter exporter)
    {
        var name = new string(project.Name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
        return $"{name}_v{version.VersionNumber}.{exporter.Extension}";
    }
}
