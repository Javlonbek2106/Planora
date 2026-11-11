using System.Text.Json;
using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Geometry.Commands;
using BuildingDraft.Domain.Geometry.Constraints;

namespace BuildingDraft.Domain.Tests;

public class GeometryEngineTests
{
    static Guid Id(int n) => Guid.Parse($"d0000000-0000-0000-0000-{n:D12}");

    [Fact]
    public void Room_metrics_use_inner_faces()
    {
        var f = new RoomFixture(thickness: 200);

        var metrics = RoomGeometry.Metrics(f.Model, f.Model.Rooms[f.RoomId])!;

        Assert.Equal(12_000_000, metrics.CenterlineAreaSqMm, 1);
        Assert.Equal(3800 * 2800, metrics.InnerAreaSqMm, 1);
        Assert.Equal(2 * (3800 + 2800), metrics.InnerPerimeterMm, 1);
    }

    [Fact]
    public void Unclosed_room_is_rejected()
    {
        var f = new RoomFixture();
        var outcome = f.Run(new CreateRoom(Id(1), RoomFixture.Floor, "Broken", [f.Walls[0], f.Walls[1], f.Walls[2]]));

        Assert.False(outcome.Success);
        Assert.Equal("GEOMETRY_VALIDATION_FAILED", outcome.Failure!.Code);
        Assert.Contains(outcome.Failure.Errors!, e => e.Code == "ROOM_NOT_CLOSED");
    }

    [Fact]
    public void Failed_command_leaves_current_model_untouched()
    {
        var f = new RoomFixture();
        var hashBefore = f.Model.ComputeHash();

        f.Run(new CreateWall(Id(2), RoomFixture.Floor, Id(10), -500, 1500, Id(11), 4500, 1500));

        Assert.Equal(hashBefore, f.Model.ComputeHash());
    }

    [Fact]
    public void Crossing_walls_are_rejected()
    {
        var f = new RoomFixture();
        var outcome = f.Run(new CreateWall(Id(2), RoomFixture.Floor, Id(10), -500, 1500, Id(11), 4500, 1500));

        Assert.Contains(outcome.Failure!.Errors!, e => e.Code == "WALL_INTERSECTION");
    }

    [Fact]
    public void Resize_with_fixed_length_constraints_keeps_the_room_a_rectangle()
    {
        var f = new RoomFixture();
        f.Run(new AddConstraint(Id(20), ConstraintType.Horizontal, [f.Walls[0]]));
        f.Run(new AddConstraint(Id(21), ConstraintType.Vertical, [f.Walls[1]]));
        f.Run(new AddConstraint(Id(22), ConstraintType.Horizontal, [f.Walls[2]]));
        f.Run(new AddConstraint(Id(23), ConstraintType.Vertical, [f.Walls[3]]));

        var outcome = f.Run(new ResizeWall(f.Walls[0], 4500));

        Assert.True(outcome.Success, outcome.Failure?.Message);
        Assert.Equal(4500, f.Model.Nodes[f.Nodes[1]].X, 2);
        Assert.Equal(4500, f.Model.Nodes[f.Nodes[2]].X, 2);
        Assert.Equal(0, f.Model.Nodes[f.Nodes[1]].Y, 2);
        Assert.Equal(3000, f.Model.Nodes[f.Nodes[2]].Y, 2);
    }

    [Fact]
    public void Conflicting_fixed_lengths_are_reported_not_hidden()
    {
        var f = new RoomFixture();
        f.Run(new AddConstraint(Id(20), ConstraintType.Horizontal, [f.Walls[0]]));
        f.Run(new AddConstraint(Id(21), ConstraintType.Vertical, [f.Walls[1]]));
        f.Run(new AddConstraint(Id(22), ConstraintType.Horizontal, [f.Walls[2]]));
        f.Run(new AddConstraint(Id(23), ConstraintType.Vertical, [f.Walls[3]]));
        f.Run(new AddConstraint(Id(24), ConstraintType.FixedLength, [f.Walls[0]], 4000));

        // Opposite wall may not differ from a rectangle's width: the two lengths contradict each other.
        var outcome = f.Run(new AddConstraint(Id(25), ConstraintType.FixedLength, [f.Walls[2]], 5000));

        Assert.False(outcome.Success);
        Assert.Equal("GEOMETRY_CONSTRAINT_CONFLICT", outcome.Failure!.Code);
        Assert.NotEmpty(outcome.Failure.Conflicts!);
        // The solver lists the constraints it could not satisfy (here the verticals give way), each with a message.
        Assert.All(outcome.Failure.Conflicts!, c => Assert.False(string.IsNullOrWhiteSpace(c.Message)));
    }

