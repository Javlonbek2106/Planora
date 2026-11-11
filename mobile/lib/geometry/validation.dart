import 'model.dart';
import 'vec2.dart';

class PlanIssue {
  const PlanIssue(this.code, this.message, [this.objectId]);

  final String code;
  final String message;
  final String? objectId;

  @override
  String toString() => '$code: $message';
}

const _epsilonMm = 1e-6;
const _minWallLengthMm = 1.0;

/// The walls an edit touched: new or altered walls, and every wall on a corner that appeared or moved.
Set<String> changedWalls(PlanModel before, PlanModel after) {
  final moved = <String>{};
  for (final node in after.nodes.values) {
    final old = before.nodes[node.id];
    if (old == null || old.x != node.x || old.y != node.y) moved.add(node.id);
  }

  final changed = <String>{};
  for (final wall in after.walls.values) {
    final old = before.walls[wall.id];
    final unchanged = old != null &&
        old.startNodeId == wall.startNodeId &&
        old.endNodeId == wall.endNodeId &&
        old.thicknessMm == wall.thicknessMm &&
        old.heightMm == wall.heightMm;
    if (!unchanged || moved.contains(wall.startNodeId) || moved.contains(wall.endNodeId)) changed.add(wall.id);
  }
  return changed;
}

/// The local part of geometry validation (PRD 14.1): the same rules the server applies to walls, so a change
/// that passes here will not be rejected there for geometry reasons.
///
/// Checking every pair of walls is O(n^2) (about 19 ms at 500 walls, over a frame). When the plan was valid before
/// an edit, pairs of untouched walls cannot have become invalid, so pass the walls the edit changed as [only]
/// and just the pairs that involve one of them are checked.
List<PlanIssue> validatePlan(PlanModel model, {Set<String>? only}) {
  final issues = <PlanIssue>[];
  final valid = <PlanWall>[];

  for (final wall in model.walls.values) {
    if (!model.nodes.containsKey(wall.startNodeId) || !model.nodes.containsKey(wall.endNodeId)) {
      issues.add(PlanIssue('WALL_NODE_MISSING', 'A wall refers to a corner that does not exist.', wall.id));
    } else if (wall.startNodeId == wall.endNodeId || model.length(wall) < _minWallLengthMm) {
      issues.add(PlanIssue('WALL_ZERO_LENGTH', 'A wall must be at least 1 mm long.', wall.id));
    } else if (wall.thicknessMm <= 0 || wall.heightMm <= 0) {
      issues.add(PlanIssue('WALL_INVALID_SIZE', 'Wall thickness and height must be positive.', wall.id));
    } else {
      valid.add(wall);
    }
  }

  for (var i = 0; i < valid.length; i++) {
    for (var j = i + 1; j < valid.length; j++) {
      final a = valid[i];
      final b = valid[j];
      if (only != null && !only.contains(a.id) && !only.contains(b.id)) continue;
      final shared = {a.startNodeId, a.endNodeId}.intersection({b.startNodeId, b.endNodeId});

      if (shared.length == 2) {
        issues.add(PlanIssue('WALL_DUPLICATE', 'Two walls connect the same pair of corners.', b.id));
      } else if (shared.length == 1) {
        final s = model.pos(shared.first);
        final u = model.pos(a.otherNode(shared.first)) - s;
        final v = model.pos(b.otherNode(shared.first)) - s;
        if (Vec2.cross(u, v).abs() <= _epsilonMm * u.length * v.length && Vec2.dot(u, v) > 0) {
          issues.add(PlanIssue('WALL_OVERLAP', 'Two connected walls overlap each other.', b.id));
        }
      } else if (_segmentsTouch(model.pos(a.startNodeId), model.pos(a.endNodeId), model.pos(b.startNodeId), model.pos(b.endNodeId))) {
        issues.add(PlanIssue('WALL_INTERSECTION', 'Walls cross or touch without a shared corner. Split the wall to connect them.', b.id));
      }
    }
  }
  return issues;
}

bool _segmentsTouch(Vec2 p1, Vec2 p2, Vec2 q1, Vec2 q2) {
  final d1 = p2 - p1;
  final d2 = q2 - q1;
  final diff = q1 - p1;
  final denominator = Vec2.cross(d1, d2);
  final length1 = d1.length;

  if (denominator.abs() < 1e-9 * length1 * d2.length) {
    if (Vec2.cross(diff, d1).abs() > _epsilonMm * length1) return false;
    final squared = Vec2.dot(d1, d1);
    final t0 = Vec2.dot(diff, d1) / squared;
    final t1 = Vec2.dot(q2 - p1, d1) / squared;
    final tolerance = _epsilonMm / length1;
    return (t0 > t1 ? t0 : t1) >= -tolerance && (t0 < t1 ? t0 : t1) <= 1 + tolerance;
  }

  final t = Vec2.cross(diff, d2) / denominator;
  final u = Vec2.cross(diff, d1) / denominator;
  final et = _epsilonMm / length1;
  final eu = _epsilonMm / d2.length;
  return t >= -et && t <= 1 + et && u >= -eu && u <= 1 + eu;
}
