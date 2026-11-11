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
    case 'create_room':
      return CreateRoom(
        roomId: p['roomId']! as String,
        name: p['name']! as String,
        wallIds: (p['wallIds']! as List).cast<String>(),
        roomType: (p['roomType'] ?? 'other') as String,
        areaType: RoomAreaType.fromWire((p['areaType'] ?? 'enclosed') as String),
      );
    case 'delete_room':
      return DeleteRoom(p['roomId']! as String);
    case 'attach_opening':
      return AttachOpening(
        openingId: p['openingId']! as String,
        wallId: p['wallId']! as String,
        kind: OpeningKind.values.byName(p['kind']! as String),
        offsetMm: _num(p['offsetMm']),
        widthMm: _num(p['widthMm']),
        heightMm: _num(p['heightMm']),
        sillMm: p['sillMm'] == null ? 0 : _num(p['sillMm']),
      );
    case 'remove_opening':
      return RemoveOpening(p['openingId']! as String);
    case 'apply_measurement':
      return ApplyMeasurement(wallId: p['wallId']! as String, valueMm: _num(p['valueMm']), inner: (p['reference'] ?? 'inner') == 'inner');
    default:
      return null;
  }
}

/// One floor of a `GET .../geometry` response: walls, corners, rooms, openings and the lengths fixed by measurements.
/// Other constraints the server may hold (angles, parallel...) are not edited by the app and are not read.
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

  final rooms = {
    for (final entry in ((geometry['rooms'] ?? const []) as List).cast<Map<String, Object?>>())
      if ((entry['room']! as Map)['floorId'] == floorId)
        (entry['room']! as Map)['id']! as String: () {
          final r = (entry['room']! as Map).cast<String, Object?>();
          return PlanRoom(
            id: r['id']! as String,
            name: r['name']! as String,
            roomType: r['roomType']! as String,
            areaType: RoomAreaType.fromWire(r['areaType']! as String),
            wallIds: (r['boundaryWallIds']! as List).cast<String>(),
          );
        }(),
  };

  final openings = {
    for (final o in ((geometry['openings'] ?? const []) as List).cast<Map<String, Object?>>())
      if (walls.containsKey(o['wallId']))
        o['id']! as String: PlanOpening(
          id: o['id']! as String,
          wallId: o['wallId']! as String,
          kind: OpeningKind.values.byName(o['kind']! as String),
          offsetMm: _num(o['offsetMm']),
          widthMm: _num(o['widthMm']),
          heightMm: _num(o['heightMm']),
          sillMm: _num(o['sillMm']),
        ),
  };

  final fixedLengths = {
    for (final c in ((geometry['constraints'] ?? const []) as List).cast<Map<String, Object?>>())
      if (c['type'] == 'fixed_length' && walls.containsKey((c['targets']! as List).first)) (c['targets']! as List).first as String: _num(c['value']),
  };

  return PlanModel(floorId: floorId, nodes: nodes, walls: walls, rooms: rooms, openings: openings, fixedLengths: fixedLengths);
}

/// A drawing made before the device was connected, as commands the server can replay: walls first, then the rooms
/// and openings that need them, then the measurements (as centerline lengths, which keeps the corners where they are).
List<PlanCommand> commandsForModel(PlanModel model) => [
      ..._wallCommands(model),
      for (final room in model.rooms.values)
        CreateRoom(roomId: room.id, name: room.name, wallIds: room.wallIds, roomType: room.roomType, areaType: room.areaType),
      for (final o in model.openings.values)
        AttachOpening(openingId: o.id, wallId: o.wallId, kind: o.kind, offsetMm: o.offsetMm, widthMm: o.widthMm, heightMm: o.heightMm, sillMm: o.sillMm),
      for (final e in model.fixedLengths.entries) ApplyMeasurement(wallId: e.key, valueMm: e.value, inner: false),
    ];

List<PlanCommand> _wallCommands(PlanModel model) => [
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
