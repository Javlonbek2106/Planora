import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../geometry/model.dart';
import '../geometry/rooms.dart';
import '../geometry/snap.dart';
import '../geometry/vec2.dart';
import '../state/plan_controller.dart';

class _RoomShape {
  const _RoomShape(this.polygon, this.label, this.areaSqM);

  final List<Vec2> polygon;
  final Vec2 label;
  final double areaSqM;
}

/// Draws the plan: grid, rooms, walls, doors and windows, corners, selection, the wall being drawn and the current
/// snap target. It repaints by listening to the controller, and only walls that intersect the screen are drawn.
class PlanPainter extends CustomPainter {
  PlanPainter(this.c, this.scheme) : super(repaint: c);

  final PlanController c;
  final ColorScheme scheme;

  // Text layout is the expensive part of a frame, so each distinct label is laid out once.
  static final _labels = <String, TextPainter>{};

  // Room outlines only change when the plan does, and a plan is immutable: cache them per plan instance.
  static PlanModel? _shapesFor;
  static Map<String, _RoomShape> _shapes = const {};

  Map<String, _RoomShape> _roomShapes(PlanModel model) {
    if (!identical(_shapesFor, model)) {
      _shapesFor = model;
      _shapes = {
        for (final room in model.rooms.values)
          if (innerPolygon(model, room) case final polygon?)
            room.id: _RoomShape(polygon, roomLabelPoint(model, room)!, signedArea(polygon).abs() / 1e6),
      };
    }
    return _shapes;
  }

  @override
  void paint(Canvas canvas, Size size) {
    final v = c.viewport;
    canvas.drawRect(Offset.zero & size, Paint()..color = scheme.surface);
    _paintGrid(canvas, size);

    final model = c.model;
    final visible = Rect.fromLTWH(-40, -40, size.width + 80, size.height + 80);
    Offset at(String nodeId) => v.toScreen(nodeId == c.dragNodeId && c.dragPosition != null ? c.dragPosition! : model.pos(nodeId));

    _paintRooms(canvas, model);

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

    _paintOpenings(canvas, model, at);

    for (final wall in model.walls.values) {
      final a = at(wall.startNodeId);
      final b = at(wall.endNodeId);
      if (!visible.contains(a) && !visible.contains(b)) continue;
      if ((a - b).distance < 70) continue;
      final fixed = model.fixedLengths.containsKey(wall.id);
      _paintLength(
        canvas,
        a,
        b,
        '${(model.length(wall) / 1000).toStringAsFixed(2)}${fixed ? ' (fixed)' : ''}',
        fixed ? scheme.tertiary : scheme.onSurfaceVariant,
        clearancePx: math.max(wall.thicknessMm * v.pxPerMm, 2) / 2,
      );
    }

    final ring = Paint()..style = PaintingStyle.stroke..strokeWidth = 1.6;
    for (final node in model.nodes.values) {
      final p = at(node.id);
      if (!visible.contains(p)) continue;
      final selected = node.id == c.selectedNodeId || node.id == c.dragNodeId;
      canvas.drawCircle(p, selected ? 7 : 4, Paint()..color = scheme.surface);
      canvas.drawCircle(p, selected ? 7 : 4, ring..color = selected ? scheme.primary : scheme.onSurface);
    }

    _paintOpeningPreview(canvas, model);
    _paintDraft(canvas, size);
  }

  void _paintRooms(Canvas canvas, PlanModel model) {
    final v = c.viewport;
    final shapes = _roomShapes(model);
    for (final room in model.rooms.values) {
      final shape = shapes[room.id];
      if (shape == null) continue;

      final path = Path()..addPolygon([for (final p in shape.polygon) v.toScreen(p)], true);
      final selected = room.id == c.selectedRoomId;
      final base = room.areaType == RoomAreaType.enclosed ? scheme.primaryContainer : scheme.tertiaryContainer;
      canvas.drawPath(path, Paint()..color = base.withValues(alpha: selected ? 0.9 : 0.55));
      if (selected) canvas.drawPath(path, Paint()..color = scheme.primary..style = PaintingStyle.stroke..strokeWidth = 2);

      final centre = v.toScreen(shape.label);
      _paintCentered(canvas, centre - const Offset(0, 8), room.name, scheme.onPrimaryContainer, 13);
      _paintCentered(canvas, centre + const Offset(0, 9), '${shape.areaSqM.toStringAsFixed(2)} m²', scheme.onPrimaryContainer, 11);
    }
  }

