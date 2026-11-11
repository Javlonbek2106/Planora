using System.Text.Json;
using BuildingDraft.Domain;
using BuildingDraft.Domain.Abstractions;
using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Geometry.Commands;
using BuildingDraft.Domain.Geometry.Pipeline;
using BuildingDraft.Domain.Measurements;
using BuildingDraft.Domain.Scans;

namespace BuildingDraft.Application;

public sealed class ScanService(
    IProjectRepository projects,
    IScanSessionRepository scans,
    IMeasurementRepository measurements,
    IGeometryVersionRepository versions,
    GeometryService geometry,
    GeometryProcessor processor,
    IUnitOfWork unitOfWork,
    ICurrentUser user,
    TimeProvider clock)
{
    public async Task<ScanSessionDto> CreateAsync(Guid projectId, CreateScanSessionRequest request, CancellationToken ct)
    {
        var project = await projects.GetAsync(projectId, user.UserId, ct);
        project.GetVariant(request.VariantId);
        project.EnsureFloorsExist([request.FloorId]);

        var session = ScanSession.Create(project.Id, request.VariantId, request.FloorId, request.Source, clock.GetUtcNow());
        scans.Add(session);
        await unitOfWork.SaveChangesAsync(ct);
        return ScanSessionDto.From(session);
    }

    public async Task<ScanSessionDto> ProcessAsync(Guid sessionId, ProcessScanRequest request, CancellationToken ct)
    {
        var session = await scans.GetAsync(sessionId, user.UserId, ct);
        var project = await projects.GetAsync(session.ProjectId, user.UserId, ct);
        var capture = request.Capture ?? throw new DomainException("INVALID_SCAN", "A capture is required.");
        if (capture.Confidence is null)
            throw new DomainException("INVALID_CONFIDENCE", "AR/LiDAR and AI scans require a confidence value.");

        var floor = project.Floors.First(f => f.Id == session.FloorId);
        var proposal = ScanNormalizer.Normalize(capture, session.FloorId, floor.HeightMm);
        session.Process(JsonSerializer.Serialize(proposal, GeometryCommandCatalog.Json), capture.Confidence.Value, capture.DeviceModel);
        await unitOfWork.SaveChangesAsync(ct);
        return ScanSessionDto.From(session);
    }

    public async Task<ScanResultDto> GetResultAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await scans.GetAsync(sessionId, user.UserId, ct);
        var proposal = ReadProposal(session);

        // Dry run against the variant's current geometry, so the user sees up front whether applying would work.
        var project = await projects.GetAsync(session.ProjectId, user.UserId, ct);
        var variant = project.GetVariant(session.VariantId);
        var current = await versions.FindByNumberAsync(variant.Id, variant.CurrentVersionNumber, ct)
                      ?? throw new DomainException("NOT_FOUND", "Current geometry version not found.");
        var outcome = processor.Execute(await versions.LoadModelAsync(current.Id, ct), proposal.Command);

        var errors = outcome.Failure is null ? []
            : outcome.Failure.Errors ?? [new ValidationIssue(outcome.Failure.Code, outcome.Failure.Message)];
        return new(ScanSessionDto.From(session), true, proposal.Command.Walls.Count, proposal.Command.Openings.Count,
            proposal.Issues, proposal.Command, new(outcome.Success, outcome.Warnings, errors));
    }

    // Bookkeeping (session state and estimate measurements) shares one unit of work with the geometry change:
    // geometry.ExecuteAsync saves everything together, and a rejected import saves nothing.
    public async Task<CommandResultDto> ApplyAsync(Guid sessionId, ApplyScanRequest request, CancellationToken ct)
    {
        var session = await scans.GetAsync(sessionId, user.UserId, ct);
        session.EnsureApplicable();
        var proposal = ReadProposal(session);

        var now = clock.GetUtcNow();
        var nodes = proposal.Command.Nodes.ToDictionary(n => n.Id);
        foreach (var wall in proposal.Command.Walls)
        {
            var length = Math.Sqrt(Math.Pow(nodes[wall.EndNodeId].XMm - nodes[wall.StartNodeId].XMm, 2)
                                   + Math.Pow(nodes[wall.EndNodeId].YMm - nodes[wall.StartNodeId].YMm, 2));
            measurements.Add(Measurement.Record(session.ProjectId, session.VariantId, wall.Id, length, MeasurementReference.Centerline,
                session.Source, session.Confidence, session.DeviceModel, $"scan {session.Id}", now));
        }

        session.MarkApplied(request.ExpectedVersion + 1);
        var payload = JsonSerializer.SerializeToElement(proposal.Command, GeometryCommandCatalog.Json);
        return await geometry.ExecuteAsync(session.VariantId,
            new(request.ClientOperationId, request.ExpectedVersion, "import_scan", payload, request.DeviceId), ct);
    }

    static ScanProposal ReadProposal(ScanSession session) =>
        session.Proposal is null
            ? throw new DomainException("SCAN_NOT_PROCESSED", "Process the scan first.")
            : JsonSerializer.Deserialize<ScanProposal>(session.Proposal, GeometryCommandCatalog.Json)!;
}
