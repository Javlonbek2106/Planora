import 'dart:convert';
import 'dart:io';
import 'dart:ui' show Offset, Size;

import 'package:building_draft_app/geometry/model.dart';
import 'package:building_draft_app/geometry/vec2.dart';
import 'package:building_draft_app/state/plan_controller.dart';
import 'package:building_draft_app/state/viewport.dart';
import 'package:flutter_test/flutter_test.dart';

const floorId = 'f0000000-0000-4000-8000-000000000001';

/// Deterministic GUID-shaped ids, valid for the server too.
IdGenerator guidCounter() {
  var n = 0;
  return () => '00000000-0000-4000-8000-${(++n).toString().padLeft(12, '0')}';
}

PlanController newController() => PlanController(model: PlanModel(floorId: floorId), ids: guidCounter())
  ..viewport = const Viewport(pxPerMm: 0.1);

void tap(PlanController c, Vec2 p) {
  c.pointerDown(p);
  c.pointerUp(p);
}

void drag(PlanController c, Vec2 from, Vec2 to) {
  c.pointerDown(from);
  c.pointerMove(Vec2((from.x + to.x) / 2, (from.y + to.y) / 2));
  c.pointerMove(to);
  c.pointerUp(to);
}

void drawRectangle(PlanController c) {
  c.selectTool(PlanTool.wall);
  for (final p in const [Vec2(0, 0), Vec2(4000, 0), Vec2(4000, 3000), Vec2(0, 3000), Vec2(0, 0)]) {
    tap(c, p);
  }
  c.cancelInteraction();
}

