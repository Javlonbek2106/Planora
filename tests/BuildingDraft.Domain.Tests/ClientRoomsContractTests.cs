using System.Text.Json;
using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Geometry.Commands;
using BuildingDraft.Domain.Geometry.Constraints;
using BuildingDraft.Domain.Geometry.Pipeline;

namespace BuildingDraft.Domain.Tests;

// Two independent implementations of the same geometry rules (the Flutter app's Dart code and this C# code) are given
// the same 14 commands (rooms, openings, splitting, a measurement that moves a corner). The Dart test wrote what it
// computed into contracts/client_rooms_session.json; the server must reach the same corners, room areas, opening
// positions and fixed lengths. A difference means the offline editor and the server would disagree about a plan.
public class ClientRoomsContractTests
{
    static JsonElement Golden()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BuildingDraft.slnx")))
            dir = dir.Parent;
        var path = Path.Combine(dir?.FullName ?? throw new FileNotFoundException("Repository root not found."), "contracts", "client_rooms_session.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    static GeometryModel Replay(JsonElement golden)
    {
        var model = new GeometryModel();
        foreach (var c in golden.GetProperty("commands").EnumerateArray())
        {
            var type = c.GetProperty("commandType").GetString()!;
            var outcome = GeometryProcessor.Default.Execute(model, GeometryCommandCatalog.Parse(type, c.GetProperty("payload").Clone()));
            Assert.True(outcome.Success, $"{type} was rejected: {outcome.Failure?.Message} {string.Join(", ", outcome.Failure?.Errors?.Select(e => e.Code) ?? [])}");
            model = outcome.Model!;
        }
        return model;
    }

    [Fact]
    public void The_server_accepts_every_command_of_the_session()
    {
        var model = Replay(Golden());

        Assert.Equal(7, model.Walls.Count);
        Assert.Empty(ValidationEngine.Validate(model).Errors);
    }

    [Fact]
    public void Corners_end_up_exactly_where_the_app_computed_them()
    {
        var expected = Golden().GetProperty("expected");
        var model = Replay(Golden());

        Assert.Equal(expected.GetProperty("wallCount").GetInt32(), model.Walls.Count);
        foreach (var node in expected.GetProperty("nodes").EnumerateObject())
        {
            var actual = model.Nodes[Guid.Parse(node.Name)];
            Assert.Equal(node.Value[0].GetDouble(), actual.X, 3);
            Assert.Equal(node.Value[1].GetDouble(), actual.Y, 3);
        }
        Assert.Equal(expected.GetProperty("nodes").EnumerateObject().Count(), model.Nodes.Count);
    }

    [Fact]
    public void Room_areas_agree_between_the_two_implementations()
    {
        var expected = Golden().GetProperty("expected").GetProperty("rooms");
        var model = Replay(Golden());

        Assert.Equal(expected.EnumerateObject().Count(), model.Rooms.Count);
        foreach (var room in expected.EnumerateObject())
        {
            var serverRoom = model.Rooms[Guid.Parse(room.Name)];
            Assert.Equal(room.Value.GetProperty("name").GetString(), serverRoom.Name);
            Assert.Equal(room.Value.GetProperty("areaType").GetString() == "open_area" ? RoomAreaType.OpenArea : RoomAreaType.Enclosed, serverRoom.AreaType);

            var serverArea = RoomGeometry.Metrics(model, serverRoom)!.InnerAreaSqMm / 1e6;
            Assert.Equal(room.Value.GetProperty("areaM2").GetDouble(), serverArea, 6);
        }
    }

    [Fact]
    public void Openings_and_fixed_lengths_agree()
    {
        var expected = Golden().GetProperty("expected");
        var model = Replay(Golden());

        foreach (var opening in expected.GetProperty("openings").EnumerateObject())
        {
            var serverOpening = model.Openings[Guid.Parse(opening.Name)];
            Assert.Equal(Guid.Parse(opening.Value.GetProperty("wallId").GetString()!), serverOpening.WallId);
            Assert.Equal(opening.Value.GetProperty("offsetMm").GetDouble(), serverOpening.OffsetMm, 3);
        }

        var fixedLengths = model.Constraints.Where(c => c.Type == ConstraintType.FixedLength).ToDictionary(c => c.Targets[0], c => c.Value!.Value);
        Assert.Equal(expected.GetProperty("fixedLengths").EnumerateObject().Count(), fixedLengths.Count);
        foreach (var fixedLength in expected.GetProperty("fixedLengths").EnumerateObject())
            Assert.Equal(fixedLength.Value.GetDouble(), fixedLengths[Guid.Parse(fixedLength.Name)], 3);
    }

    [Fact]
    public void Dragging_a_measured_walls_corner_makes_the_server_move_the_other_end_which_is_why_the_app_refuses_it()
    {
        var model = Replay(Golden());
        var fixedWall = model.Constraints.Single(c => c.Type == ConstraintType.FixedLength).Targets[0];
        var wall = model.Walls[fixedWall];
        var length = model.Length(wall);
        var farBefore = model.Pos(wall.StartNodeId);
        var dragged = model.Pos(wall.EndNodeId);

        var outcome = GeometryProcessor.Default.Execute(model, new MoveNode(wall.EndNodeId, dragged.X, dragged.Y - 100));

        Assert.True(outcome.Success, outcome.Failure?.Message);
        Assert.Equal(length, outcome.Model!.Length(outcome.Model.Walls[fixedWall]), 2);
        Assert.NotEqual(farBefore, outcome.Model.Pos(wall.StartNodeId));
    }
}
