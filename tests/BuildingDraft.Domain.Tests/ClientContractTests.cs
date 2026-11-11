using System.Text.Json;
using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Geometry.Commands;
using BuildingDraft.Domain.Geometry.Pipeline;

namespace BuildingDraft.Domain.Tests;

// The Flutter app's drawing session (contracts/client_session.json, written by the Dart test) must be accepted
// by the real server pipeline, command by command. If either side changes a name or a field, this fails.
public class ClientContractTests
{
    static string ContractsFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BuildingDraft.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new FileNotFoundException("Repository root not found."), "contracts", name);
    }

    static List<(string Type, JsonElement Payload)> Session() =>
        JsonDocument.Parse(File.ReadAllText(ContractsFile("client_session.json"))).RootElement.EnumerateArray()
            .Select(c => (c.GetProperty("commandType").GetString()!, c.GetProperty("payload").Clone())).ToList();

    [Fact]
    public void Every_command_the_mobile_app_produces_is_known_to_the_server()
    {
        foreach (var (type, payload) in Session())
            Assert.NotNull(GeometryCommandCatalog.Parse(type, payload));
    }

    [Fact]
    public void The_whole_session_replays_on_the_server_and_ends_in_the_same_plan()
    {
        var model = new GeometryModel();
        var processor = GeometryProcessor.Default;

        foreach (var (type, payload) in Session())
        {
            var outcome = processor.Execute(model, GeometryCommandCatalog.Parse(type, payload));
            Assert.True(outcome.Success, $"{type} was rejected: {outcome.Failure?.Message} {string.Join(", ", outcome.Failure?.Errors?.Select(e => e.Code) ?? [])}");
            model = outcome.Model!;
        }

        // Rectangle (4) + two splits (+2) + interior wall (+1) - the deleted interior wall = 6 walls.
        Assert.Equal(6, model.Walls.Count);
        Assert.Contains(model.Nodes.Values, n => n.X == 4200 && n.Y == 3200);
        Assert.Empty(ValidationEngine.Validate(model).Errors);
    }
}
