import 'dart:math' as math;

import 'vec2.dart';

/// Creates ids for new nodes and walls. Tests inject a counter; the app uses random UUIDs, because ids are
/// created offline and must never collide with ids made on another device.
typedef IdGenerator = String Function();

String randomUuid() {
  final random = math.Random.secure();
  final bytes = List<int>.generate(16, (_) => random.nextInt(256));
  bytes[6] = (bytes[6] & 0x0f) | 0x40; // version 4
  bytes[8] = (bytes[8] & 0x3f) | 0x80; // RFC 4122 variant
  final hex = bytes.map((b) => b.toRadixString(16).padLeft(2, '0')).join();
  return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-${hex.substring(12, 16)}-${hex.substring(16, 20)}-${hex.substring(20)}';
}

class PlanNode {
  const PlanNode(this.id, this.x, this.y);

  final String id;
  final double x;
  final double y;

  Vec2 get position => Vec2(x, y);

  PlanNode moved(double x, double y) => PlanNode(id, x, y);
}

class PlanWall {
  const PlanWall({
    required this.id,
    required this.startNodeId,
    required this.endNodeId,
    this.thicknessMm = 200,
    this.heightMm = 2700,
  });

  final String id;
  final String startNodeId;
  final String endNodeId;
  final double thicknessMm;
  final double heightMm;

  String otherNode(String nodeId) => nodeId == startNodeId ? endNodeId : startNodeId;

  bool touches(String nodeId) => nodeId == startNodeId || nodeId == endNodeId;
}

/// One floor's walls and corners. Immutable: every change makes a new model, which is what makes undo a plain
/// "go back to the previous model" and keeps the canvas free of half-applied states.
class PlanModel {
  PlanModel({required this.floorId, Map<String, PlanNode> nodes = const {}, Map<String, PlanWall> walls = const {}})
      : nodes = Map.unmodifiable(nodes),
        walls = Map.unmodifiable(walls);

  final String floorId;
  final Map<String, PlanNode> nodes;
  final Map<String, PlanWall> walls;

  Vec2 pos(String nodeId) => nodes[nodeId]!.position;

  double length(PlanWall wall) => pos(wall.startNodeId).distanceTo(pos(wall.endNodeId));

  Iterable<PlanWall> wallsAt(String nodeId) => walls.values.where((w) => w.touches(nodeId));

  PlanModel copyWith({Map<String, PlanNode>? nodes, Map<String, PlanWall>? walls}) =>
      PlanModel(floorId: floorId, nodes: nodes ?? this.nodes, walls: walls ?? this.walls);

  Map<String, Object?> toJson() => {
        'floorId': floorId,
        'nodes': [for (final n in nodes.values) {'id': n.id, 'x': n.x, 'y': n.y}],
        'walls': [
          for (final w in walls.values)
            {
              'id': w.id,
              'startNodeId': w.startNodeId,
              'endNodeId': w.endNodeId,
              'thicknessMm': w.thicknessMm,
              'heightMm': w.heightMm,
            }
        ],
      };

  factory PlanModel.fromJson(Map<String, Object?> json) => PlanModel(
        floorId: json['floorId']! as String,
        nodes: {
          for (final n in (json['nodes']! as List).cast<Map<String, Object?>>())
            n['id']! as String: PlanNode(n['id']! as String, (n['x']! as num).toDouble(), (n['y']! as num).toDouble())
        },
        walls: {
          for (final w in (json['walls']! as List).cast<Map<String, Object?>>())
            w['id']! as String: PlanWall(
              id: w['id']! as String,
              startNodeId: w['startNodeId']! as String,
              endNodeId: w['endNodeId']! as String,
              thicknessMm: (w['thicknessMm']! as num).toDouble(),
              heightMm: (w['heightMm']! as num).toDouble(),
            )
        },
      );
}
