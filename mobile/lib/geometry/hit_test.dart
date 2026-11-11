import 'model.dart';
import 'rooms.dart';
import 'vec2.dart';

sealed class PlanHit {
  const PlanHit();
}

class NodeHit extends PlanHit {
  const NodeHit(this.nodeId);

  final String nodeId;
}

class OpeningHit extends PlanHit {
  const OpeningHit(this.openingId);

  final String openingId;
}

class WallHit extends PlanHit {
  const WallHit(this.wallId);

  final String wallId;
}

class RoomHit extends PlanHit {
  const RoomHit(this.roomId);

  final String roomId;
}

/// What is under a pointer, most specific first: a corner (it sits on top of walls), then a door or window, then a
/// wall, then a room (only the empty inside of one). A wall counts as hit within its own half-thickness, but never
/// less than [tolerancePx] so thin walls stay easy to tap on a phone.
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

  if (nearestWall != null) {
    for (final opening in model.openingsOn(nearestWall)) {
      final wall = model.walls[nearestWall]!;
      final start = model.pos(wall.startNodeId);
      final length = model.length(wall);
      final along = closestOnSegment(start, model.pos(wall.endNodeId), point).t * length;
      if (along >= opening.offsetMm && along <= opening.offsetMm + opening.widthMm) return OpeningHit(opening.id);
    }
    return WallHit(nearestWall);
  }

  for (final room in model.rooms.values) {
    final polygon = innerPolygon(model, room);
    if (polygon != null && _inside(polygon, point)) return RoomHit(room.id);
  }
  return null;
}

bool _inside(List<Vec2> polygon, Vec2 p) {
  var inside = false;
  for (var i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
    if ((polygon[i].y > p.y) != (polygon[j].y > p.y) &&
        p.x < (polygon[j].x - polygon[i].x) * (p.y - polygon[i].y) / (polygon[j].y - polygon[i].y) + polygon[i].x) {
      inside = !inside;
    }
  }
  return inside;
}
