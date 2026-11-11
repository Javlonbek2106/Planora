using System.Globalization;
using System.Text;
using System.Xml.Linq;
using BuildingDraft.Domain.Export;
using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Geometry.Commands;
using BuildingDraft.Domain.Projects;

namespace BuildingDraft.Domain.Tests;

public class ExportTests
{
    static readonly Project Project = Project.Create(Guid.NewGuid(), "Flat 12", null, null, LengthUnit.M, DateTimeOffset.UtcNow);
    static readonly Floor GroundFloor = Project.AddFloor(1, 0, 2700, "Ground", DateTimeOffset.UtcNow);

    static RoomFixture Room() => new(floorId: GroundFloor.Id);

    static ExportContext Context(RoomFixture f, Guid? floorId = null) =>
        new(f.Model, Project.Floors, floorId, "Flat 12", 7);

    static RoomFixture RoomWithDoor()
    {
        var f = Room();
        f.Run(new AttachOpening(Guid.NewGuid(), f.Walls[0], OpeningKind.Door, 500, 900, 2100));
        f.Run(new AttachOpening(Guid.NewGuid(), f.Walls[2], OpeningKind.Window, 1000, 1200, 1200, 900));
        return f;
    }

    [Fact]
    public void Svg_is_well_formed_and_draws_walls_openings_and_area()
    {
        var svg = Encoding.UTF8.GetString(new SvgExporter().Export(Context(RoomWithDoor())));

        var doc = XDocument.Parse(svg);
        XNamespace ns = "http://www.w3.org/2000/svg";
        Assert.Equal(4, doc.Descendants(ns + "g").Single(g => (string?)g.Attribute("id") == "walls").Elements(ns + "polygon").Count());
        Assert.Equal(2, doc.Descendants(ns + "g").Single(g => (string?)g.Attribute("id") == "openings").Elements(ns + "polygon").Count());
        Assert.Contains("10.64 m2", svg);
        Assert.Contains("Living room", svg);
    }

    [Fact]
    public void Dxf_has_editable_entities_on_the_expected_layers()
    {
        var dxf = Encoding.ASCII.GetString(new DxfExporter().Export(Context(RoomWithDoor())));
        var lines = dxf.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

        Assert.Equal(0, lines.Length % 2);
        Assert.Equal("EOF", lines[^1]);
        Assert.Equal("AC1009", lines[lines.ToList().IndexOf("$ACADVER") + 2]);
        var entities = lines.Chunk(2).Where(p => p[0] == "0" && p[1] is "POLYLINE" or "LINE" or "TEXT").Select(p => p[1]).ToList();
        Assert.Equal(4 + 1 + 1 + 1, entities.Count(e => e == "POLYLINE"));
        Assert.Equal(4, entities.Count(e => e == "LINE"));
        foreach (var layer in new[] { "WALLS", "DOORS", "WINDOWS", "ROOM_BOUNDARIES", "DIMENSIONS", "LABELS", "WALLS_CENTERLINE" })
            Assert.Contains(layer, lines);
        Assert.Contains("UNITS=mm", dxf);
    }

    [Fact]
    public void Dxf_escapes_non_ascii_names()
    {
        var f = Room();
        f.Run(new DeleteRoom(f.RoomId));
        f.Run(new CreateRoom(Guid.NewGuid(), f.FloorId, "Yotoqxona o‘g‘il", f.Walls));

        var dxf = Encoding.ASCII.GetString(new DxfExporter().Export(Context(f)));

        Assert.Contains("\\U+2018", dxf);
        Assert.All(dxf, c => Assert.True(c < 128));
    }

