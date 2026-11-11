using System.Text.Json;
using BuildingDraft.Domain.Abstractions;
using BuildingDraft.Domain.Audit;
using BuildingDraft.Domain.Geometry.Commands;

namespace BuildingDraft.Application;

// Records an audit entry in the current unit of work, so it is saved together with the change it describes
// and never exists for a change that was rejected.
public sealed class AuditLog(IAuditEntryRepository entries, ICurrentUser user, TimeProvider clock)
{
    public void Record(Guid projectId, string action, string entityType, Guid entityId, object? details = null) =>
        entries.Add(AuditEntry.Create(projectId, user.UserId, action, entityType, entityId,
            details is null ? null : JsonSerializer.Serialize(details, GeometryCommandCatalog.Json), clock.GetUtcNow()));
}

public sealed record AuditEntryDto(Guid Id, Guid UserId, string Action, string EntityType, Guid EntityId, JsonElement? Details, DateTimeOffset CreatedAt)
{
    public static AuditEntryDto From(AuditEntry e) => new(e.Id, e.UserId, e.Action, e.EntityType, e.EntityId,
        e.Details is null ? null : JsonDocument.Parse(e.Details).RootElement.Clone(), e.CreatedAt);
}

public sealed class AuditService(IProjectRepository projects, IAuditEntryRepository entries, ICurrentUser user)
{
    const int MaxPageSize = 100;

    public async Task<PagedResult<AuditEntryDto>> ListAsync(Guid projectId, int page, int pageSize, CancellationToken ct)
    {
        await projects.GetAsync(projectId, user.UserId, ct);
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var (items, total) = await entries.ListAsync(projectId, page, pageSize, ct);
        return new([..items.Select(AuditEntryDto.From)], page, pageSize, total);
    }
}
