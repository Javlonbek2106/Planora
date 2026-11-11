import 'dart:math' as math;

import 'model.dart';
import 'vec2.dart';

/// A room's boundary walked as one closed loop: the walls in order and the corner each one starts from.
class RoomLoop {
  const RoomLoop(this.walls, this.nodeIds);

  final List<PlanWall> walls;
  final List<String> nodeIds;
}

/// Walks the room's walls as a single closed loop, or returns null when they are not one (fewer than three, a wall
/// missing, a corner used by other than two of them, or several separate loops). Same rule as the server's.
RoomLoop? roomLoop(PlanModel model, PlanRoom room) {
  final ids = room.wallIds;
  if (ids.length < 3 || ids.toSet().length != ids.length) return null;
  if (ids.any((id) => !model.walls.containsKey(id))) return null;

  final walls = [for (final id in ids) model.walls[id]!];
  if (walls.any((w) => w.startNodeId == w.endNodeId || !model.nodes.containsKey(w.startNodeId) || !model.nodes.containsKey(w.endNodeId))) {
    return null;
  }

  final adjacency = <String, List<PlanWall>>{};
  for (final w in walls) {
    for (final node in [w.startNodeId, w.endNodeId]) {
      (adjacency[node] ??= []).add(w);
    }
  }
  if (adjacency.values.any((l) => l.length != 2)) return null;

  final ordered = <PlanWall>[];
  final nodes = <String>[];
  var current = walls.first;
  var from = current.startNodeId;
  for (var i = 0; i < walls.length; i++) {
    final to = current.otherNode(from);
    ordered.add(current);
    nodes.add(from);
    from = to;
    final next = adjacency[to]!.firstWhere((w) => w.id != current.id);
    if (i < walls.length - 1 && next.id == walls.first.id) return null; // closed too early: several loops
    current = next;
  }
  return from == walls.first.startNodeId ? RoomLoop(ordered, nodes) : null;
}

double signedArea(List<Vec2> p) {
  var sum = 0.0;
  for (var i = 0; i < p.length; i++) {
    sum += Vec2.cross(p[i], p[(i + 1) % p.length]);
  }
  return sum / 2;
}

double _perimeter(List<Vec2> p) {
  var sum = 0.0;
  for (var i = 0; i < p.length; i++) {
    sum += p[(i + 1) % p.length].distanceTo(p[i]);
  }
  return sum;
}

/// The room outline on the inner wall faces: every edge moved inward by half its wall's thickness, corners mitred.
List<Vec2>? innerPolygon(PlanModel model, PlanRoom room) {
  final loop = roomLoop(model, room);
  if (loop == null) return null;

  final centerline = [for (final id in loop.nodeIds) model.pos(id)];
  final counterClockwise = signedArea(centerline) >= 0;
  final n = centerline.length;
  final directions = <Vec2>[];
  final anchors = <Vec2>[];
  for (var i = 0; i < n; i++) {
    final d = centerline[(i + 1) % n] - centerline[i];
    directions.add(d);
    final normal = counterClockwise ? Vec2(-d.y, d.x) : Vec2(d.y, -d.x);
    anchors.add(centerline[i] + normal / d.length * (loop.walls[i].thicknessMm / 2));
  }

  final result = <Vec2>[];
  for (var i = 0; i < n; i++) {
    final prev = (i + n - 1) % n;
    final cross = Vec2.cross(directions[prev], directions[i]);
    if (cross.abs() < 1e-9 * directions[prev].length * directions[i].length) {
      result.add(anchors[i]);
      continue;
    }
    final s = Vec2.cross(anchors[i] - anchors[prev], directions[i]) / cross;
    result.add(anchors[prev] + directions[prev] * s);
  }
  return result;
}

class RoomMetrics {
  const RoomMetrics(this.innerAreaSqMm, this.innerPerimeterMm);

  final double innerAreaSqMm;
  final double innerPerimeterMm;

  double get areaSqM => innerAreaSqMm / 1e6;
}

RoomMetrics? roomMetrics(PlanModel model, PlanRoom room) {
  final polygon = innerPolygon(model, room);
  return polygon == null ? null : RoomMetrics(signedArea(polygon).abs(), _perimeter(polygon));
}

/// Where a label for the room can go: the centroid of the inner outline.
Vec2? roomLabelPoint(PlanModel model, PlanRoom room) {
  final p = innerPolygon(model, room);
  if (p == null) return null;
  final area = signedArea(p) * 2;
  if (area.abs() < 1e-9) return p.first;
  var cx = 0.0, cy = 0.0;
  for (var i = 0; i < p.length; i++) {
    final a = p[i], b = p[(i + 1) % p.length];
    final cross = Vec2.cross(a, b);
    cx += (a.x + b.x) * cross;
    cy += (a.y + b.y) * cross;
  }
  return Vec2(cx / (3 * area), cy / (3 * area));
}

