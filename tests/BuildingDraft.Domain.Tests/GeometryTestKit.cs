using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Geometry.Commands;
using BuildingDraft.Domain.Geometry.Pipeline;

namespace BuildingDraft.Domain.Tests;

// Builds a 4000 x 3000 rectangular room through real commands.
public sealed class RoomFixture
{
    public static readonly Guid Floor = Guid.Parse("f0000000-0000-0000-0000-000000000001");

    public Guid[] Nodes { get; } = Enumerable.Range(1, 4).Select(i => Guid.Parse($"a0000000-0000-0000-0000-00000000000{i}")).ToArray();
    public Guid[] Walls { get; } = Enumerable.Range(1, 4).Select(i => Guid.Parse($"b0000000-0000-0000-0000-00000000000{i}")).ToArray();
    public Guid RoomId { get; } = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    public GeometryModel Model { get; private set; } = new();

    public RoomFixture(double thickness = 200, double[][]? corners = null)
    {
        corners ??= [[0, 0], [4000, 0], [4000, 3000], [0, 3000]];
        for (var i = 0; i < 4; i++)
        {
            var next = (i + 1) % 4;
            Run(new CreateWall(Walls[i], Floor, Nodes[i], corners[i][0], corners[i][1], Nodes[next], corners[next][0], corners[next][1], thickness));
        }
        Run(new CreateRoom(RoomId, Floor, "Living room", Walls, "living_room"));
    }

    public CommandOutcome Run(IGeometryCommand command)
    {
        var outcome = GeometryProcessor.Default.Execute(Model, command);
        if (outcome.Success)
            Model = outcome.Model!;
        return outcome;
    }
}
