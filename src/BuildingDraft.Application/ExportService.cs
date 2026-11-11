using System.Text.Json;
using BuildingDraft.Domain;
using BuildingDraft.Domain.Audit;
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
    IExportQueue queue,
    IUnitOfWork unitOfWork,
    AuditLog audit,
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

        var job = ExportJob.Create(project.Id, version.Id, request.Format,
            JsonSerializer.Serialize(new ExportOptions(request.FloorId), GeometryJson.Options), clock.GetUtcNow());
        jobs.Add(job);
        audit.Record(project.Id, AuditActions.ExportRequested, "export", job.Id, new { request.Format, version.VersionNumber });
        await unitOfWork.SaveChangesAsync(ct);

        await queue.EnqueueAsync(job.Id, ct);
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
}