    [Fact]
    public void Perpendicular_constraint_squares_a_skewed_corner_around_a_fixed_point()
    {
        var f = new RoomFixture(corners: [[0, 0], [4000, 0], [4000, 3000], [200, 3000]]);
        f.Run(new AddConstraint(Id(30), ConstraintType.FixedPoint, [f.Nodes[0]]));
        f.Run(new AddConstraint(Id(31), ConstraintType.Horizontal, [f.Walls[0]]));

        var outcome = f.Run(new AddConstraint(Id(32), ConstraintType.Perpendicular, [f.Walls[0], f.Walls[3]]));

        Assert.True(outcome.Success, outcome.Failure?.Message);
        Assert.Equal(0, f.Model.Nodes[f.Nodes[3]].X, 2);
        Assert.Equal(0, f.Model.Nodes[f.Nodes[0]].X, 2);
        Assert.Equal(0, f.Model.Nodes[f.Nodes[1]].Y, 2);
    }

    [Fact]
    public void Moving_a_fixed_point_is_a_reported_conflict_not_a_silent_revert()
    {
        var f = new RoomFixture();
        f.Run(new AddConstraint(Id(30), ConstraintType.FixedPoint, [f.Nodes[0]]));

        var outcome = f.Run(new MoveWall(f.Walls[0], 0, 100));

        Assert.Equal("GEOMETRY_CONSTRAINT_CONFLICT", outcome.Failure!.Code);
        Assert.Contains(outcome.Failure.Conflicts!, c => c.ConstraintId == Id(30) && c.Type == ConstraintType.FixedPoint);
    }

    [Fact]
    public void Solver_is_deterministic()
    {
        var a = new RoomFixture();
        var b = new RoomFixture();
        foreach (var f in new[] { a, b })
        {
            f.Run(new AddConstraint(Id(20), ConstraintType.Horizontal, [f.Walls[0]]));
            f.Run(new AddConstraint(Id(21), ConstraintType.Vertical, [f.Walls[1]]));
            f.Run(new ResizeWall(f.Walls[0], 4321));
        }

        Assert.Equal(a.Model.ComputeHash(), b.Model.ComputeHash());
    }

    [Fact]
    public void Every_constraint_type_has_a_rule()
    {
        foreach (var type in Enum.GetValues<ConstraintType>())
            Assert.Equal(type, ConstraintRules.For(type).Type);
    }

    [Fact]
    public void Apply_measurement_converts_inner_to_centerline_and_pins_the_length()
    {
        var f = new RoomFixture(thickness: 200);

        var outcome = f.Run(new ApplyMeasurement(f.Walls[0], 4250));

        Assert.True(outcome.Success, outcome.Failure?.Message);
        Assert.Equal(4450, f.Model.Length(f.Model.Walls[f.Walls[0]]), 2);
        Assert.Contains(f.Model.Constraints, c => c.Type == ConstraintType.FixedLength && c.Value == 4450);
    }

    [Fact]
    public void Opening_outside_wall_is_rejected_and_overlap_is_rejected()
    {
        var f = new RoomFixture();

        var tooWide = f.Run(new AttachOpening(Id(40), f.Walls[0], OpeningKind.Door, 3500, 900, 2100));
        Assert.Contains(tooWide.Failure!.Errors!, e => e.Code == "OPENING_OUT_OF_BOUNDS");

        Assert.True(f.Run(new AttachOpening(Id(41), f.Walls[0], OpeningKind.Door, 500, 900, 2100)).Success);
        var overlap = f.Run(new AttachOpening(Id(42), f.Walls[0], OpeningKind.Window, 1000, 1200, 1200, 900));
        Assert.Contains(overlap.Failure!.Errors!, e => e.Code == "OPENING_OVERLAP");
    }

    [Fact]
    public void Split_wall_moves_openings_and_keeps_room_closed()
    {
        var f = new RoomFixture();
        f.Run(new AttachOpening(Id(40), f.Walls[0], OpeningKind.Door, 100, 800, 2100));
        f.Run(new AttachOpening(Id(41), f.Walls[0], OpeningKind.Window, 3000, 600, 1200, 900));

        var outcome = f.Run(new SplitWall(f.Walls[0], Id(50), Id(51), 2000));

        Assert.True(outcome.Success, outcome.Failure?.Message);
        Assert.Equal(f.Walls[0], f.Model.Openings[Id(40)].WallId);
        Assert.Equal(Id(50), f.Model.Openings[Id(41)].WallId);
        Assert.Equal(1000, f.Model.Openings[Id(41)].OffsetMm);
        Assert.Equal(5, f.Model.Rooms[f.RoomId].BoundaryWallIds.Count);
    }