void main() {
  group('drawing walls', () {
    test('tapping corners draws a chain of walls and closing it reuses the first corner', () {
      final c = newController();

      drawRectangle(c);

      expect(c.model.walls, hasLength(4));
      expect(c.model.nodes, hasLength(4));
      expect(c.lastError, isNull);
    });

    test('press, drag and release draws one wall and does not start a chain', () {
      final c = newController()..selectTool(PlanTool.wall);

      drag(c, const Vec2(0, 0), const Vec2(3000, 0));

      expect(c.model.walls, hasLength(1));
      expect(c.draftStart, isNull);
    });

    test('a tap shorter than a drag only sets the start of the next wall', () {
      final c = newController()..selectTool(PlanTool.wall);

      tap(c, const Vec2(0, 0));

      expect(c.model.walls, isEmpty);
      expect(c.draftStart, isNotNull);
    });

    test('while drawing, points snap in line with the previous corner', () {
      final c = newController()..selectTool(PlanTool.wall);
      tap(c, const Vec2(0, 0));

      c.hoverAt(const Vec2(3010, 40));

      expect(c.hover!.point, const Vec2(3000, 0));
    });

    test('ending a wall on another wall splits it, so the junction is a real corner', () {
      final c = newController();
      drawRectangle(c);
      c.selectTool(PlanTool.wall);

      tap(c, const Vec2(2000, 0));
      tap(c, const Vec2(2000, 3000));
      c.cancelInteraction();

      expect(c.model.walls, hasLength(7)); // 4 + 2 splits + 1 new wall
      expect(c.model.nodes.values.where((n) => n.x == 2000), hasLength(2));
      expect(c.lastError, isNull);
      // One undo reverts the splits and the wall together.
      c.undo();
      expect(c.model.walls, hasLength(4));
    });

    test('a wall that would cross another is refused and the plan stays as it was', () {
      final c = newController();
      drawRectangle(c);
      c.selectTool(PlanTool.wall);
      final before = jsonEncode(c.model.toJson());

      drag(c, const Vec2(-1000, 1500), const Vec2(5000, 1500));

      expect(c.lastError, isNotNull);
      expect(jsonEncode(c.model.toJson()), before);
      expect(c.canUndo, isTrue); // only the rectangle's walls are in the history
    });
  });

  group('selecting and editing', () {
    test('tapping a wall selects it; tapping empty space clears the selection', () {
      final c = newController();
      drawRectangle(c);
      c.selectTool(PlanTool.select);

      tap(c, const Vec2(2000, 0));
      expect(c.selectedWallId, isNotNull);

      tap(c, const Vec2(2000, 1500));
      expect(c.selectedWallId, isNull);
    });

    test('dragging a corner moves it, and one undo puts it back', () {
      final c = newController();
      drawRectangle(c);
      c.selectTool(PlanTool.select);

      drag(c, const Vec2(4000, 3000), const Vec2(4500, 3500));

      final moved = c.model.nodes.values.singleWhere((n) => n.x == 4500 && n.y == 3500);
      expect(moved, isNotNull);
      c.undo();
      expect(c.model.nodes.values.any((n) => n.x == 4000 && n.y == 3000), isTrue);
    });

    test('a corner cannot be dragged so that walls cross', () {
      final c = newController();
      drawRectangle(c);
      c.selectTool(PlanTool.select);
      final before = jsonEncode(c.model.toJson());

      drag(c, const Vec2(4000, 3000), const Vec2(4000, -1000));

      expect(c.lastError, contains('cross'));
      expect(jsonEncode(c.model.toJson()), before);
    });

    test('a corner dragged onto nothing but a tiny wobble is not a move', () {
      final c = newController();
      drawRectangle(c);
      c.selectTool(PlanTool.select);
      final undoDepth = c.serverCommands.length;

      tap(c, const Vec2(4000, 3000));

      expect(c.serverCommands, hasLength(undoDepth));
    });

    test('deleting the selected wall removes it; deleting a corner removes its walls', () {
      final c = newController();
      drawRectangle(c);
      c.selectTool(PlanTool.select);

      tap(c, const Vec2(2000, 0));
      c.deleteSelection();
      expect(c.model.walls, hasLength(3));

      tap(c, const Vec2(4000, 3000));
      c.deleteSelection();
      expect(c.model.walls, hasLength(1));
    });
  });

  group('history', () {
    test('undo and redo walk back and forth through every step', () {
      final c = newController();
      drawRectangle(c);
      final steps = <String>[jsonEncode(c.model.toJson())];

      for (var i = 0; i < 4; i++) {
        c.undo();
        steps.add(jsonEncode(c.model.toJson()));
      }
      expect(c.model.walls, isEmpty);
      expect(c.canUndo, isFalse);

      for (var i = 0; i < 4; i++) {
        c.redo();
      }
      expect(jsonEncode(c.model.toJson()), steps.first);
      expect(c.canRedo, isFalse);
    });

    test('a new edit after an undo discards the redo history', () {
      final c = newController();
      drawRectangle(c);
      c.undo();
      expect(c.canRedo, isTrue);

      c.selectTool(PlanTool.wall);
      drag(c, const Vec2(8000, 0), const Vec2(9000, 0));

      expect(c.canRedo, isFalse);
    });

    test('the server command log follows undo and redo', () {
      final c = newController();
      drawRectangle(c);
      expect(c.serverCommands, hasLength(4));

      c.undo();
      expect(c.serverCommands, hasLength(3));
      c.redo();
      expect(c.serverCommands, hasLength(4));
    });
  });

  group('viewport', () {
    test('screen and plan coordinates convert back and forth', () {
      const v = Viewport(origin: Offset(100, 400), pxPerMm: 0.2);

      expect(v.toScreen(const Vec2(1000, 500)), const Offset(300, 300));
      expect(v.toPlan(const Offset(300, 300)), const Vec2(1000, 500));
    });

    test('zooming keeps the point under the pointer where it is', () {
      const v = Viewport(origin: Offset(100, 400), pxPerMm: 0.2);
      const focal = Offset(250, 250);
      final plan = v.toPlan(focal);

      final zoomed = v.zoomAt(focal, 2.5);

      expect(zoomed.pxPerMm, closeTo(0.5, 1e-9));
      final after = zoomed.toPlan(focal);
      expect(after.x, closeTo(plan.x, 1e-6));
      expect(after.y, closeTo(plan.y, 1e-6));
    });

    test('zoom is limited', () {
      const v = Viewport(pxPerMm: 1.9);

      expect(v.zoomAt(Offset.zero, 100).pxPerMm, Viewport.maxScale);
      expect(v.zoomAt(Offset.zero, 1e-9).pxPerMm, Viewport.minScale);
    });

    test('fit puts the whole plan inside the screen', () {
      final c = newController();
      drawRectangle(c);

      c.fitToContent(const Size(800, 600));

      final topRight = c.viewport.toScreen(const Vec2(4000, 3000));
      final bottomLeft = c.viewport.toScreen(const Vec2(0, 0));
      expect(topRight.dx, lessThanOrEqualTo(800));
      expect(bottomLeft.dx, greaterThanOrEqualTo(0));
      expect(topRight.dy, greaterThanOrEqualTo(0));
      expect(bottomLeft.dy, lessThanOrEqualTo(600));
    });
  });

  group('contract with the server', () {
    // A realistic drawing session. Its commands are the golden file the C# test replays against the real
    // geometry pipeline (tests/BuildingDraft.Domain.Tests/ClientContractTests.cs).
    List<Map<String, Object?>> session() {
      final c = newController();
      drawRectangle(c);

      c.selectTool(PlanTool.wall);
      tap(c, const Vec2(2000, 0));
      tap(c, const Vec2(2000, 3000));
      c.cancelInteraction();

      c.selectTool(PlanTool.select);
      drag(c, const Vec2(4000, 3000), const Vec2(4200, 3200));

      tap(c, const Vec2(2000, 1500));
      c.deleteSelection();
      return c.serverCommands;
    }

    test('the session matches the golden file (regenerate with --dart-define=UPDATE_GOLDEN=true)', () {
      final golden = File('../contracts/client_session.json');
      final actual = const JsonEncoder.withIndent('  ').convert(session());

      if (const bool.fromEnvironment('UPDATE_GOLDEN')) {
        golden.createSync(recursive: true);
        golden.writeAsStringSync('$actual\n');
      }

      expect(golden.existsSync(), isTrue, reason: 'golden file is missing; run once with --dart-define=UPDATE_GOLDEN=true');
      expect(jsonDecode(golden.readAsStringSync()), jsonDecode(actual));
    });

    test('the session uses every command type the mobile app produces', () {
      final types = session().map((c) => c['commandType']).toSet();

      expect(types, {'create_wall', 'split_wall', 'move_node', 'delete_wall'});
    });
  });
}
