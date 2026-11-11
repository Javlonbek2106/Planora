using System.Diagnostics.Metrics;
using BuildingDraft.Domain.Export;

namespace BuildingDraft.Application;

// Business metrics. Tag values come from fixed sets (known command names, formats, outcomes), never from
// user input, so the number of time series stays bounded.
public static class Telemetry
{
    public const string MeterName = "BuildingDraft";

    static readonly Meter Meter = new(MeterName);
    static readonly Counter<long> Commands = Meter.CreateCounter<long>("building_draft.geometry.commands", description: "Geometry commands by outcome (applied, rejected, conflict).");
    static readonly Counter<long> Exports = Meter.CreateCounter<long>("building_draft.exports", description: "Finished export jobs by format and status.");

    public static void Command(string commandType, string outcome) =>
        Commands.Add(1, new("command_type", Domain.Geometry.Commands.GeometryCommandCatalog.IsKnown(commandType) ? commandType : "unknown"), new("outcome", outcome));

    public static void Export(ExportFormat format, ExportStatus status) =>
        Exports.Add(1, new("format", format.ToString().ToLowerInvariant()), new("status", status.ToString().ToLowerInvariant()));
}
