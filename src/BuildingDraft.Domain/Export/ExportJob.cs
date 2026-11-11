using System.Security.Cryptography;

namespace BuildingDraft.Domain.Export;

public enum ExportStatus { Pending, Completed, Failed }

// File metadata; the bytes live in file storage under StorageKey.
public sealed class ProjectFile
{
    ProjectFile()
    {
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid? VersionId { get; private set; }
    public string FileName { get; private set; } = "";
    public string ContentType { get; private set; } = "";
    public string StorageKey { get; private set; } = "";
    public string Checksum { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static ProjectFile Create(Guid projectId, Guid? versionId, string fileName, string contentType, byte[] content, DateTimeOffset now)
    {
        var id = Guid.CreateVersion7();
        return new()
        {
            Id = id,
            ProjectId = projectId,
            VersionId = versionId,
            FileName = fileName,
            ContentType = contentType,
            StorageKey = $"{projectId:N}/{id:N}",
            Checksum = Convert.ToHexString(SHA256.HashData(content)),
            SizeBytes = content.LongLength,
            CreatedAt = now
        };
    }
}

// An export of one geometry version in one format. Jobs run to completion inside the request for now,
// but clients already use the job contract (create, poll status, download), so a background worker can replace that.
public sealed class ExportJob
{
    ExportJob()
    {
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid VersionId { get; private set; }
    public ExportFormat Format { get; private set; }
    public ExportStatus Status { get; private set; }
    public string Options { get; private set; } = "{}";
    public Guid? ResultFileId { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public static ExportJob Create(Guid projectId, Guid versionId, ExportFormat format, string options, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            VersionId = versionId,
            Format = format,
            Status = ExportStatus.Pending,
            Options = options,
            CreatedAt = now
        };

    public void Complete(Guid fileId, DateTimeOffset now) =>
        (Status, ResultFileId, CompletedAt) = (ExportStatus.Completed, fileId, now);

    public void Fail(string code, string message, DateTimeOffset now) =>
        (Status, ErrorCode, ErrorMessage, CompletedAt) = (ExportStatus.Failed, code, message, now);
}
