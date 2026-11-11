import 'dart:ui' show Offset, Size;

import 'package:flutter/foundation.dart';

import '../geometry/commands.dart';
import '../geometry/hit_test.dart';
import '../geometry/model.dart';
import '../geometry/snap.dart';
import '../geometry/validation.dart';
import '../geometry/vec2.dart';
import 'viewport.dart';

enum PlanTool { select, wall }

/// Everything the 2D Constructor knows: the plan, the history, the current tool and the interaction in progress.
/// Pointer handling lives here (not in the widget) so that drawing, snapping and dragging can be tested
/// without a UI.
class PlanController extends ChangeNotifier {
  PlanController({PlanModel? model, IdGenerator? ids, this.snapEngine = const SnapEngine()})
      : _model = model ?? PlanModel(floorId: 'floor-1'),
        _ids = ids ?? randomUuid;

  final SnapEngine snapEngine;
  final IdGenerator _ids;

  PlanModel _model;
  final List<PlanModel> _undoStack = [];
  final List<PlanModel> _redoStack = [];
  final List<PlanCommand> _log = [];
  final List<PlanCommand> _redoLog = [];

  PlanTool tool = PlanTool.select;
  Viewport viewport = const Viewport();
  bool snapToGrid = true;

  /// True once the user panned or zoomed; until then the plan is re-fitted when the screen size changes.
  bool viewportTouched = false;

  String? selectedWallId;
  String? selectedNodeId;
  String? lastError;

  // Wall tool: the point the next wall starts from (a chain of walls keeps going from the last end).
  SnapResult? draftStart;
  SnapResult? hover;
  bool _startedThisPress = false;
  bool _movedThisPress = false;
  Vec2? _pressPlanPoint;

  // Select tool: a corner being dragged, shown at [dragPosition] until the pointer is released.
  String? dragNodeId;
  Vec2? dragPosition;

  PlanModel get model => _model;

  bool get canUndo => _undoStack.isNotEmpty;

  bool get canRedo => _redoStack.isNotEmpty;

  /// The edits made so far, as server commands: what a sync would send.
  List<Map<String, Object?>> get serverCommands => [for (final c in _log) ...c.toServer(_model.floorId)];

  // ---- history ----

  /// Applies [command] if the result is valid geometry; otherwise leaves the plan untouched and sets [lastError].
  bool execute(PlanCommand command) {
    try {
      final next = command.apply(_model);
      // The plan is valid before every edit, so only pairs that involve an edited wall can have become invalid.
      final issues = validatePlan(next, only: changedWalls(_model, next));
      if (issues.isNotEmpty) {
        lastError = issues.first.message;
        notifyListeners();
        return false;
      }
      _undoStack.add(_model);
      _redoStack.clear();
      _log.add(command);
      _redoLog.clear();
      _model = next;
      lastError = null;
      _dropStaleSelection();
      notifyListeners();
      return true;
    } on PlanException catch (e) {
      lastError = e.message;
      notifyListeners();
      return false;
    }
  }

  /// Replaces the plan with [next] (a different document) and forgets all history.
  void load(PlanModel next) {
    _model = next;
    _undoStack.clear();
    _redoStack.clear();
    _log.clear();
    _redoLog.clear();
    selectedWallId = null;
    selectedNodeId = null;
    lastError = null;
    cancelInteraction();
    notifyListeners();
  }

  void undo() {
    if (!canUndo) return;
    _redoStack.add(_model);
    _redoLog.add(_log.removeLast());
    _model = _undoStack.removeLast();
    cancelInteraction();
    _dropStaleSelection();
    notifyListeners();
  }

  void redo() {
    if (!canRedo) return;
    _undoStack.add(_model);
    _log.add(_redoLog.removeLast());
    _model = _redoStack.removeLast();
    cancelInteraction();
    _dropStaleSelection();
    notifyListeners();
  }

  void clearError() {
    if (lastError == null) return;
    lastError = null;
    notifyListeners();
  }

  void _dropStaleSelection() {
    if (selectedWallId != null && !_model.walls.containsKey(selectedWallId)) selectedWallId = null;
    if (selectedNodeId != null && !_model.nodes.containsKey(selectedNodeId)) selectedNodeId = null;
  }

  // ---- tools ----

  void selectTool(PlanTool next) {
    tool = next;
    cancelInteraction();
    notifyListeners();
  }

  void toggleGrid() {
    snapToGrid = !snapToGrid;
    notifyListeners();
  }

  void deleteSelection() {
    final wallId = selectedWallId;
    final nodeId = selectedNodeId;
    if (wallId != null) {
      execute(DeleteWall(wallId));
    } else if (nodeId != null) {
      final walls = _model.wallsAt(nodeId).toList();
      if (walls.isNotEmpty) execute(CommandSequence('Delete corner', [for (final w in walls) DeleteWall(w.id)]));
    }
  }

  void setViewport(Viewport next, {bool byUser = true}) {
    viewport = next;
    if (byUser) viewportTouched = true;
    notifyListeners();
  }

  void fitToContent(Size size) {
    if (_model.nodes.isEmpty) {
      setViewport(Viewport(origin: Offset(size.width / 2, size.height / 2), pxPerMm: 0.1), byUser: false);
      return;
    }
    final xs = _model.nodes.values.map((n) => n.x);
    final ys = _model.nodes.values.map((n) => n.y);
    setViewport(Viewport.fit(
      Vec2(xs.reduce((a, b) => a < b ? a : b), ys.reduce((a, b) => a < b ? a : b)),
      Vec2(xs.reduce((a, b) => a > b ? a : b), ys.reduce((a, b) => a > b ? a : b)),
      size,
    ), byUser: false);
  }

