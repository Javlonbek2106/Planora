using BuildingDraft.Domain.Geometry;
using System.Globalization;
using System.Text;

namespace BuildingDraft.Domain.Export;

// AutoCAD R12 ASCII DXF: the oldest format that every CAD program opens. Editable entities, millimetres,
// origin = plan origin. R12 has no insertion-units variable, so the unit is stated in a comment and the layer set.
public sealed class DxfExporter : IGeometryExporter
{
    static readonly (string Name, int Color)[] Layers =
    [
        ("WALLS", 7), ("WALLS_CENTERLINE", 8), ("DOORS", 1), ("WINDOWS", 5),
        ("ROOM_BOUNDARIES", 3), ("DIMENSIONS", 2), ("LABELS", 7)
    ];

    public ExportFormat Format => ExportFormat.Dxf;
    public string ContentType => "application/dxf";
    public string Extension => "dxf";

    public byte[] Export(ExportContext context)
    {
        var plan = PlanDrawing.Build(context.Model, context.ResolvePlanFloor().Id);
        var dxf = new StringBuilder();

        void Pair(int code, object value) =>
            dxf.Append(code.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append('\n')
                .Append(value is double d ? d.ToString("0.###", CultureInfo.InvariantCulture) : value).Append('\n');

        Pair(999, $"Building Draft: {Text(context.ProjectName)}, version {context.VersionNumber}. UNITS=mm, ORIGIN=plan (0,0)");
        Pair(0, "SECTION"); Pair(2, "HEADER"); Pair(9, "$ACADVER"); Pair(1, "AC1009"); Pair(0, "ENDSEC");

        Pair(0, "SECTION"); Pair(2, "TABLES");
        Pair(0, "TABLE"); Pair(2, "LTYPE"); Pair(70, 1);
        Pair(0, "LTYPE"); Pair(2, "CONTINUOUS"); Pair(70, 0); Pair(3, "Solid line"); Pair(72, 65); Pair(73, 0); Pair(40, 0.0);
        Pair(0, "ENDTAB");
        Pair(0, "TABLE"); Pair(2, "LAYER"); Pair(70, Layers.Length);
        foreach (var (name, color) in Layers)
        {
            Pair(0, "LAYER"); Pair(2, name); Pair(70, 0); Pair(62, color); Pair(6, "CONTINUOUS");
        }
        Pair(0, "ENDTAB"); Pair(0, "ENDSEC");

        Pair(0, "SECTION"); Pair(2, "ENTITIES");

        void Polygon(string layer, IEnumerable<Vec2> points)
        {
            Pair(0, "POLYLINE"); Pair(8, layer); Pair(66, 1); Pair(70, 1);
            foreach (var p in points)
            {
                Pair(0, "VERTEX"); Pair(8, layer); Pair(10, p.X); Pair(20, p.Y); Pair(30, 0.0);
            }
            Pair(0, "SEQEND"); Pair(8, layer);
        }

        void Line(string layer, Vec2 a, Vec2 b)
        {
            Pair(0, "LINE"); Pair(8, layer); Pair(10, a.X); Pair(20, a.Y); Pair(30, 0.0); Pair(11, b.X); Pair(21, b.Y); Pair(31, 0.0);
        }

        void Label(string layer, Vec2 at, double height, string text)
        {
            Pair(0, "TEXT"); Pair(8, layer); Pair(10, at.X); Pair(20, at.Y); Pair(30, 0.0); Pair(40, height); Pair(1, Text(text));
        }

        foreach (var wall in plan.Walls)
        {
            Polygon("WALLS", wall.Outline);
            Line("WALLS_CENTERLINE", wall.Start, wall.End);
            Label("DIMENSIONS", wall.Label, 120, ((int)Math.Round(wall.LengthMm)).ToString(CultureInfo.InvariantCulture));
        }
        foreach (var opening in plan.Openings)
            Polygon(opening.Kind == OpeningKind.Door ? "DOORS" : "WINDOWS", opening.Outline);
        foreach (var room in plan.Rooms)
        {
            Polygon("ROOM_BOUNDARIES", room.Outline);
            Label("LABELS", room.Label, 200, $"{room.Name} {room.AreaM2.ToString("0.00", CultureInfo.InvariantCulture)} m2");
        }

        Pair(0, "ENDSEC"); Pair(0, "EOF");
        return Encoding.ASCII.GetBytes(dxf.ToString());
    }

    // R12 is ASCII only; other characters use the DXF \U+XXXX escape.
    static string Text(string value) =>
        string.Concat(value.Select(c => c < 128 ? c.ToString() : $"\\U+{(int)c:X4}"));
}
