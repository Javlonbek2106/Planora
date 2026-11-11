using BuildingDraft.Domain.Geometry;
using System.Globalization;
using System.Security;
using System.Text;

namespace BuildingDraft.Domain.Export;

// Vector plan in millimetres (viewBox units = mm), y flipped so the plan is not mirrored.
public sealed class SvgExporter : IGeometryExporter
{
    const double Margin = 600;

    public ExportFormat Format => ExportFormat.Svg;
    public string ContentType => "image/svg+xml";
    public string Extension => "svg";

    public byte[] Export(ExportContext context)
    {
        var plan = PlanDrawing.Build(context.Model, context.ResolvePlanFloor().Id);
        var width = plan.Max.X - plan.Min.X + 2 * Margin;
        var height = plan.Max.Y - plan.Min.Y + 2 * Margin;

        string P(Vec2 p) => $"{N(p.X - plan.Min.X + Margin)},{N(plan.Max.Y - p.Y + Margin)}";
        string Points(IEnumerable<Vec2> points) => string.Join(' ', points.Select(P));

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {N(width)} {N(height)}\" width=\"1000\" height=\"{N(1000 * height / width)}\">\n");
        sb.Append($"<title>{SecurityElement.Escape(context.ProjectName)} - version {context.VersionNumber} (units: mm)</title>\n");

        sb.Append("<g id=\"rooms\">\n");
        foreach (var room in plan.Rooms)
            sb.Append($"<polygon points=\"{Points(room.Outline)}\" fill=\"{(room.AreaType == RoomAreaType.Enclosed ? "#e8eef4" : "#eef6e8")}\"/>\n");
        sb.Append("</g>\n<g id=\"walls\">\n");
        foreach (var wall in plan.Walls)
            sb.Append($"<polygon points=\"{Points(wall.Outline)}\" fill=\"#3a3a3a\"/>\n");
        sb.Append("</g>\n<g id=\"openings\">\n");
        foreach (var opening in plan.Openings)
            sb.Append($"<polygon points=\"{Points(opening.Outline)}\" fill=\"#ffffff\" stroke=\"{(opening.Kind == OpeningKind.Door ? "#c0392b" : "#2980b9")}\" stroke-width=\"20\"/>\n");
        sb.Append("</g>\n<g id=\"labels\" font-family=\"sans-serif\" text-anchor=\"middle\" fill=\"#222\">\n");
        foreach (var room in plan.Rooms)
        {
            var at = room.Label;
            sb.Append($"<text x=\"{N(at.X - plan.Min.X + Margin)}\" y=\"{N(plan.Max.Y - at.Y + Margin)}\" font-size=\"220\">{SecurityElement.Escape(room.Name)}</text>\n");
            sb.Append($"<text x=\"{N(at.X - plan.Min.X + Margin)}\" y=\"{N(plan.Max.Y - at.Y + Margin + 260)}\" font-size=\"180\">{N(room.AreaM2, 2)} m2</text>\n");
        }
        sb.Append("</g>\n<g id=\"dimensions\" font-family=\"sans-serif\" text-anchor=\"middle\" font-size=\"150\" fill=\"#666\">\n");
        foreach (var wall in plan.Walls)
            sb.Append($"<text x=\"{N(wall.Label.X - plan.Min.X + Margin)}\" y=\"{N(plan.Max.Y - wall.Label.Y + Margin + 50)}\">{N(wall.LengthMm, 0)}</text>\n");
        sb.Append("</g>\n</svg>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    static string N(double value, int decimals = 1) => Math.Round(value, decimals).ToString(CultureInfo.InvariantCulture);
}
