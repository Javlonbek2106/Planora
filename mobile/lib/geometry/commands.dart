import 'model.dart';
import 'rooms.dart';
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
/// drawn offline can be replayed there unchanged. The side effects on rooms, openings and fixed lengths follow the
/// server's rules exactly: a plan that differs here would be changed (or refused) by the server later.
abstract class PlanCommand {
  const PlanCommand();

  String get label;

  PlanModel apply(PlanModel model);

  List<Map<String, Object?>> toServer(String floorId);
}

Map<String, Object?> _command(String type, Map<String, Object?> payload) => {'commandType': type, 'payload': payload};

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
        _command('create_wall', {
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
        })
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
        _command('move_node', {'nodeId': nodeId, 'xMm': roundMm(to.x), 'yMm': roundMm(to.y)})
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
    if (model.rooms.values.any((r) => r.wallIds.contains(wallId))) {
      throw const PlanException('WALL_USED_BY_ROOM', 'Delete or change the rooms bounded by this wall first.');
    }

    final walls = {...model.walls}..remove(wallId);
    final nodes = {...model.nodes};
    for (final nodeId in [wall.startNodeId, wall.endNodeId]) {
      if (!walls.values.any((w) => w.touches(nodeId))) nodes.remove(nodeId);
    }
    return model.copyWith(
      nodes: nodes,
      walls: walls,
      openings: {...model.openings}..removeWhere((_, o) => o.wallId == wallId),
      fixedLengths: {...model.fixedLengths}..remove(wallId),
    );
  }

  @override
  List<Map<String, Object?>> toServer(String floorId) => [
        _command('delete_wall', {'wallId': wallId})
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

    // Openings after the cut move to the new wall; one that crosses the cut has nowhere to go.
    final openings = {...model.openings};
    for (final opening in model.openingsOn(wallId)) {
      if (opening.offsetMm + opening.widthMm <= atMm) continue;
      if (opening.offsetMm < atMm) {
        throw PlanException('OPENING_STRADDLES_SPLIT', 'A ${opening.kind.name} crosses the split position.');
      }
      openings[opening.id] = opening.copyWith(wallId: newWallId, offsetMm: opening.offsetMm - atMm);
    }

    final point = start + (end - start) / length * atMm;
    final rooms = {
      for (final e in model.rooms.entries)
        e.key: e.value.wallIds.contains(wallId)
            ? PlanRoom(id: e.value.id, name: e.value.name, roomType: e.value.roomType, areaType: e.value.areaType, wallIds: [...e.value.wallIds, newWallId])
            : e.value,
    };

    return model.copyWith(
      nodes: {...model.nodes, newNodeId: PlanNode(newNodeId, roundMm(point.x), roundMm(point.y))},
      walls: {
        ...model.walls,
        wallId: PlanWall(id: wall.id, startNodeId: wall.startNodeId, endNodeId: newNodeId, thicknessMm: wall.thicknessMm, heightMm: wall.heightMm),
        newWallId: PlanWall(id: newWallId, startNodeId: newNodeId, endNodeId: wall.endNodeId, thicknessMm: wall.thicknessMm, heightMm: wall.heightMm),
      },
      openings: openings,
      rooms: rooms,
      // The server drops a fixed length on a wall that is cut: it no longer describes either half.
      fixedLengths: {...model.fixedLengths}..remove(wallId),
    );
  }

  @override
  List<Map<String, Object?>> toServer(String floorId) => [
        _command('split_wall', {'wallId': wallId, 'newWallId': newWallId, 'newNodeId': newNodeId, 'atMm': atMm})
      ];
}

class CreateRoom extends PlanCommand {
  const CreateRoom({required this.roomId, required this.name, required this.wallIds, this.roomType = 'other', this.areaType = RoomAreaType.enclosed});

  final String roomId;
  final String name;
  final List<String> wallIds;
  final String roomType;
  final RoomAreaType areaType;

  @override
  String get label => 'Add room';

  @override
  PlanModel apply(PlanModel model) {
    if (model.rooms.containsKey(roomId)) throw PlanException('ROOM_EXISTS', 'Room $roomId already exists.');
    if (name.trim().isEmpty) throw const PlanException('INVALID_ROOM_NAME', 'Room name is required.');

    final room = PlanRoom(id: roomId, name: name.trim(), roomType: roomType, areaType: areaType, wallIds: wallIds);
    final ordered = roomLoop(model, room)?.walls.map((w) => w.id).toList() ?? wallIds;
    return model.copyWith(rooms: {
      ...model.rooms,
      roomId: PlanRoom(id: roomId, name: room.name, roomType: roomType, areaType: areaType, wallIds: ordered),
    });
  }