    [Fact]
    public void Merge_walls_undoes_a_split_including_opening_positions()
    {
        var f = new RoomFixture();
        f.Run(new AttachOpening(Id(41), f.Walls[0], OpeningKind.Window, 3000, 600, 1200, 900));
        f.Run(new SplitWall(f.Walls[0], Id(50), Id(51), 2000));

        var outcome = f.Run(new MergeWalls(f.Walls[0], Id(50)));

        Assert.True(outcome.Success, outcome.Failure?.Message);
        Assert.Equal(3000, f.Model.Openings[Id(41)].OffsetMm, 3);
        Assert.Equal(4, f.Model.Rooms[f.RoomId].BoundaryWallIds.Count);
        Assert.False(f.Model.Nodes.ContainsKey(Id(51)));
    }

    [Fact]
    public void Deleting_a_wall_used_by_a_room_is_refused()
    {
        var f = new RoomFixture();

        var outcome = f.Run(new DeleteWall(f.Walls[0]));

        Assert.Equal("WALL_USED_BY_ROOM", outcome.Failure!.Code);
    }

    [Fact]
    public void Merge_rooms_drops_shared_wall_from_boundary_and_warns()
    {
        var f = new RoomFixture();
        var (w0, w1, w2, w3) = (f.Walls[0], f.Walls[1], f.Walls[2], f.Walls[3]);
        f.Run(new DeleteRoom(f.RoomId));
        f.Run(new SplitWall(w0, Id(50), Id(51), 2000));
        f.Run(new SplitWall(w2, Id(52), Id(53), 2000));
        Assert.True(f.Run(new CreateWall(Id(60), RoomFixture.Floor, Id(51), 2000, 0, Id(53), 2000, 3000)).Success);
        Assert.True(f.Run(new CreateRoom(Id(70), RoomFixture.Floor, "Left", [w0, Id(60), Id(52), w3])).Success);
        Assert.True(f.Run(new CreateRoom(Id(71), RoomFixture.Floor, "Right", [Id(50), w1, w2, Id(60)])).Success);

        var outcome = f.Run(new MergeRooms(Id(70), Id(71), "Open plan"));

        Assert.True(outcome.Success, outcome.Failure?.Message);
        Assert.Equal(6, f.Model.Rooms[Id(70)].BoundaryWallIds.Count);
        Assert.DoesNotContain(Id(60), f.Model.Rooms[Id(70)].BoundaryWallIds);
        Assert.False(f.Model.Rooms.ContainsKey(Id(71)));
        Assert.Contains(outcome.Warnings, w => w.Code == "SHARED_WALL_KEPT");
        Assert.True(f.Model.Walls.ContainsKey(Id(60)));
    }

    [Fact]
    public void Load_bearing_change_produces_a_warning_but_succeeds()
    {
        var f = new RoomFixture();
        f.Run(new UpdateWall(f.Walls[0], Classification: WallClassification.LoadBearing));

        var outcome = f.Run(new MoveWall(f.Walls[0], 0, -100));

        Assert.True(outcome.Success);
        Assert.Contains(outcome.Warnings, w => w.Code == "LOAD_BEARING_WALL_MODIFIED");
    }

    [Fact]
    public void Catalog_parses_snake_case_commands_and_rejects_bad_input()
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(
            """{"wallId":"b0000000-0000-0000-0000-000000000001","newLengthMm":4200,"dimensionReference":"end_fixed"}""");

        var command = (ResizeWall)GeometryCommandCatalog.Parse("resize_wall", payload);

        Assert.Equal(4200, command.NewLengthMm);
        Assert.Equal(DimensionReference.EndFixed, command.DimensionReference);
        Assert.Equal("UNKNOWN_COMMAND",
            Assert.Throws<DomainException>(() => GeometryCommandCatalog.Parse("nope", payload)).Code);
        Assert.Equal("INVALID_COMMAND_PAYLOAD",
            Assert.Throws<DomainException>(() => GeometryCommandCatalog.Parse("resize_wall", JsonSerializer.Deserialize<JsonElement>("{}"))).Code);
    }
}
