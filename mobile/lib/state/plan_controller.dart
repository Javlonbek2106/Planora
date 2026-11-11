import 'dart:ui' show Offset, Size;

import 'package:flutter/foundation.dart';

import '../geometry/commands.dart';
import '../geometry/hit_test.dart';
import '../geometry/model.dart';
import '../geometry/rooms.dart';
import '../geometry/snap.dart';
import '../geometry/validation.dart';
import '../geometry/vec2.dart';
import 'viewport.dart';

enum PlanTool { select, wall, room, door, window }

/// Where a door or window would go if the pointer were pressed now.
class OpeningPreview {
  const OpeningPreview({required this.wallId, required this.offsetMm, required this.widthMm, required this.kind});

  final String wallId;
  final double offsetMm;
  final double widthMm;
  final OpeningKind kind;
}

/// Sizes a new door or window starts with (the user can change them later).
({double width, double height, double sill}) defaultOpeningSize(OpeningKind kind) =>
    kind == OpeningKind.door ? (width: 900, height: 2100, sill: 0) : (width: 1200, height: 1200, sill: 900);

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

  /// Called after an edit was applied, undone or redone, so storage and sync can follow the history.
  void Function(PlanCommand command)? onExecuted;
  void Function(PlanCommand command)? onUndone;
  void Function(PlanCommand command)? onRedone;

  /// While true (during a sync) edits are refused, so the plan cannot change under the synchronization.
  bool editingLocked = false;

  PlanTool tool = PlanTool.select;
  Viewport viewport = const Viewport();
  bool snapToGrid = true;

  /// True once the user panned or zoomed; until then the plan is re-fitted when the screen size changes.
  bool viewportTouched = false;

  String? selectedWallId;
  String? selectedNodeId;
  String? selectedRoomId;
  String? selectedOpeningId;
  String? lastError;

  /// Room tool: a closed area the user tapped that is not a room yet. The screen asks for a name, then calls
  /// [createRoom] (or [cancelRoomProposal]).
  PlanFace? roomProposal;

  /// Door/window tools: the opening that would be placed at the pointer.
  OpeningPreview? openingPreview;

  // Select tool: an opening being slid along its wall.
  String? dragOpeningId;
  double? dragOpeningOffset;
  double _dragOpeningGrab = 0;

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
    if (editingLocked) {
      lastError = 'Synchronizing: try again in a moment.';
      notifyListeners();
      return false;
    }
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
      onExecuted?.call(command);
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
    selectedRoomId = null;
    selectedOpeningId = null;
    lastError = null;
    cancelInteraction();
    notifyListeners();
  }

  /// Forgets undo/redo history but keeps the plan: used once edits are on the server, because taking back something
  /// the server already has would need an inverse command.
  void clearHistory() {
    _undoStack.clear();
    _redoStack.clear();
    _log.clear();
    _redoLog.clear();
    notifyListeners();
  }

  void undo() {
    if (!canUndo || editingLocked) return;
    _redoStack.add(_model);
    final undone = _log.removeLast();
    _redoLog.add(undone);
    _model = _undoStack.removeLast();
    cancelInteraction();
    _dropStaleSelection();
    onUndone?.call(undone);
    notifyListeners();
  }

  void redo() {
    if (!canRedo || editingLocked) return;
    _undoStack.add(_model);
    final redone = _redoLog.removeLast();
    _log.add(redone);
    _model = _redoStack.removeLast();
    cancelInteraction();
    _dropStaleSelection();
    onRedone?.call(redone);
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
    if (selectedRoomId != null && !_model.rooms.containsKey(selectedRoomId)) selectedRoomId = null;
    if (selectedOpeningId != null && !_model.openings.containsKey(selectedOpeningId)) selectedOpeningId = null;
  }

  // ---- tools ----

  void selectTool(PlanTool next) {
    tool = next;
    cancelInteraction();
    if (next != PlanTool.select) {
      selectedWallId = selectedNodeId = selectedRoomId = selectedOpeningId = null;
    }
    notifyListeners();
  }

  void toggleGrid() {
    snapToGrid = !snapToGrid;
    notifyListeners();
  }

  /// True when something is selected that Delete can remove.
  bool get hasSelection => selectedWallId != null || selectedNodeId != null || selectedRoomId != null || selectedOpeningId != null;

  void deleteSelection() {
    final wallId = selectedWallId;
    final nodeId = selectedNodeId;
    if (selectedOpeningId != null) {
      execute(RemoveOpening(selectedOpeningId!));
    } else if (selectedRoomId != null) {
      execute(DeleteRoom(selectedRoomId!));
    } else if (wallId != null) {
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
    roomProposal = null;
    openingPreview = null;
    dragOpeningId = null;
    dragOpeningOffset = null;
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
    if (tool == PlanTool.door || tool == PlanTool.window) {
      openingPreview = _openingAt(plan, tool == PlanTool.door ? OpeningKind.door : OpeningKind.window);
      notifyListeners();
      return;
    }
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

    if (tool == PlanTool.room) {
      _proposeRoom(plan);
      notifyListeners();
      return true;
    }
    if (tool == PlanTool.door || tool == PlanTool.window) {
      _placeOpening(plan, tool == PlanTool.door ? OpeningKind.door : OpeningKind.window);
      notifyListeners();
      return true;
    }

    final hit = hitTest(_model, plan, viewport.pxPerMm);
    selectedNodeId = hit is NodeHit ? hit.nodeId : null;
    selectedWallId = hit is WallHit ? hit.wallId : null;
    selectedOpeningId = hit is OpeningHit ? hit.openingId : null;
    selectedRoomId = hit is RoomHit ? hit.roomId : null;
    dragNodeId = hit is NodeHit ? hit.nodeId : null;
    dragPosition = hit is NodeHit ? _model.pos(hit.nodeId) : null;

    if (hit is OpeningHit) {
      final opening = _model.openings[hit.openingId]!;
      dragOpeningId = opening.id;
      dragOpeningOffset = opening.offsetMm;
      _dragOpeningGrab = _along(opening.wallId, plan) - opening.offsetMm;
    }
    notifyListeners();
    return hit != null && hit is! RoomHit; // pressing a room's empty inside pans, like empty space
  }

  void pointerMove(Vec2 plan) {
    final pressed = _pressPlanPoint;
    if (pressed != null && !_movedThisPress && (plan - pressed).length * viewport.pxPerMm > 6) _movedThisPress = true;

    if (tool == PlanTool.wall) {
      hover = _snap(plan, anchor: draftStart?.point);
    } else if (tool == PlanTool.door || tool == PlanTool.window) {
      openingPreview = _openingAt(plan, tool == PlanTool.door ? OpeningKind.door : OpeningKind.window);
    } else if (dragOpeningId != null) {
      final opening = _model.openings[dragOpeningId]!;
      final length = _model.length(_model.walls[opening.wallId]!);
      final raw = _along(opening.wallId, plan) - _dragOpeningGrab;
      dragOpeningOffset = ((raw / 50).roundToDouble() * 50).clamp(0.0, length - opening.widthMm);
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
    } else if (dragOpeningId != null) {
      final opening = _model.openings[dragOpeningId]!;
      final target = dragOpeningOffset;
      if (target != null && _movedThisPress && target != opening.offsetMm) {
        execute(CommandSequence('Move ${opening.kind.name}', [
          RemoveOpening(opening.id),
          AttachOpening(
            openingId: opening.id,
            wallId: opening.wallId,
            kind: opening.kind,
            offsetMm: target,
            widthMm: opening.widthMm,
            heightMm: opening.heightMm,
            sillMm: opening.sillMm,
          ),
        ]));
      }
      dragOpeningId = null;
      dragOpeningOffset = null;
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

  // ---- rooms ----

  void _proposeRoom(Vec2 plan) {
    final face = faceAt(_model, plan);
    if (face == null) {
      lastError = 'No closed area here. Close a loop of walls first.';
      return;
    }

    final walls = face.wallIds.toSet();
    for (final room in _model.rooms.values) {
      if (room.wallIds.toSet().length == walls.length && room.wallIds.toSet().containsAll(walls)) {
        selectedRoomId = room.id;
        lastError = '"${room.name}" is already a room.';
        return;
      }
    }
    roomProposal = face;
  }

  /// Makes the proposed area a room. Returns false (with [lastError]) if the plan refuses it.
  bool createRoom(String name, {String roomType = 'other', RoomAreaType areaType = RoomAreaType.enclosed}) {
    final face = roomProposal;
    if (face == null) return false;
    final ok = execute(CreateRoom(roomId: _ids(), name: name, wallIds: face.wallIds, roomType: roomType, areaType: areaType));
    if (ok) roomProposal = null;
    return ok;
  }

  void cancelRoomProposal() {
    roomProposal = null;
    notifyListeners();
  }

  // ---- doors and windows ----

  ({String wallId, double along})? _wallNear(Vec2 plan) {
    final tolerance = 14 / viewport.pxPerMm;
    String? best;
    var bestDistance = double.infinity;
    for (final wall in _model.walls.values) {
      final reach = wall.thicknessMm / 2 > tolerance ? wall.thicknessMm / 2 : tolerance;
      final distance = closestOnSegment(_model.pos(wall.startNodeId), _model.pos(wall.endNodeId), plan).point.distanceTo(plan);
      if (distance <= reach && distance < bestDistance) {
        bestDistance = distance;
        best = wall.id;
      }
    }
    return best == null ? null : (wallId: best, along: _along(best, plan));
  }

  /// Distance of [plan] along a wall from its start, in mm.
  double _along(String wallId, Vec2 plan) {
    final wall = _model.walls[wallId]!;
    final start = _model.pos(wall.startNodeId);
    final end = _model.pos(wall.endNodeId);
    return closestOnSegment(start, end, plan).t * start.distanceTo(end);
  }

  OpeningPreview? _openingAt(Vec2 plan, OpeningKind kind) {
    final near = _wallNear(plan);
    if (near == null) return null;
    final size = defaultOpeningSize(kind);
    final length = _model.length(_model.walls[near.wallId]!);
    if (length < size.width) return null;
    final offset = ((near.along - size.width / 2) / 50).roundToDouble() * 50;
    return OpeningPreview(wallId: near.wallId, offsetMm: offset.clamp(0.0, length - size.width), widthMm: size.width, kind: kind);
  }

  void _placeOpening(Vec2 plan, OpeningKind kind) {
    final noun = kind == OpeningKind.door ? 'door' : 'window';
    final near = _wallNear(plan);
    if (near == null) {
      lastError = 'Tap a wall to place a $noun.';
      return;
    }
    final preview = _openingAt(plan, kind);
    if (preview == null) {
      lastError = 'This wall is too short for a $noun.';
      return;
    }

    final size = defaultOpeningSize(kind);
    final ok = execute(AttachOpening(
      openingId: _ids(),
      wallId: preview.wallId,
      kind: kind,
      offsetMm: preview.offsetMm,
      widthMm: size.width,
      heightMm: size.height,
      sillMm: size.sill,
    ));
    if (ok) openingPreview = null;
  }

  // ---- measurements ----

  /// Fixes the selected wall to a measured length ([inner] = between the wall faces, otherwise centerline).
  bool applyMeasurement(double valueMm, {bool inner = true}) {
    final wallId = selectedWallId;
    if (wallId == null) {
      lastError = 'Select a wall to measure first.';
      notifyListeners();
      return false;
    }
    return execute(ApplyMeasurement(wallId: wallId, valueMm: valueMm, inner: inner));
  }
}
