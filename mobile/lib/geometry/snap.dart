import 'model.dart';
import 'vec2.dart';

enum SnapKind { none, grid, axis, node, wall }

class SnapResult {
  const SnapResult(this.point, this.kind, {this.nodeId, this.wallId, this.t});

  final Vec2 point;
  final SnapKind kind;

  /// Set when the point snapped to an existing corner.
  final String? nodeId;

  /// Set when the point snapped onto the body of a wall, [t] along it (0 start, 1 end): a T-junction.
  final String? wallId;
  final double? t;
}

/// Decides where a pointer position ends up: onto a corner, onto a wall, in line with the previous point, or on
/// the grid. Tolerances are in screen pixels, so snapping feels the same at every zoom level.
class SnapEngine {
  const SnapEngine({
    this.gridMm = 100,
    this.nodeTolerancePx = 14,
    this.wallTolerancePx = 10,
    this.axisTolerancePx = 8,
    this.minGridPx = 10,
  });

  final double gridMm;
  final double nodeTolerancePx;
  final double wallTolerancePx;
  final double axisTolerancePx;
  final double minGridPx;

  /// The grid step actually used: doubled until lines are at least [minGridPx] apart, so a zoomed-out plan
  /// does not snap to steps the user cannot see.
  double effectiveGridMm(double pxPerMm) {
    var step = gridMm;
    while (step * pxPerMm < minGridPx) {
      step *= 2;
    }
    return step;
  }

  SnapResult snap(
    Vec2 raw, {
    required PlanModel model,
    required double pxPerMm,
    Vec2? anchor,
    Set<String> excludeNodes = const {},
    bool useGrid = true,
  }) {
    final nodeTolerance = nodeTolerancePx / pxPerMm;
    String? nearestNode;
    var nearestDistance = nodeTolerance;
    for (final node in model.nodes.values) {
      if (excludeNodes.contains(node.id)) continue;
      final distance = node.position.distanceTo(raw);
      if (distance <= nearestDistance) {
        nearestDistance = distance;
        nearestNode = node.id;
      }
    }
    if (nearestNode != null) return SnapResult(model.pos(nearestNode), SnapKind.node, nodeId: nearestNode);

    final wallTolerance = wallTolerancePx / pxPerMm;
    String? nearestWall;
    double? nearestT;
    Vec2? nearestPoint;
    var wallDistance = wallTolerance;
    for (final wall in model.walls.values) {
      if (excludeNodes.contains(wall.startNodeId) || excludeNodes.contains(wall.endNodeId)) continue;
      final closest = closestOnSegment(model.pos(wall.startNodeId), model.pos(wall.endNodeId), raw);
      final distance = closest.point.distanceTo(raw);
      if (distance <= wallDistance && closest.t > 0 && closest.t < 1) {
        wallDistance = distance;
        nearestWall = wall.id;
        nearestT = closest.t;
        nearestPoint = closest.point;
      }
    }
    if (nearestWall != null) return SnapResult(nearestPoint!, SnapKind.wall, wallId: nearestWall, t: nearestT);

    final grid = effectiveGridMm(pxPerMm);
    double onGrid(double v) => useGrid ? (v / grid).roundToDouble() * grid : v;

    if (anchor != null) {
      final axisTolerance = axisTolerancePx / pxPerMm;
      if ((raw.x - anchor.x).abs() <= axisTolerance && (raw.y - anchor.y).abs() > axisTolerance) {
        return SnapResult(Vec2(anchor.x, onGrid(raw.y)), SnapKind.axis);
      }
      if ((raw.y - anchor.y).abs() <= axisTolerance && (raw.x - anchor.x).abs() > axisTolerance) {
        return SnapResult(Vec2(onGrid(raw.x), anchor.y), SnapKind.axis);
      }
    }

    return useGrid ? SnapResult(Vec2(onGrid(raw.x), onGrid(raw.y)), SnapKind.grid) : SnapResult(raw, SnapKind.none);
  }
}
