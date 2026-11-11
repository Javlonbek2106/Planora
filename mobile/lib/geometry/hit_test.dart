import 'model.dart';
import 'vec2.dart';

sealed class PlanHit {
  const PlanHit();
}

class NodeHit extends PlanHit {
  const NodeHit(this.nodeId);

  final String nodeId;
}

class WallHit extends PlanHit {
  const WallHit(this.wallId);

  final String wallId;
}

/// What is under a pointer: a corner first (it sits on top of walls), then a wall. A wall counts as hit within
/// its own half-thickness, but never less than [tolerancePx] so thin walls stay easy to tap on a phone.
PlanHit? hitTest(PlanModel model, Vec2 point, double pxPerMm, {double tolerancePx = 12}) {
  final tolerance = tolerancePx / pxPerMm;

  String? nearestNode;
  var nearestDistance = tolerance;
  for (final node in model.nodes.values) {
    final distance = node.position.distanceTo(point);
    if (distance <= nearestDistance) {
      nearestDistance = distance;
      nearestNode = node.id;
    }
  }
  if (nearestNode != null) return NodeHit(nearestNode);

  String? nearestWall;
  var wallDistance = double.infinity;
  for (final wall in model.walls.values) {
    final reach = wall.thicknessMm / 2 > tolerance ? wall.thicknessMm / 2 : tolerance;
    final distance = closestOnSegment(model.pos(wall.startNodeId), model.pos(wall.endNodeId), point).point.distanceTo(point);
    if (distance <= reach && distance < wallDistance) {
      wallDistance = distance;
      nearestWall = wall.id;
    }
  }
  return nearestWall == null ? null : WallHit(nearestWall);
}