  @override
  List<Map<String, Object?>> toServer(String floorId) => [
        _command('create_room', {
          'roomId': roomId,
          'floorId': floorId,
          'name': name.trim(),
          'wallIds': wallIds,
          'roomType': roomType,
          'areaType': areaType.wire,
        })
      ];
}

class DeleteRoom extends PlanCommand {
  const DeleteRoom(this.roomId);

  final String roomId;

  @override
  String get label => 'Delete room';

  @override
  PlanModel apply(PlanModel model) {
    if (!model.rooms.containsKey(roomId)) throw PlanException('ROOM_NOT_FOUND', 'Room $roomId not found.');
    return model.copyWith(rooms: {...model.rooms}..remove(roomId));
  }

  @override
  List<Map<String, Object?>> toServer(String floorId) => [
        _command('delete_room', {'roomId': roomId})
      ];
}

class AttachOpening extends PlanCommand {
  const AttachOpening({
    required this.openingId,
    required this.wallId,
    required this.kind,
    required this.offsetMm,
    required this.widthMm,
    required this.heightMm,
    this.sillMm = 0,
  });

  final String openingId;
  final String wallId;
  final OpeningKind kind;
  final double offsetMm;
  final double widthMm;
  final double heightMm;
  final double sillMm;

  @override
  String get label => kind == OpeningKind.door ? 'Add door' : 'Add window';

  @override
  PlanModel apply(PlanModel model) {
    if (!model.walls.containsKey(wallId)) throw PlanException('WALL_NOT_FOUND', 'Wall $wallId not found.');
    if (model.openings.containsKey(openingId)) throw PlanException('OPENING_EXISTS', 'Opening $openingId already exists.');
    return model.copyWith(openings: {
      ...model.openings,
      openingId: PlanOpening(
        id: openingId,
        wallId: wallId,
        kind: kind,
        offsetMm: roundMm(offsetMm),
        widthMm: widthMm,
        heightMm: heightMm,
        sillMm: sillMm,
      ),
    });
  }

  @override
  List<Map<String, Object?>> toServer(String floorId) => [
        _command('attach_opening', {
          'openingId': openingId,
          'wallId': wallId,
          'kind': kind.name,
          'offsetMm': roundMm(offsetMm),
          'widthMm': widthMm,
          'heightMm': heightMm,
          'sillMm': sillMm,
          'subtype': 'single',
        })
      ];
}

class RemoveOpening extends PlanCommand {
  const RemoveOpening(this.openingId);

  final String openingId;

  @override
  String get label => 'Delete opening';

  @override
  PlanModel apply(PlanModel model) {
    if (!model.openings.containsKey(openingId)) throw PlanException('OPENING_NOT_FOUND', 'Opening $openingId not found.');
    return model.copyWith(openings: {...model.openings}..remove(openingId));
  }

  @override
  List<Map<String, Object?>> toServer(String floorId) => [
        _command('remove_opening', {'openingId': openingId})
      ];
}

/// Fixes a wall to a measured length. [valueMm] is an inner dimension by default (between the wall faces), which the
/// server turns into a centerline length by adding the wall thickness; the far end moves, the start stays.
class ApplyMeasurement extends PlanCommand {
  const ApplyMeasurement({required this.wallId, required this.valueMm, this.inner = true});

  final String wallId;
  final double valueMm;
  final bool inner;

  @override
  String get label => 'Apply measurement';

  @override
  PlanModel apply(PlanModel model) {
    final wall = model.walls[wallId];
    if (wall == null) throw PlanException('WALL_NOT_FOUND', 'Wall $wallId not found.');
    if (valueMm <= 0) throw const PlanException('INVALID_MEASUREMENT', 'A measurement must be positive.');

    final centerline = inner ? valueMm + wall.thicknessMm : valueMm;
    final start = model.pos(wall.startNodeId);
    final end = model.pos(wall.endNodeId);
    final current = start.distanceTo(end);
    if (current < 1e-9) throw const PlanException('WALL_ZERO_LENGTH', 'Cannot resize a zero-length wall.');

    final moved = start + (end - start) / current * centerline;
    return model.copyWith(
      nodes: {...model.nodes, wall.endNodeId: PlanNode(wall.endNodeId, roundMm(moved.x), roundMm(moved.y))},
      fixedLengths: {...model.fixedLengths, wallId: roundMm(centerline)},
    );
  }

  @override
  List<Map<String, Object?>> toServer(String floorId) => [
        _command('apply_measurement', {'wallId': wallId, 'valueMm': valueMm, 'reference': inner ? 'inner' : 'centerline'})
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
