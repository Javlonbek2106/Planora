using System.Text.Json;
using BuildingDraft.Domain;
using BuildingDraft.Domain.Abstractions;
using BuildingDraft.Domain.Audit;
using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Geometry.Commands;
using BuildingDraft.Domain.Measurements;
using BuildingDraft.Domain.Projects;

namespace BuildingDraft.Application;

// A measurement taken offline travels inside the apply_measurement command as extra fields (measurementId, source,
// notes, verified, capturedAt). When such a command is applied, the measurement record is created in the same unit of
// work as the new geometry version, so the history (who/what source, verified or not) and the geometry cannot drift
// apart. Validation happens before anything is staged: a refused measurement leaves no half-written version behind.
public sealed class MeasurementRecorder(IMeasurementRepository measurements, AuditLog audit, TimeProvider clock)
{
    // Measurements staged earlier in this same unit of work (one push can carry several). They are not in the
    // database yet, so a query cannot see them: without this a second measurement of the wall would not supersede
    // the first, and the same id twice in one push would collide on save.
    readonly List<Measurement> stagedInThisUnit = [];

    public async Task<PreparedMeasurement?> PrepareAsync(Project project, DesignVariant variant, string commandType, string payloadJson, CancellationToken ct)
    {
        if (commandType != "apply_measurement")
            return null;

        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("measurementId", out var idElement) || !Guid.TryParse(idElement.GetString(), out var id))
            return null;

        // A retry with a new operation id must not record the same measurement twice.
        if (stagedInThisUnit.Any(m => m.Id == id) || await measurements.FindAsync(id, project.OwnerId, ct) is not null)
            return null;

        var wallId = Guid.Parse(root.GetProperty("wallId").GetString()!);
        var reference = root.TryGetProperty("reference", out var r) && r.GetString() == "centerline" ? MeasurementReference.Centerline : MeasurementReference.Inner;
        var source = ParseSource(root);
        var measurement = Measurement.Record(project.Id, variant.Id, wallId, root.GetProperty("valueMm").GetDouble(), reference, source,
            confidence: null, Text(root, "deviceName"), Text(root, "notes"),
            root.TryGetProperty("capturedAt", out var c) && c.TryGetDateTimeOffset(out var at) ? at : clock.GetUtcNow(), id);

        measurement.EnsureApplicable();
        if (root.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True)
            measurement.ChangeStatus(MeasurementStatus.Verified);

        var earlier = (await measurements.ListAsync(project.Id, variant.Id, wallId, ct))
            .Concat(stagedInThisUnit.Where(m => m.VariantId == variant.Id && m.TargetObjectId == wallId))
            .DistinctBy(m => m.Id)
            .Where(m => m.IsApplied && m.Status != MeasurementStatus.Rejected).ToList();
        return new(measurement, earlier, project.Id);
    }

    // Called once the version exists: the measurement is applied at that version, older applied ones are superseded.
    public void Commit(PreparedMeasurement prepared, GeometryVersion version)
    {
        foreach (var old in prepared.Earlier)
            old.Supersede();
        prepared.Measurement.MarkApplied(version.VersionNumber);
        measurements.Add(prepared.Measurement);
        stagedInThisUnit.Add(prepared.Measurement);
        audit.Record(prepared.ProjectId, AuditActions.MeasurementApplied, "measurement", prepared.Measurement.Id,
            new { prepared.Measurement.ValueMm, prepared.Measurement.Source });
    }

    static MeasurementSource ParseSource(JsonElement root)
    {
        var text = Text(root, "source") ?? "manual";
        try
        {
            return JsonSerializer.Deserialize<MeasurementSource>($"\"{text}\"", GeometryCommandCatalog.Json);
        }
        catch (JsonException)
        {
            throw new DomainException("INVALID_MEASUREMENT_SOURCE", $"Unknown measurement source '{text}'.");
        }
    }

    static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed record PreparedMeasurement(Measurement Measurement, IReadOnlyList<Measurement> Earlier, Guid ProjectId);