  /// A door or window is a gap in the wall with a coloured frame: drawn in the background colour over the wall.
  void _paintOpenings(Canvas canvas, PlanModel model, Offset Function(String) at) {
    final v = c.viewport;
    for (final opening in model.openings.values) {
      final wall = model.walls[opening.wallId];
      if (wall == null) continue;
      final offset = opening.id == c.dragOpeningId && c.dragOpeningOffset != null ? c.dragOpeningOffset! : opening.offsetMm;
      final selected = opening.id == c.selectedOpeningId;
      _paintOpeningBox(canvas, wall, at, offset, opening.widthMm, opening.kind, selected: selected, scale: v.pxPerMm);
    }
  }

  void _paintOpeningBox(Canvas canvas, PlanWall wall, Offset Function(String) at, double offsetMm, double widthMm, OpeningKind kind,
      {bool selected = false, bool ghost = false, required double scale}) {
    final a = at(wall.startNodeId);
    final b = at(wall.endNodeId);
    final length = (b - a).distance;
    if (length == 0) return;
    final direction = (b - a) / length;
    final normal = Offset(-direction.dy, direction.dx);
    final half = math.max(wall.thicknessMm * scale, 3) / 2 + 1;
    final from = a + direction * (offsetMm * scale);
    final to = a + direction * ((offsetMm + widthMm) * scale);
    final path = Path()
      ..moveTo((from + normal * half).dx, (from + normal * half).dy)
      ..lineTo((to + normal * half).dx, (to + normal * half).dy)
      ..lineTo((to - normal * half).dx, (to - normal * half).dy)
      ..lineTo((from - normal * half).dx, (from - normal * half).dy)
      ..close();

    final frame = kind == OpeningKind.door ? scheme.error : scheme.primary;
    canvas.drawPath(path, Paint()..color = ghost ? frame.withValues(alpha: 0.25) : scheme.surface);
    canvas.drawPath(
      path,
      Paint()
        ..color = selected ? scheme.tertiary : frame
        ..style = PaintingStyle.stroke
        ..strokeWidth = selected ? 3 : 1.6,
    );
    if (kind == OpeningKind.window && !ghost) {
      final mid = a + direction * ((offsetMm + widthMm / 2) * scale);
      canvas.drawLine(mid - normal * (half - 2), mid + normal * (half - 2), Paint()..color = frame..strokeWidth = 1.2);
    }
  }

  void _paintOpeningPreview(Canvas canvas, PlanModel model) {
    final preview = c.openingPreview;
    if (preview == null) return;
    final wall = model.walls[preview.wallId];
    if (wall == null) return;
    final v = c.viewport;
    _paintOpeningBox(canvas, wall, (id) => v.toScreen(model.pos(id)), preview.offsetMm, preview.widthMm, preview.kind, ghost: true, scale: v.pxPerMm);
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

  TextPainter _label(String text, Color color, double size) {
    return _labels.putIfAbsent('$text|${color.toARGB32()}|$size', () {
      if (_labels.length > 400) _labels.clear();
      return TextPainter(
        text: TextSpan(text: text, style: TextStyle(fontSize: size, color: color, fontWeight: FontWeight.w600)),
        textDirection: TextDirection.ltr,
      )..layout();
    });
  }

  void _paintCentered(Canvas canvas, Offset centre, String text, Color color, double size) {
    final painter = _label(text, color, size);
    painter.paint(canvas, centre - Offset(painter.width / 2, painter.height / 2));
  }

  void _paintLength(Canvas canvas, Offset a, Offset b, String text, Color color, {required double clearancePx}) {
    final painter = _label(text, color, 11);
    final mid = (a + b) / 2;
    final direction = b - a;
    final normal = direction.distance == 0 ? Offset.zero : Offset(-direction.dy, direction.dx) / direction.distance;
    // Beside the wall, not on it: half the wall's drawn thickness plus room for half the text in that direction.
    final distance = clearancePx + 6 + (normal.dx.abs() * painter.width + normal.dy.abs() * painter.height) / 2;
    painter.paint(canvas, mid + normal * distance - Offset(painter.width / 2, painter.height / 2));
  }

  @override
  bool shouldRepaint(PlanPainter old) => old.c != c || old.scheme != scheme;
}
