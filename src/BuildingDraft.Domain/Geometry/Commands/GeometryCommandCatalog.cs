using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildingDraft.Domain.Geometry.Commands;

// Factory: every IGeometryCommand record is discovered by reflection and addressed by its snake_case
// name (CreateWall -> create_wall), so adding a command never requires editing this class.
public static class GeometryCommandCatalog
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    static readonly IReadOnlyDictionary<string, Type> Commands = typeof(IGeometryCommand).Assembly.GetTypes()
        .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IGeometryCommand).IsAssignableFrom(t))
        .ToDictionary(t => JsonNamingPolicy.SnakeCaseLower.ConvertName(t.Name));

    public static IReadOnlyCollection<string> Names => Commands.Keys.ToList();

    public static IGeometryCommand Parse(string commandType, JsonElement payload)
    {
        if (!Commands.TryGetValue(commandType, out var type))
            throw new DomainException("UNKNOWN_COMMAND", $"Unknown command type '{commandType}'.");

        IGeometryCommand? command;
        try
        {
            command = payload.Deserialize(type, Json) as IGeometryCommand;
        }
        catch (JsonException ex)
        {
            throw new DomainException("INVALID_COMMAND_PAYLOAD", ex.Message);
        }

        if (command is null)
            throw new DomainException("INVALID_COMMAND_PAYLOAD", "Command payload is required.");

        var missingId = type.GetProperties().FirstOrDefault(p => p.PropertyType == typeof(Guid) && (Guid)p.GetValue(command)! == Guid.Empty);
        return missingId is null
            ? command
            : throw new DomainException("INVALID_COMMAND_PAYLOAD", $"'{missingId.Name}' is required.");
    }

    public static string Serialize(IGeometryCommand command) => JsonSerializer.Serialize(command, command.GetType(), Json);
}
