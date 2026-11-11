using BuildingDraft.Domain;
using BuildingDraft.Domain.Audit;
using BuildingDraft.Domain.Abstractions;
using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Projects;

namespace BuildingDraft.Application;

public sealed class ProjectService(
    IProjectRepository projects,
    IGeometryVersionRepository versions,
    IUnitOfWork unitOfWork,
    AuditLog audit,
    ICurrentUser user,
    TimeProvider clock)
{
    const int MaxPageSize = 100;

    public async Task<ProjectDto> CreateAsync(CreateProjectRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var project = Project.Create(user.UserId, request.Name, request.Address, request.ConstructionState, request.Units, now);
        projects.Add(project);
        audit.Record(project.Id, AuditActions.ProjectCreated, "project", project.Id, new { project.Name });
        versions.Add(GeometryVersion.Create(project.Original.Id, 1, null, new GeometryModel().ComputeHash(), now), new GeometryModel());
        await unitOfWork.SaveChangesAsync(ct);
        return ProjectDto.From(project);
    }

    public async Task<ProjectDto> GetAsync(Guid projectId, CancellationToken ct) =>
        ProjectDto.From(await projects.GetAsync(projectId, user.UserId, ct));

    public async Task<PagedResult<ProjectDto>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var (items, total) = await projects.ListAsync(user.UserId, page, pageSize, ct);
        return new([..items.Select(ProjectDto.From)], page, pageSize, total);
    }

    public async Task<ProjectDto> UpdateAsync(Guid projectId, UpdateProjectRequest request, CancellationToken ct)
    {
        var project = await projects.GetAsync(projectId, user.UserId, ct);
        project.UpdateDetails(request.Name, request.Address, request.ConstructionState, request.Units, clock.GetUtcNow());
        audit.Record(project.Id, AuditActions.ProjectUpdated, "project", project.Id, new { project.Name });
        await unitOfWork.SaveChangesAsync(ct);
        return ProjectDto.From(project);
    }

    public async Task ArchiveAsync(Guid projectId, CancellationToken ct)
    {
        var project = await projects.GetAsync(projectId, user.UserId, ct);
        project.Archive(clock.GetUtcNow());
        audit.Record(project.Id, AuditActions.ProjectArchived, "project", project.Id);
        await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<FloorDto>> ListFloorsAsync(Guid projectId, CancellationToken ct) =>
        ProjectDto.From(await projects.GetAsync(projectId, user.UserId, ct)).Floors;

    public async Task<FloorDto> AddFloorAsync(Guid projectId, FloorRequest request, CancellationToken ct)
    {
        var project = await projects.GetAsync(projectId, user.UserId, ct);
        var floor = project.AddFloor(request.FloorNumber, request.ElevationMm, request.HeightMm, request.Name, clock.GetUtcNow());
        audit.Record(project.Id, AuditActions.FloorAdded, "floor", floor.Id, new { floor.FloorNumber });
        await unitOfWork.SaveChangesAsync(ct);
        return FloorDto.From(floor);
    }

    public async Task<FloorDto> UpdateFloorAsync(Guid floorId, FloorRequest request, CancellationToken ct)
    {
        var project = await projects.GetByFloorAsync(floorId, user.UserId, ct);
        var floor = project.UpdateFloor(floorId, request.FloorNumber, request.ElevationMm, request.HeightMm, request.Name, clock.GetUtcNow());
        audit.Record(project.Id, AuditActions.FloorUpdated, "floor", floor.Id, new { floor.FloorNumber });
        await unitOfWork.SaveChangesAsync(ct);
        return FloorDto.From(floor);
    }
}
