import 'model.dart';
import 'vec2.dart';

class PlanException implements Exception {
  const PlanException(this.code, this.message);

  final String code;
  final String message;

  @override
  String toString() => '$code: $message';
}

/// A reversible edit of the plan. Each command also knows how to describe itself to the server
/// (`toServer`), using the same command names and payloads as the backend's command catalog, so what is
/// drawn offline can be replayed there unchanged.
abstract class PlanCommand {
  const PlanCommand();

  String get label;

  PlanModel apply(PlanModel model);

  List<Map<String, Object?>> toServer(String floorId);
}

class CreateWall extends PlanCommand {
  const CreateWall({
    required this.wallId,
    required this.startNodeId,
    required this.start,
    required this.endNodeId,
    required this.end,
    this.thicknessMm = 200,
    this.heightMm = 2700,
  });

  final String wallId;
  final String startNodeId;
  final Vec2 start;
  final String endNodeId;
  final Vec2 end;
  final double thicknessMm;
  final double heightMm;

  @override
  String get label => 'Add wall';

  @override
  PlanModel apply(PlanModel model) {
    if (model.walls.containsKey(wallId)) throw PlanException('WALL_EXISTS', 'Wall $wallId already exists.');
    if (startNodeId == endNodeId) throw const PlanException('WALL_ZERO_LENGTH', 'A wall needs two different corners.');

    final nodes = {...model.nodes};
    nodes.putIfAbsent(startNodeId, () => PlanNode(startNodeId, roundMm(start.x), roundMm(start.y)));
    nodes.putIfAbsent(endNodeId, () => PlanNode(endNodeId, roundMm(end.x), roundMm(end.y)));
    final walls = {
      ...model.walls,
      wallId: PlanWall(id: wallId, startNodeId: startNodeId, endNodeId: endNodeId, thicknessMm: thicknessMm, heightMm: heightMm),
    };
    return model.copyWith(nodes: nodes, walls: walls);
  }

  @override
  List<Map<String, Object?>> toServer(String floorId) => [
        {
          'commandType': 'create_wall',
          'payload': {
            'wallId': wallId,
            'floorId': floorId,
            'startNodeId': startNodeId,
            'startXMm': roundMm(start.x),
            'startYMm': roundMm(start.y),
            'endNodeId': endNodeId,
            'endXMm': roundMm(end.x),
            'endYMm': roundMm(end.y),
            'thicknessMm': thicknessMm,
            'heightMm': heightMm,
          },
        }
      ];
}

class MoveNode extends PlanCommand {
  const MoveNode(this.nodeId, this.to);

  final String nodeId;
  final Vec2 to;

  @override
  String get label => 'Move corner';

  @override
  PlanModel apply(PlanModel model) {
    if (!model.nodes.containsKey(nodeId)) throw PlanException('NODE_NOT_FOUND', 'Node $nodeId not found.');
    return model.copyWith(nodes: {...model.nodes, nodeId: PlanNode(nodeId, roundMm(to.x), roundMm(to.y))});
  }

  @override
  List<Map<String, Object?>> toServer(String floorId) => [
        {
          'commandType': 'move_node',
          'payload': {'nodeId': nodeId, 'xMm': roundMm(to.x), 'yMm': roundMm(to.y)},
        }
      ];
}

class DeleteWall extends PlanCommand {
  const DeleteWall(this.wallId);

  final String wallId;

  @override
  String get label => 'Delete wall';

  @override
  PlanModel apply(PlanModel model) {
    final wall = model.walls[wallId];
    if (wall == null) throw PlanException('WALL_NOT_FOUND', 'Wall $wallId not found.');

    final walls = {...model.walls}..remove(wallId);
    final nodes = {...model.nodes};
    for (final nodeId in [wall.startNodeId, wall.endNodeId]) {
      if (!walls.values.any((w) => w.touches(nodeId))) nodes.remove(nodeId);
    }
    return model.copyWith(nodes: nodes, walls: walls);
  }

  @override
  List<Map<String, Object?>> toServer(String floorId) => [
        {
          'commandType': 'delete_wall',
          'payload': {'wallId': wallId},
        }
      ];
}

/// Cuts a wall in two at [atMm] from its start, so another wall can end exactly on it.
class SplitWall extends PlanCommand {
  const SplitWall({required this.wallId, required this.newWallId, required this.newNodeId, required this.atMm});

  final String wallId;
  final String newWallId;
  final String newNodeId;
  final double atMm;

  @override
  String get label => 'Split wall';

  @override
  PlanModel apply(PlanModel model) {
    final wall = model.walls[wallId];
    if (wall == null) throw PlanException('WALL_NOT_FOUND', 'Wall $wallId not found.');
    if (model.walls.containsKey(newWallId) || model.nodes.containsKey(newNodeId)) {
      throw const PlanException('ID_EXISTS', 'NewWallId or NewNodeId already exists.');
    }

    final start = model.pos(wall.startNodeId);
    final end = model.pos(wall.endNodeId);
    final length = start.distanceTo(end);
    if (atMm < 1 || atMm > length - 1) {
      throw const PlanException('INVALID_SPLIT_POSITION', 'Split position must be inside the wall.');
    }

    final point = start + (end - start) / length * atMm;
    return model.copyWith(
      nodes: {...model.nodes, newNodeId: PlanNode(newNodeId, roundMm(point.x), roundMm(point.y))},
      walls: {
        ...model.walls,
        wallId: PlanWall(id: wall.id, startNodeId: wall.startNodeId, endNodeId: newNodeId, thicknessMm: wall.thicknessMm, heightMm: wall.heightMm),
        newWallId: PlanWall(id: newWallId, startNodeId: newNodeId, endNodeId: wall.endNodeId, thicknessMm: wall.thicknessMm, heightMm: wall.heightMm),
      },
    );
  }

  @override
  List<Map<String, Object?>> toServer(String floorId) => [
        {
          'commandType': 'split_wall',
          'payload': {'wallId': wallId, 'newWallId': newWallId, 'newNodeId': newNodeId, 'atMm': atMm},
        }
      ];
}

/// Several commands that the user sees as one step: one undo reverts all of them.
class CommandSequence extends PlanCommand {
  const CommandSequence(this.label, this.commands);

  @override
  final String label;

  final List<PlanCommand> commands;

  @override
  PlanModel apply(PlanModel model) => commands.fold(model, (m, c) => c.apply(m));

  @override
  List<Map<String, Object?>> toServer(String floorId) => [for (final c in commands) ...c.toServer(floorId)];
}