    [Fact]
    public void Pdf_has_a_valid_cross_reference_table()
    {
        var bytes = new PdfExporter().Export(Context(RoomWithDoor()));
        var pdf = Encoding.Latin1.GetString(bytes);

        Assert.StartsWith("%PDF-1.4", pdf);
        Assert.EndsWith("%%EOF\n", pdf);
        var xrefOffset = int.Parse(pdf[(pdf.LastIndexOf("startxref", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        Assert.StartsWith("xref", pdf[xrefOffset..]);
        var entries = pdf[xrefOffset..].Split('\n').Skip(3).Take(5).Select(l => int.Parse(l[..10])).ToList();
        for (var i = 0; i < entries.Count; i++)
            Assert.StartsWith($"{i + 1} 0 obj", pdf[entries[i]..]);
        Assert.Contains("Living room", pdf);
    }

    static (List<double[]> Vertices, Dictionary<string, List<int[]>> Groups) ParseObj(byte[] bytes)
    {
        var vertices = new List<double[]>();
        var groups = new Dictionary<string, List<int[]>>();
        var group = "";
        foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;
            if (parts[0] == "v")
                vertices.Add(parts[1..].Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray());
            else if (parts[0] == "g")
                groups[group = parts[1]] = [];
            else if (parts[0] == "f")
                groups[group].Add(parts[1..].Select(int.Parse).ToArray());
        }
        return (vertices, groups);
    }

    [Fact]
    public void Obj_builds_one_box_per_wall_and_two_triangles_per_slab()
    {
        var (vertices, groups) = ParseObj(new ObjExporter().Export(Context(Room())));

        Assert.Equal(4 * 8 + 4 + 4, vertices.Count);
        Assert.Equal(4 * 6, groups["walls"].Count);
        Assert.Equal(2, groups["floors"].Count);
        Assert.Equal(2, groups["ceilings"].Count);
        Assert.Equal(2.7, vertices.Max(v => v[1]), 3);
        Assert.Equal(0, vertices.Min(v => v[1]), 3);
        Assert.All(groups.Values.SelectMany(g => g), face => Assert.All(face, i => Assert.InRange(i, 1, vertices.Count)));
    }

    [Fact]
    public void Obj_splits_walls_around_openings()
    {
        var f = Room();
        f.Run(new AttachOpening(Guid.NewGuid(), f.Walls[0], OpeningKind.Door, 500, 900, 2100));
        f.Run(new AttachOpening(Guid.NewGuid(), f.Walls[2], OpeningKind.Window, 1000, 1200, 1200, 900));

        var (_, groups) = ParseObj(new ObjExporter().Export(Context(f)));

        // Door wall: left, above door, right = 3 boxes. Window wall: left, below, above, right = 4 boxes. Two plain walls = 2.
        Assert.Equal((3 + 4 + 2) * 6, groups["walls"].Count);
    }

    [Fact]
    public void Obj_floor_triangles_cover_a_non_convex_room_exactly()
    {
        double[][] corners = [[0, 0], [4000, 0], [4000, 2000], [2000, 2000], [2000, 4000], [0, 4000]];
        var model = new GeometryModel();
        var nodes = corners.Select(_ => Guid.NewGuid()).ToArray();
        var walls = corners.Select(_ => Guid.NewGuid()).ToArray();
        var f = new RoomFixtureL(GroundFloor.Id, corners, nodes, walls);

        var (vertices, groups) = ParseObj(new ObjExporter().Export(Context(f.Fixture)));

        var floorFaces = groups["floors"];
        Assert.Equal(4, floorFaces.Count);
        double Area(int[] t)
        {
            var (a, b, c) = (vertices[t[0] - 1], vertices[t[1] - 1], vertices[t[2] - 1]);
            return ((b[0] - a[0]) * (c[2] - a[2]) - (c[0] - a[0]) * (b[2] - a[2])) / 2;
        }
        var expected = RoomGeometry.Metrics(f.Fixture.Model, f.Fixture.Model.Rooms.Values.Single())!.InnerAreaSqMm / 1e6;
        Assert.Equal(expected, floorFaces.Sum(t => Math.Abs(Area(t))), 3);
        Assert.All(floorFaces, t => Assert.True(Area(t) > 0 == Area(floorFaces[0]) > 0));
    }

    [Fact]
    public void Connected_wall_ends_are_extended_to_fill_corners_but_free_ends_are_not()
    {
        var f = Room();
        var floorId = f.FloorId;
        f.Run(new CreateWall(Guid.NewGuid(), floorId, Guid.NewGuid(), 6000, 0, Guid.NewGuid(), 8000, 0));

        var plan = PlanDrawing.Build(f.Model, floorId);

        var bottom = plan.Walls.Single(w => w.Id == f.Walls[0]);
        Assert.Equal(-100, bottom.Outline.Min(p => p.X), 3);
        Assert.Equal(4100, bottom.Outline.Max(p => p.X), 3);
        var free = plan.Walls.Single(w => w.Start.X == 6000);
        Assert.Equal(6000, free.Outline.Min(p => p.X), 3);
        Assert.Equal(8000, free.Outline.Max(p => p.X), 3);
    }

    [Fact]
    public void Wall_labels_stay_outside_the_rooms_even_for_a_non_convex_room()
    {
        double[][] corners = [[0, 0], [4000, 0], [4000, 2000], [2000, 2000], [2000, 4000], [0, 4000]];
        var l = new RoomFixtureL(GroundFloor.Id, corners, corners.Select(_ => Guid.NewGuid()).ToArray(), corners.Select(_ => Guid.NewGuid()).ToArray());

        var plan = PlanDrawing.Build(l.Fixture.Model, GroundFloor.Id);

        static bool Inside(IReadOnlyList<Vec2> polygon, Vec2 p)
        {
            var inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
                if ((polygon[i].Y > p.Y) != (polygon[j].Y > p.Y)
                    && p.X < (polygon[j].X - polygon[i].X) * (p.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
                    inside = !inside;
            return inside;
        }

        Assert.Equal(6, plan.Walls.Count);
        Assert.All(plan.Walls, w => Assert.DoesNotContain(plan.Rooms, r => Inside(r.Outline, w.Label)));
    }

    [Fact]
    public void Plan_formats_need_a_floor_when_geometry_spans_several()
    {
        var f = Room();
        var second = Project.AddFloor(2, 2700, 2700, "First", DateTimeOffset.UtcNow);
        f.Run(new CreateWall(Guid.NewGuid(), second.Id, Guid.NewGuid(), 0, 0, Guid.NewGuid(), 3000, 0));

        var ex = Assert.Throws<DomainException>(() => new SvgExporter().Export(Context(f)));
        Assert.Equal("EXPORT_FLOOR_REQUIRED", ex.Code);

        Assert.NotEmpty(new SvgExporter().Export(Context(f, GroundFloor.Id)));
        Assert.NotEmpty(new ObjExporter().Export(Context(f)));
    }

    [Fact]
    public void Empty_geometry_cannot_be_exported()
    {
        var context = new ExportContext(new GeometryModel(), Project.Floors, GroundFloor.Id, "Flat 12", 1);

        foreach (IGeometryExporter exporter in new IGeometryExporter[] { new SvgExporter(), new DxfExporter(), new PdfExporter(), new ObjExporter(), new PngExporter() })
            Assert.Equal("EXPORT_EMPTY", Assert.Throws<DomainException>(() => exporter.Export(context)).Code);
    }
}

// An L-shaped room built wall by wall, reusing the shared command pipeline.
sealed class RoomFixtureL
{
    public RoomFixture Fixture { get; }

    public RoomFixtureL(Guid floorId, double[][] corners, Guid[] nodes, Guid[] walls)
    {
        Fixture = new RoomFixture(floorId: floorId);
        Fixture.Run(new DeleteRoom(Fixture.RoomId));
        foreach (var wall in Fixture.Walls)
            Fixture.Run(new DeleteWall(wall));
        for (var i = 0; i < corners.Length; i++)
        {
            var next = (i + 1) % corners.Length;
            var outcome = Fixture.Run(new CreateWall(walls[i], floorId, nodes[i], corners[i][0], corners[i][1], nodes[next], corners[next][0], corners[next][1]));
            Assert.True(outcome.Success, outcome.Failure?.Message);
        }
        var room = Fixture.Run(new CreateRoom(Guid.NewGuid(), floorId, "L room", walls));
        Assert.True(room.Success, room.Failure?.Message);
    }
}
