import 'dart:math' as math;

import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';

import '../state/plan_controller.dart';
import 'plan_painter.dart';

/// The drawing surface. One finger (or the mouse) drives the current tool; two fingers pan and zoom; the mouse
/// wheel zooms. Panning with one finger works when the select tool is pressed on empty space.
class PlanCanvas extends StatefulWidget {
  const PlanCanvas({super.key, required this.controller});

  final PlanController controller;

  @override
  State<PlanCanvas> createState() => _PlanCanvasState();
}

class _PlanCanvasState extends State<PlanCanvas> {
  final Map<int, Offset> _pointers = {};
  bool _panning = false;
  bool _suppressTool = false;
  Size? _fittedFor;
  double _lastDistance = 0;
  Offset _lastFocal = Offset.zero;

  PlanController get c => widget.controller;

  void _down(PointerDownEvent e) {
    _pointers[e.pointer] = e.localPosition;
    if (_pointers.length == 2) {
      c.cancelInteraction();
      _suppressTool = true;
      _panning = false;
      final (distance, focal) = _pinch();
      _lastDistance = distance;
      _lastFocal = focal;
      return;
    }
    if (_pointers.length > 2 || _suppressTool) return;

    final hit = c.pointerDown(c.viewport.toPlan(e.localPosition));
    _panning = c.tool == PlanTool.select && !hit;
  }

  void _move(PointerMoveEvent e) {
    if (!_pointers.containsKey(e.pointer)) return;
    _pointers[e.pointer] = e.localPosition;

    if (_pointers.length >= 2) {
      final (distance, focal) = _pinch();
      var next = c.viewport.panBy(focal - _lastFocal);
      if (_lastDistance > 0 && distance > 0) next = next.zoomAt(focal, distance / _lastDistance);
      c.setViewport(next);
      _lastDistance = distance;
      _lastFocal = focal;
    } else if (_suppressTool) {
      return;
    } else if (_panning) {
      c.setViewport(c.viewport.panBy(e.delta));
    } else {
      c.pointerMove(c.viewport.toPlan(e.localPosition));
    }
  }

  void _up(PointerEvent e) {
    final wasSingle = _pointers.length == 1 && !_suppressTool;
    _pointers.remove(e.pointer);
    if (wasSingle && !_panning) {
      e is PointerCancelEvent ? c.cancelInteraction() : c.pointerUp(c.viewport.toPlan(e.localPosition));
    }
    if (_pointers.isEmpty) {
      _suppressTool = false;
      _panning = false;
    }
  }

  (double, Offset) _pinch() {
    final points = _pointers.values.take(2).toList();
    return ((points[0] - points[1]).distance, (points[0] + points[1]) / 2);
  }

  void _scroll(PointerSignalEvent e) {
    if (e is PointerScrollEvent) c.setViewport(c.viewport.zoomAt(e.localPosition, math.exp(-e.scrollDelta.dy / 300)));
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return LayoutBuilder(builder: (context, constraints) {
      final size = constraints.biggest;
      if (size.width > 0 && size != _fittedFor && !c.viewportTouched) {
        // First layout, rotation or window resize: keep the whole plan in view until the user moves the view.
        _fittedFor = size;
        WidgetsBinding.instance.addPostFrameCallback((_) => c.fitToContent(size));
      }
      return ClipRect(
        child: Listener(
          onPointerDown: _down,
          onPointerMove: _move,
          onPointerUp: _up,
          onPointerCancel: _up,
          onPointerHover: (e) => c.hoverAt(c.viewport.toPlan(e.localPosition)),
          onPointerSignal: _scroll,
          child: MouseRegion(
            cursor: c.tool == PlanTool.wall ? SystemMouseCursors.precise : SystemMouseCursors.basic,
            child: CustomPaint(key: const ValueKey('plan-canvas'), painter: PlanPainter(c, scheme), size: Size.infinite),
          ),
        ),
      );
    });
  }
}