/// A closed area enclosed by walls: the walls around it, in order.
class PlanFace {
  const PlanFace(this.wallIds, this.nodeIds, this.areaSqMm);

  final List<String> wallIds;
  final List<String> nodeIds;
  final double areaSqMm;
}

/// Every closed area the walls enclose (the outside is not one). Built by walking the plan's directed wall sides
/// so that the area stays on the left, always taking the tightest turn at a corner.
List<PlanFace> findFaces(PlanModel model) {
  final outgoing = <String, List<({String wallId, String to, double angle})>>{};
  for (final wall in model.walls.values) {
    final a = model.pos(wall.startNodeId);
    final b = model.pos(wall.endNodeId);
    (outgoing[wall.startNodeId] ??= []).add((wallId: wall.id, to: wall.endNodeId, angle: math.atan2(b.y - a.y, b.x - a.x)));
    (outgoing[wall.endNodeId] ??= []).add((wallId: wall.id, to: wall.startNodeId, angle: math.atan2(a.y - b.y, a.x - b.x)));
  }

  ({String wallId, String to, double angle}) next(String arrivedAt, String cameFrom, double reverseAngle) {
    // The first edge clockwise from the way back; the way back itself only at a dead end.
    final candidates = outgoing[arrivedAt]!;
    var best = candidates.first;
    var bestTurn = double.infinity;
    for (final e in candidates) {
      var turn = (reverseAngle - e.angle) % (2 * math.pi);
      if (turn < 1e-12) turn = 2 * math.pi;
      if (turn < bestTurn) {
        bestTurn = turn;
        best = e;
      }
    }
    return best;
  }

  final visited = <String>{};
  final faces = <PlanFace>[];
  for (final wall in model.walls.values) {
    for (final forward in [true, false]) {
      final startFrom = forward ? wall.startNodeId : wall.endNodeId;
      final key = '${wall.id}|$startFrom';
      if (visited.contains(key)) continue;

      final wallIds = <String>[];
      final nodeIds = <String>[];
      var from = startFrom;
      var current = (wallId: wall.id, to: wall.otherNode(startFrom));
      var closed = false;
      for (var guard = 0; guard < model.walls.length * 2 + 2; guard++) {
        visited.add('${current.wallId}|$from');
        wallIds.add(current.wallId);
        nodeIds.add(from);
        final arrivedAt = current.to;
        final back = outgoing[arrivedAt]!.firstWhere((e) => e.wallId == current.wallId && e.to == from);
        final following = next(arrivedAt, from, back.angle);
        from = arrivedAt;
        current = (wallId: following.wallId, to: following.to);
        if (from == startFrom && current.wallId == wall.id) {
          closed = true;
          break;
        }
      }
      if (!closed) continue;

      final area = signedArea([for (final id in nodeIds) model.pos(id)]);
      if (area > 1) faces.add(PlanFace(wallIds, nodeIds, area)); // counter-clockwise = inside; the outside is clockwise
    }
  }
  return faces;
}

/// The smallest closed area that contains [point], as long as it is a plain loop (no wall or corner visited twice,
/// which would make it more than a room: a wall sticking into it, say).
PlanFace? faceAt(PlanModel model, Vec2 point) {
  PlanFace? smallest;
  for (final face in findFaces(model)) {
    if (!_contains([for (final id in face.nodeIds) model.pos(id)], point)) continue;
    if (smallest == null || face.areaSqMm < smallest.areaSqMm) smallest = face;
  }
  if (smallest == null) return null;

  // The tightest area around the point decides. If it is not a plain loop it is not a room; falling back to a
  // bigger area around it would silently swallow what is inside.
  final plain = smallest.wallIds.toSet().length == smallest.wallIds.length && smallest.nodeIds.toSet().length == smallest.nodeIds.length;
  return plain ? smallest : null;
}

bool _contains(List<Vec2> polygon, Vec2 p) {
  var inside = false;
  for (var i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
    if ((polygon[i].y > p.y) != (polygon[j].y > p.y) &&
        p.x < (polygon[j].x - polygon[i].x) * (p.y - polygon[i].y) / (polygon[j].y - polygon[i].y) + polygon[i].x) {
      inside = !inside;
    }
  }
  return inside;
}
