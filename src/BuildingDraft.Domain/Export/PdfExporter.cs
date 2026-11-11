using BuildingDraft.Domain.Geometry;
using System.Globalization;
using System.Text;

namespace BuildingDraft.Domain.Export;

// Single A4 landscape page with vector geometry. Uses the built-in Helvetica, so characters outside
// Latin-1 in room names are replaced with '?'.
public sealed class PdfExporter : IGeometryExporter
{
    const double PageWidth = 842, PageHeight = 595, PageMargin = 40;

    public ExportFormat Format => ExportFormat.Pdf;
    public string ContentType => "application/pdf";
    public string Extension => "pdf";

    public byte[] Export(ExportContext context)
    {
        var plan = PlanDrawing.Build(context.Model, context.ResolvePlanFloor().Id);
        var scale = Math.Min((PageWidth - 2 * PageMargin) / (plan.Max.X - plan.Min.X), (PageHeight - 2 * PageMargin - 20) / (plan.Max.Y - plan.Min.Y));
        // Centre the drawing in the printable area (below the title line).
        var offsetX = PageMargin + ((PageWidth - 2 * PageMargin) - (plan.Max.X - plan.Min.X) * scale) / 2;
        var offsetY = PageMargin + ((PageHeight - 2 * PageMargin - 20) - (plan.Max.Y - plan.Min.Y) * scale) / 2;
        string X(double x) => N((x - plan.Min.X) * scale + offsetX);
        string Y(double y) => N((y - plan.Min.Y) * scale + offsetY);

        var content = new StringBuilder();

        void Polygon(IEnumerable<Vec2> points, string paint)
        {
            var list = points.ToList();
            content.Append($"{X(list[0].X)} {Y(list[0].Y)} m ");
            foreach (var p in list.Skip(1))
                content.Append($"{X(p.X)} {Y(p.Y)} l ");
            content.Append($"h {paint}\n");
        }

        void Text(double x, double y, double size, string text) =>
            content.Append($"BT /F1 {N(size)} Tf {X(x)} {Y(y)} Td ({Escape(text)}) Tj ET\n");

        foreach (var room in plan.Rooms)
        {
            content.Append(room.AreaType == RoomAreaType.Enclosed ? "0.91 0.93 0.96 rg\n" : "0.93 0.96 0.91 rg\n");
            Polygon(room.Outline, "f");
        }
        content.Append("0.23 0.23 0.23 rg\n");
        foreach (var wall in plan.Walls)
            Polygon(wall.Outline, "f");
        content.Append("1 1 1 rg 0.8 w\n");
        foreach (var opening in plan.Openings)
        {
            content.Append(opening.Kind == OpeningKind.Door ? "0.75 0.22 0.17 RG\n" : "0.16 0.5 0.73 RG\n");
            Polygon(opening.Outline, "B");
        }

        content.Append("0.13 0.13 0.13 rg\n");
        foreach (var room in plan.Rooms)
        {
            Text(room.Label.X - 400, room.Label.Y, 9, room.Name);
            Text(room.Label.X - 400, room.Label.Y - 330, 8, $"{room.AreaM2.ToString("0.00", CultureInfo.InvariantCulture)} m2");
        }
        content.Append("0.4 0.4 0.4 rg\n");
        foreach (var wall in plan.Walls)
            Text(wall.Label.X - 150, wall.Label.Y - 40, 6, ((int)Math.Round(wall.LengthMm)).ToString(CultureInfo.InvariantCulture));
        content.Append("0 g\n");
        content.Append($"BT /F1 8 Tf {N(PageMargin)} {N(PageHeight - 25)} Td ({Escape($"{context.ProjectName} - version {context.VersionNumber} - dimensions in mm")}) Tj ET\n");

        return Assemble(content.ToString());
    }

    static byte[] Assemble(string content)
    {
        var latin1 = Encoding.Latin1;
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {latin1.GetByteCount(content)} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
        };

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return latin1.GetBytes(pdf.ToString());
    }

    static string Escape(string text) =>
        string.Concat(text.Select(c => c switch { '\\' => "\\\\", '(' => "\\(", ')' => "\\)", > 'ÿ' => "?", _ => c.ToString() }));

    static string N(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
}
