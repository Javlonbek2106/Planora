import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../geometry/snap.dart';
import '../geometry/vec2.dart';
import '../state/plan_controller.dart';

/// Draws the plan: grid, walls, corners, selection, the wall being drawn and the current snap target.
/// It repaints by listening to the controller, and only walls that intersect the screen are drawn.
class PlanPainter extends CustomPainter {
  PlanPainter(this.c, this.scheme) : super(repaint: c);

  final PlanController c;
  final ColorScheme scheme;

  // Text layout is the expensive part of a frame, so each distinct label is laid out once.
  static final _labels = <String, TextPainter>{};

  @override
  void paint(Canvas canvas, Size size) {
    final v = c.viewport;
    canvas.drawRect(Offset.zero & size, Paint()..color = scheme.surface);
    _paintGrid(canvas, size);

    final model = c.model;
    final visible = Rect.fromLTWH(-40, -40, size.width + 80, size.height + 80);
    Offset at(String nodeId) => v.toScreen(nodeId == c.dragNodeId && c.dragPosition != null ? c.dragPosition! : model.pos(nodeId));

    // Opaque on purpose: translucent walls get brighter where two of them overlap at a corner.
    final wallColor = Color.alphaBlend(scheme.onSurface.withValues(alpha: 0.82), scheme.surface);
    final wallPaint = Paint()..strokeCap = StrokeCap.square;
    for (final wall in model.walls.values) {
      final a = at(wall.startNodeId);
      final b = at(wall.endNodeId);
      if (!visible.overlaps(Rect.fromPoints(a, b).inflate(wall.thicknessMm * v.pxPerMm))) continue;

      final selected = wall.id == c.selectedWallId;
      wallPaint
        ..color = selected ? scheme.primary : wallColor
        ..strokeWidth = math.max(wall.thicknessMm * v.pxPerMm, 2);
      canvas.drawLine(a, b, wallPaint);
    }

    for (final wall in model.walls.values) {
      final a = at(wall.startNodeId);
      final b = at(wall.endNodeId);
      if (!visible.contains(a) && !visible.contains(b)) continue;
      if ((a - b).distance < 70) continue;
      _paintLength(canvas, a, b, (model.length(wall) / 1000).toStringAsFixed(2), scheme.onSurfaceVariant, clearancePx: math.max(wall.thicknessMm * v.pxPerMm, 2) / 2);
    }

    final ring = Paint()..style = PaintingStyle.stroke..strokeWidth = 1.6;
    for (final node in model.nodes.values) {
      final p = at(node.id);
      if (!visible.contains(p)) continue;
      final selected = node.id == c.selectedNodeId || node.id == c.dragNodeId;
      canvas.drawCircle(p, selected ? 7 : 4, Paint()..color = scheme.surface);
      canvas.drawCircle(p, selected ? 7 : 4, ring..color = selected ? scheme.primary : scheme.onSurface);
    }

    _paintDraft(canvas, size);
  }

  void _paintGrid(Canvas canvas, Size size) {
    final v = c.viewport;
    final step = c.snapEngine.effectiveGridMm(v.pxPerMm);
    final topLeft = v.toPlan(Offset.zero);
    final bottomRight = v.toPlan(Offset(size.width, size.height));
    final minor = Paint()..color = scheme.outlineVariant.withValues(alpha: 0.35)..strokeWidth = 1;
    final major = Paint()..color = scheme.outlineVariant.withValues(alpha: 0.8)..strokeWidth = 1;

    var index = (topLeft.x / step).floor();
    for (var x = index * step; x <= bottomRight.x; x += step, index++) {
      final sx = v.toScreen(Vec2(x, 0)).dx;
      canvas.drawLine(Offset(sx, 0), Offset(sx, size.height), index % 5 == 0 ? major : minor);
    }
    index = (bottomRight.y / step).floor();
    for (var y = index * step; y <= topLeft.y; y += step, index++) {
      final sy = v.toScreen(Vec2(0, y)).dy;
      canvas.drawLine(Offset(0, sy), Offset(size.width, sy), index % 5 == 0 ? major : minor);
    }
  }

  void _paintDraft(Canvas canvas, Size size) {
    final v = c.viewport;
    final start = c.draftStart;
    final hover = c.hover;

    if (start != null && hover != null) {
      final a = v.toScreen(start.point);
      final b = v.toScreen(hover.point);
      canvas.drawLine(
        a,
        b,
        Paint()
          ..color = scheme.primary.withValues(alpha: 0.55)
          ..strokeWidth = math.max(200 * v.pxPerMm, 2)
          ..strokeCap = StrokeCap.square,
      );
      _paintLength(canvas, a, b, (start.point.distanceTo(hover.point) / 1000).toStringAsFixed(2), scheme.primary, clearancePx: math.max(200 * v.pxPerMm, 2) / 2);
    }
    if (start != null) canvas.drawCircle(v.toScreen(start.point), 6, Paint()..color = scheme.primary);

    if (hover != null) {
      final p = v.toScreen(hover.point);
      final paint = Paint()..color = scheme.tertiary..style = PaintingStyle.stroke..strokeWidth = 2;
      switch (hover.kind) {
        case SnapKind.node:
          canvas.drawCircle(p, 10, paint);
        case SnapKind.wall:
          canvas.drawPath(Path()..moveTo(p.dx, p.dy - 9)..lineTo(p.dx + 9, p.dy)..lineTo(p.dx, p.dy + 9)..lineTo(p.dx - 9, p.dy)..close(), paint);
        case SnapKind.axis:
          canvas.drawLine(p - const Offset(8, 0), p + const Offset(8, 0), paint);
          canvas.drawLine(p - const Offset(0, 8), p + const Offset(0, 8), paint);
        case SnapKind.grid || SnapKind.none:
          canvas.drawCircle(p, 3, Paint()..color = scheme.tertiary);
      }
    }
  }

  void _paintLength(Canvas canvas, Offset a, Offset b, String text, Color color, {required double clearancePx}) {
    final painter = _labels.putIfAbsent('$text|${color.toARGB32()}', () {
      if (_labels.length > 400) _labels.clear();
      return TextPainter(
        text: TextSpan(text: text, style: TextStyle(fontSize: 11, color: color, fontWeight: FontWeight.w600)),
        textDirection: TextDirection.ltr,
      )..layout();
    });
    final mid = (a + b) / 2;
    final direction = b - a;
    final normal = direction.distance == 0 ? Offset.zero : Offset(-direction.dy, direction.dx) / direction.distance;
    // Beside the wall, not on it: half the wall's drawn thickness plus room for half the text in that direction.
    final distance = clearancePx + 6 + (normal.dx.abs() * painter.width + normal.dy.abs() * painter.height) / 2;
    final spot = mid + normal * distance - Offset(painter.width / 2, painter.height / 2);
    painter.paint(canvas, spot);
  }

  @override
  bool shouldRepaint(PlanPainter old) => old.c != c || old.scheme != scheme;
}
