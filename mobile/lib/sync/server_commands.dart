import '../geometry/commands.dart';
import '../geometry/model.dart';
import '../geometry/vec2.dart';

double _num(Object? v) => (v! as num).toDouble();

/// Turns a command that came from the server (made on another device) into one this app can apply.
/// Returns null for commands the editor does not understand yet (rooms, openings, measurements...); the caller
/// then reloads the whole geometry instead of replaying.
PlanCommand? decodeServerCommand(String type, Map<String, Object?> p) {
  switch (type) {
    case 'create_wall':
      return CreateWall(
        wallId: p['wallId']! as String,
        startNodeId: p['startNodeId']! as String,
        start: Vec2(_num(p['startXMm']), _num(p['startYMm'])),
        endNodeId: p['endNodeId']! as String,
        end: Vec2(_num(p['endXMm']), _num(p['endYMm'])),
        thicknessMm: p['thicknessMm'] == null ? 200 : _num(p['thicknessMm']),
        heightMm: p['heightMm'] == null ? 2700 : _num(p['heightMm']),
      );
    case 'move_node':
      return MoveNode(p['nodeId']! as String, Vec2(_num(p['xMm']), _num(p['yMm'])));
    case 'delete_wall':
      return DeleteWall(p['wallId']! as String);
    case 'split_wall':
      return SplitWall(
        wallId: p['wallId']! as String,
        newWallId: p['newWallId']! as String,
        newNodeId: p['newNodeId']! as String,
        atMm: _num(p['atMm']),
      );
    default:
      return null;
  }
}

/// The walls and corners of one floor from a `GET .../geometry` response. Everything else the server stores
/// (rooms, openings, constraints) is not shown by the editor yet and is left alone here.
PlanModel modelFromSnapshot(Map<String, Object?> geometry, String floorId) {
  final nodes = {
    for (final n in (geometry['nodes']! as List).cast<Map<String, Object?>>())
      if (n['floorId'] == floorId) n['id']! as String: PlanNode(n['id']! as String, _num(n['x']), _num(n['y'])),
  };
  final walls = {
    for (final w in (geometry['walls']! as List).cast<Map<String, Object?>>())
      if (nodes.containsKey(w['startNodeId']))
        w['id']! as String: PlanWall(
          id: w['id']! as String,
          startNodeId: w['startNodeId']! as String,
          endNodeId: w['endNodeId']! as String,
          thicknessMm: _num(w['thicknessMm']),
          heightMm: _num(w['heightMm']),
        ),
  };
  return PlanModel(floorId: floorId, nodes: nodes, walls: walls);
}

/// A drawing made before the device was connected, as commands the server can replay (one per wall).
List<PlanCommand> commandsForModel(PlanModel model) => [
      for (final wall in model.walls.values)
        CreateWall(
          wallId: wall.id,
          startNodeId: wall.startNodeId,
          start: model.pos(wall.startNodeId),
          endNodeId: wall.endNodeId,
          end: model.pos(wall.endNodeId),
          thicknessMm: wall.thicknessMm,
          heightMm: wall.heightMm,
        ),
    ];
