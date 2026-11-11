using BuildingDraft.Domain;
using BuildingDraft.Domain.Audit;
using BuildingDraft.Domain.Abstractions;
using BuildingDraft.Domain.Projects;

namespace BuildingDraft.Application;

public sealed class VariantService(
    IProjectRepository projects,
    IGeometryVersionRepository versions,
    IUnitOfWork unitOfWork,
    AuditLog audit,
    ICurrentUser user,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<VariantDto>> ListAsync(Guid projectId, CancellationToken ct) =>
        ProjectDto.From(await projects.GetAsync(projectId, user.UserId, ct)).Variants;

    // Without a source, the variant is copied from the Original Layout.
    public async Task<VariantDto> CreateAsync(Guid projectId, CreateVariantRequest request, CancellationToken ct)
    {
        var project = await projects.GetAsync(projectId, user.UserId, ct);
        return await CopyAsync(project, request.SourceVariantId ?? project.Original.Id, request.Name, ct);
    }

    public async Task<VariantDto> DuplicateAsync(Guid variantId, NameRequest request, CancellationToken ct)
    {
        var project = await projects.GetByVariantAsync(variantId, user.UserId, ct);
        return await CopyAsync(project, variantId, request.Name, ct);
    }

    public async Task<VariantDto> RenameAsync(Guid variantId, NameRequest request, CancellationToken ct)
    {
        var project = await projects.GetByVariantAsync(variantId, user.UserId, ct);
        var variant = project.GetVariant(variantId);
        variant.Rename(request.Name);
        audit.Record(project.Id, AuditActions.VariantRenamed, "variant", variant.Id, new { variant.Name });
        await unitOfWork.SaveChangesAsync(ct);
        return VariantDto.From(variant);
    }

    public async Task<VariantDto> ApproveAsync(Guid variantId, CancellationToken ct)
    {
        var project = await projects.GetByVariantAsync(variantId, user.UserId, ct);
        var variant = project.GetVariant(variantId);
        variant.Approve();
        audit.Record(project.Id, AuditActions.VariantApproved, "variant", variant.Id, new { variant.Type });
        await unitOfWork.SaveChangesAsync(ct);
        return VariantDto.From(variant);
    }

    public async Task DeleteAsync(Guid variantId, CancellationToken ct)
    {
        var project = await projects.GetByVariantAsync(variantId, user.UserId, ct);
        project.DeleteVariant(variantId, clock.GetUtcNow());
        audit.Record(project.Id, AuditActions.VariantDeleted, "variant", variantId);
        await unitOfWork.SaveChangesAsync(ct);
    }

    async Task<VariantDto> CopyAsync(Project project, Guid sourceVariantId, string name, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var variant = project.CreateVariant(sourceVariantId, name, now);
        var source = project.GetVariant(sourceVariantId);

        var sourceVersion = await versions.FindByNumberAsync(source.Id, source.CurrentVersionNumber, ct)
                            ?? throw new DomainException("NOT_FOUND", "Source geometry not found.");
        var model = await versions.LoadModelAsync(sourceVersion.Id, ct);
        versions.Add(GeometryVersion.Create(variant.Id, 1, null, model.ComputeHash(), now), model);
        audit.Record(project.Id, AuditActions.VariantCreated, "variant", variant.Id, new { sourceVariantId, variant.Name });

        await unitOfWork.SaveChangesAsync(ct);
        return VariantDto.From(variant);
    }
}
