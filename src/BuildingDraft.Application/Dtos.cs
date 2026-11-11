using System.Text.Json;
using BuildingDraft.Domain.Export;
using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Measurements;
using BuildingDraft.Domain.Projects;

namespace BuildingDraft.Application;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record CreateProjectRequest(string Name, string? Address = null, string? ConstructionState = null, LengthUnit Units = LengthUnit.M);

public sealed record UpdateProjectRequest(string Name, string? Address = null, string? ConstructionState = null, LengthUnit Units = LengthUnit.M);

public sealed record FloorRequest(int FloorNumber, double ElevationMm = 0, double HeightMm = 2700, string? Name = null);

public sealed record CreateVariantRequest(string Name, Guid? SourceVariantId = null);

public sealed record NameRequest(string Name);

public sealed record ExecuteCommandRequest(Guid ClientOperationId, int ExpectedVersion, string CommandType, JsonElement Payload, string? DeviceId = null);

public sealed record RestoreVersionRequest(Guid ClientOperationId, int ExpectedVersion);

public sealed record FloorDto(Guid Id, int FloorNumber, double ElevationMm, double HeightMm, string? Name)
{
    public static FloorDto From(Floor f) => new(f.Id, f.FloorNumber, f.ElevationMm, f.HeightMm, f.Name);
}

public sealed record VariantDto(Guid Id, string Name, VariantType Type, Guid? SourceVariantId, bool IsLocked, int CurrentVersion, DateTimeOffset CreatedAt)
{
    public static VariantDto From(DesignVariant v) => new(v.Id, v.Name, v.Type, v.SourceVariantId, v.IsLocked, v.CurrentVersionNumber, v.CreatedAt);
}

public sealed record ProjectDto(
    Guid Id,
    string Name,
    string? Address,
    string? ConstructionState,
    ProjectStatus Status,
    LengthUnit Units,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<FloorDto> Floors,
    IReadOnlyList<VariantDto> Variants)
{
    public static ProjectDto From(Project p) => new(
        p.Id, p.Name, p.Address, p.ConstructionState, p.Status, p.Units, p.CreatedAt, p.UpdatedAt,
        [..p.Floors.OrderBy(f => f.FloorNumber).Select(FloorDto.From)],
        [..p.ActiveVariants.Select(VariantDto.From)]);
}

public sealed record VersionDto(Guid Id, int VersionNumber, Guid? ParentVersionId, string Hash, DateTimeOffset CreatedAt)
{
    public static VersionDto From(GeometryVersion v) => new(v.Id, v.VersionNumber, v.ParentVersionId, v.Hash, v.CreatedAt);
}

public sealed record ValidationDto(bool IsValid, IReadOnlyList<ValidationIssue> Warnings, IReadOnlyList<ValidationIssue> Errors);

public sealed record CommandResultDto(bool Success, int Version, Guid OperationId, ValidationDto Validation);

public sealed record RoomDto(Room Room, double? InnerAreaM2, double? InnerPerimeterMm, double? CenterlineAreaM2);

// Enclosed rooms and open areas (balcony, loggia, terrace) are totalled separately.
public sealed record AreaSummary(double EnclosedAreaM2, double OpenAreaM2);

public sealed record GeometryDto(
    int Version,
    string Hash,
    IReadOnlyList<Node> Nodes,
    IReadOnlyList<Wall> Walls,
    IReadOnlyList<RoomDto> Rooms,
    IReadOnlyList<Opening> Openings,
    IReadOnlyList<GeometryConstraint> Constraints,
    AreaSummary Summary)
{
    public static GeometryDto From(GeometryModel model, int version, string hash)
    {
        var rooms = model.Rooms.Values.OrderBy(r => r.Id).Select(r =>
        {
            var m = RoomGeometry.Metrics(model, r);
            return new RoomDto(r, m is null ? null : m.InnerAreaSqMm / 1e6, m?.InnerPerimeterMm, m is null ? null : m.CenterlineAreaSqMm / 1e6);
        }).ToList();

        double Total(RoomAreaType type) => Math.Round(rooms.Where(r => r.Room.AreaType == type).Sum(r => r.InnerAreaM2 ?? 0), 4);

        return new(version, hash,
            [..model.Nodes.Values.OrderBy(n => n.Id)],
            [..model.Walls.Values.OrderBy(w => w.Id)],
            rooms,
            [..model.Openings.Values.OrderBy(o => o.Id)],
            [..model.Constraints],
            new(Total(RoomAreaType.Enclosed), Total(RoomAreaType.OpenArea)));
    }
}

public sealed record CreateMeasurementRequest(
    Guid VariantId,
    Guid TargetObjectId,
    double ValueMm,
    MeasurementSource Source,
    MeasurementReference Reference = MeasurementReference.Inner,
    double? Confidence = null,
    string? DeviceName = null,
    string? Notes = null,
    DateTimeOffset? CapturedAt = null);

public sealed record ChangeMeasurementStatusRequest(MeasurementStatus Status);

public sealed record ApplyMeasurementRequest(Guid ClientOperationId, int ExpectedVersion, string? DeviceId = null);

public sealed record MeasurementDto(
    Guid Id,
    Guid ProjectId,
    Guid VariantId,
    Guid TargetObjectId,
    string TargetProperty,
    double ValueMm,
    MeasurementReference Reference,
    MeasurementSource Source,
    MeasurementStatus Status,
    bool IsEstimate,
    double? Confidence,
    string? DeviceName,
    string? Notes,
    DateTimeOffset CapturedAt,
    bool IsApplied,
    int? AppliedVersionNumber)
{
    public static MeasurementDto From(Measurement m) => new(
        m.Id, m.ProjectId, m.VariantId, m.TargetObjectId, m.TargetProperty, m.ValueMm, m.Reference, m.Source, m.Status,
        m.IsEstimate, m.Confidence, m.DeviceName, m.Notes, m.CapturedAt, m.IsApplied, m.AppliedVersionNumber);
}

public sealed record CreateExportRequest(ExportFormat Format, Guid? FloorId = null);

public sealed record ExportJobDto(
    Guid Id,
    Guid VersionId,
    ExportFormat Format,
    ExportStatus Status,
    Guid? ResultFileId,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt)
{
    public static ExportJobDto From(ExportJob j) =>
        new(j.Id, j.VersionId, j.Format, j.Status, j.ResultFileId, j.ErrorCode, j.ErrorMessage, j.CreatedAt, j.CompletedAt);
}

public sealed record ExportDownload(byte[] Content, string ContentType, string FileName);