  /// Ends whatever the pointer or the wall chain was doing, without changing the plan.
  void cancelInteraction() {
    draftStart = null;
    hover = null;
    dragNodeId = null;
    dragPosition = null;
    _startedThisPress = false;
    _movedThisPress = false;
    _pressPlanPoint = null;
  }

  // ---- pointer interaction (plan coordinates) ----

  SnapResult _snap(Vec2 raw, {Vec2? anchor, Set<String> exclude = const {}}) => snapEngine.snap(
        raw,
        model: _model,
        pxPerMm: viewport.pxPerMm,
        anchor: anchor,
        excludeNodes: exclude,
        useGrid: snapToGrid,
      );

  /// Pointer moved without a button pressed: shows where a wall would snap to.
  void hoverAt(Vec2 plan) {
    if (tool != PlanTool.wall) return;
    hover = _snap(plan, anchor: draftStart?.point);
    notifyListeners();
  }

  /// Returns true when the press landed on something the select tool can drag; false means "pan instead".
  bool pointerDown(Vec2 plan) {
    clearErrorSilently();
    _pressPlanPoint = plan;
    _movedThisPress = false;
    _startedThisPress = false;

    if (tool == PlanTool.wall) {
      final snapped = _snap(plan, anchor: draftStart?.point);
      hover = snapped;
      if (draftStart == null) {
        draftStart = snapped;
        _startedThisPress = true;
      } else {
        _commitWall(snapped);
      }
      notifyListeners();
      return true;
    }

    final hit = hitTest(_model, plan, viewport.pxPerMm);
    selectedNodeId = hit is NodeHit ? hit.nodeId : null;
    selectedWallId = hit is WallHit ? hit.wallId : null;
    dragNodeId = hit is NodeHit ? hit.nodeId : null;
    dragPosition = hit is NodeHit ? _model.pos(hit.nodeId) : null;
    notifyListeners();
    return hit != null;
  }

  void pointerMove(Vec2 plan) {
    final pressed = _pressPlanPoint;
    if (pressed != null && !_movedThisPress && (plan - pressed).length * viewport.pxPerMm > 6) _movedThisPress = true;

    if (tool == PlanTool.wall) {
      hover = _snap(plan, anchor: draftStart?.point);
    } else if (dragNodeId != null) {
      final snapped = _snap(plan, exclude: {dragNodeId!});
      dragPosition = snapped.point;
    }
    notifyListeners();
  }

  void pointerUp(Vec2 plan) {
    if (tool == PlanTool.wall) {
      // Press, drag, release draws one wall; a plain tap only sets the start of a chain.
      if (_startedThisPress && _movedThisPress && draftStart != null) {
        final end = _snap(plan, anchor: draftStart!.point);
        _commitWall(end, continueChain: false);
      }
    } else if (dragNodeId != null) {
      final nodeId = dragNodeId!;
      final target = dragPosition;
      if (target != null && _movedThisPress && target != _model.pos(nodeId)) execute(MoveNode(nodeId, target));
      dragNodeId = null;
      dragPosition = null;
    }
    _pressPlanPoint = null;
    _startedThisPress = false;
    _movedThisPress = false;
    notifyListeners();
  }

  void clearErrorSilently() => lastError = null;

  /// Builds and runs the wall from [draftStart] to [end]. Endpoints that land on the body of an existing wall
  /// first split that wall, so the new wall connects to a real corner (the server requires it).
  void _commitWall(SnapResult end, {bool continueChain = true}) {
    final start = draftStart!;
    if (start.point == end.point) return;

    final commands = <PlanCommand>[];
    var working = _model;

    (String, Vec2) endpoint(SnapResult s) {
      if (s.kind == SnapKind.node) return (s.nodeId!, s.point);
      if (s.kind == SnapKind.wall) {
        // Find the wall under the point in the model as it is now: an earlier split in this same step may have
        // cut the wall the snap originally pointed at.
        final (wall, t) = _wallUnder(working, s.point);
        final split = SplitWall(wallId: wall.id, newWallId: _ids(), newNodeId: _ids(), atMm: working.length(wall) * t);
        commands.add(split);
        working = split.apply(working);
        return (split.newNodeId, s.point);
      }
      return (_ids(), s.point);
    }

    final (startId, startPoint) = endpoint(start);
    final (endId, endPoint) = endpoint(end);
    commands.add(CreateWall(wallId: _ids(), startNodeId: startId, start: startPoint, endNodeId: endId, end: endPoint));

    final ok = execute(commands.length == 1 ? commands.single : CommandSequence('Add wall', commands));
    if (!ok) return;

    draftStart = continueChain ? SnapResult(endPoint, SnapKind.node, nodeId: endId) : null;
    hover = null;
  }

  static (PlanWall, double) _wallUnder(PlanModel model, Vec2 point) {
    PlanWall? best;
    var bestT = 0.0;
    var bestDistance = 1.0; // millimetres
    for (final wall in model.walls.values) {
      final closest = closestOnSegment(model.pos(wall.startNodeId), model.pos(wall.endNodeId), point);
      final distance = closest.point.distanceTo(point);
      if (distance < bestDistance && closest.t > 0 && closest.t < 1) {
        best = wall;
        bestT = closest.t;
        bestDistance = distance;
      }
    }
    if (best == null) throw const PlanException('WALL_NOT_FOUND', 'No wall under the snapped point.');
    return (best, bestT);
  }
}
