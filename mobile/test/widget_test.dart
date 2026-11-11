import 'package:building_draft_app/demo/demo_plans.dart';
import 'package:building_draft_app/geometry/model.dart';
import 'package:building_draft_app/geometry/vec2.dart';
import 'package:building_draft_app/state/plan_controller.dart';
import 'package:building_draft_app/ui/home_page.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

Future<PlanController> pumpApp(WidgetTester tester, {Size size = const Size(1100, 800), PlanController? controller}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);

  final c = controller ?? PlanController(model: PlanModel(floorId: 'floor-1'));
  await tester.pumpWidget(MaterialApp(home: HomePage(controller: c)));
  await tester.pumpAndSettle();
  return c;
}

Finder canvas() => find.byKey(const ValueKey('plan-canvas'));

/// A point on the canvas, [dx] and [dy] pixels from its centre (plan origin after the first fit).
Offset canvasPoint(WidgetTester tester, {double dx = 0, double dy = 0}) => tester.getCenter(canvas()) + Offset(dx, dy);

void main() {
  group('layout', () {
    testWidgets('a wide screen shows the tool rail and the properties panel', (tester) async {
      await pumpApp(tester, size: const Size(1100, 800));

      expect(find.byKey(const ValueKey('layout-wide')), findsOneWidget);
      expect(find.byKey(const ValueKey('properties-panel')), findsOneWidget);
      expect(find.byKey(const ValueKey('layout-narrow')), findsNothing);
    });

    testWidgets('a phone shows the canvas with a bottom toolbar and no side panel', (tester) async {
      await pumpApp(tester, size: const Size(390, 800));

      expect(find.byKey(const ValueKey('layout-narrow')), findsOneWidget);
      expect(find.byKey(const ValueKey('properties-panel')), findsNothing);
      final toolBottom = tester.getBottomLeft(find.byKey(const ValueKey('tool-wall'))).dy;
      expect(toolBottom, greaterThan(tester.getBottomLeft(canvas()).dy - 1), reason: 'toolbar sits below the canvas');
    });
  });

  group('drawing with touch', () {
    testWidgets('dragging with the wall tool draws a wall of the dragged length', (tester) async {
      final c = await pumpApp(tester);
      await tester.tap(find.byKey(const ValueKey('tool-wall')));
      await tester.pump();

      // 200 px at the default 0.1 px/mm is 2000 mm.
      await tester.dragFrom(canvasPoint(tester), const Offset(200, 0));
      await tester.pump();

      expect(c.model.walls, hasLength(1));
      expect(c.model.length(c.model.walls.values.single), closeTo(2000, 1));
      expect(find.byKey(const ValueKey('prop-summary')), findsOneWidget);
      expect(find.text('1 walls, 2 corners'), findsOneWidget);
    });

    testWidgets('undo and redo buttons follow the history', (tester) async {
      final c = await pumpApp(tester);
      await tester.tap(find.byKey(const ValueKey('tool-wall')));
      await tester.pump();
      await tester.dragFrom(canvasPoint(tester), const Offset(200, 0));
      await tester.pump();

      await tester.tap(find.byKey(const ValueKey('action-undo')));
      await tester.pump();
      expect(c.model.walls, isEmpty);

      await tester.tap(find.byKey(const ValueKey('action-redo')));
      await tester.pump();
      expect(c.model.walls, hasLength(1));
    });

    testWidgets('a wall that would cross another shows the reason instead of changing the plan', (tester) async {
      final c = await pumpApp(tester);
      await tester.tap(find.byKey(const ValueKey('tool-wall')));
      await tester.pump();
      await tester.dragFrom(canvasPoint(tester, dx: -150), const Offset(300, 0));
      await tester.pump();

      await tester.dragFrom(canvasPoint(tester, dx: 0, dy: -100), const Offset(0, 200));
      await tester.pump();

      expect(c.model.walls, hasLength(1));
      expect(find.byKey(const ValueKey('error-banner')), findsOneWidget);
      expect(find.textContaining('cross'), findsOneWidget);

      await tester.tap(find.descendant(of: find.byKey(const ValueKey('error-banner')), matching: find.byIcon(Icons.close)));
      await tester.pump();
      expect(find.byKey(const ValueKey('error-banner')), findsNothing);
    });

    testWidgets('selecting a wall shows its length in the properties panel and Delete removes it', (tester) async {
      final c = await pumpApp(tester);
      await tester.tap(find.byKey(const ValueKey('tool-wall')));
      await tester.pump();
      await tester.dragFrom(canvasPoint(tester, dx: -100), const Offset(200, 0));
      await tester.pump();

      await tester.tap(find.byKey(const ValueKey('tool-select')));
      await tester.pump();
      await tester.tapAt(canvasPoint(tester));
      await tester.pump();

      expect(c.selectedWallId, isNotNull);
      expect(find.text('Length: 2000 mm'), findsOneWidget);

      await tester.tap(find.byKey(const ValueKey('action-delete')));
      await tester.pump();
      expect(c.model.walls, isEmpty);
      expect(find.text('Nothing selected'), findsOneWidget);
    });

    testWidgets('dragging a corner with the select tool moves it', (tester) async {
      final c = await pumpApp(tester);
      await tester.tap(find.byKey(const ValueKey('tool-wall')));
      await tester.pump();
      await tester.dragFrom(canvasPoint(tester, dx: -100), const Offset(200, 0));
      await tester.pump();
      await tester.tap(find.byKey(const ValueKey('tool-select')));
      await tester.pump();

      await tester.dragFrom(canvasPoint(tester, dx: 100), const Offset(0, -100));
      await tester.pump();

      expect(c.model.nodes.values.any((n) => n.x == 1000 && n.y == 1000), isTrue);
    });
  });

  group('navigating', () {
    testWidgets('two fingers spreading apart zoom in', (tester) async {
      final c = await pumpApp(tester);
      final before = c.viewport.pxPerMm;
      final centre = canvasPoint(tester);

      final first = await tester.startGesture(centre - const Offset(50, 0), pointer: 1);
      final second = await tester.startGesture(centre + const Offset(50, 0), pointer: 2);
      await first.moveBy(const Offset(-50, 0));
      await second.moveBy(const Offset(50, 0));
      await first.up();
      await second.up();
      await tester.pump();

      expect(c.viewport.pxPerMm, closeTo(before * 2, before * 0.01));
    });

    testWidgets('a pinch never draws anything, even with the wall tool', (tester) async {
      final c = await pumpApp(tester);
      await tester.tap(find.byKey(const ValueKey('tool-wall')));
      await tester.pump();
      final centre = canvasPoint(tester);

      final first = await tester.startGesture(centre - const Offset(50, 0), pointer: 1);
      final second = await tester.startGesture(centre + const Offset(50, 0), pointer: 2);
      await first.moveBy(const Offset(-50, 0));
      await second.moveBy(const Offset(50, 0));
      await first.up();
      await second.up();
      await tester.pump();

      expect(c.model.walls, isEmpty);
      expect(c.draftStart, isNull);
    });

    testWidgets('the mouse wheel zooms around the pointer', (tester) async {
      final c = await pumpApp(tester);
      final before = c.viewport.pxPerMm;
      final mouse = TestPointer(1, PointerDeviceKind.mouse);

      await tester.sendEventToBinding(mouse.hover(canvasPoint(tester)));
      await tester.sendEventToBinding(mouse.scroll(const Offset(0, -300)));
      await tester.pump();

      expect(c.viewport.pxPerMm, greaterThan(before));
    });

    testWidgets('dragging empty space with the select tool pans the plan', (tester) async {
      final c = await pumpApp(tester);
      final before = c.viewport.origin;

      await tester.dragFrom(canvasPoint(tester), const Offset(80, 40));
      await tester.pump();

      expect(c.viewport.origin.dx, closeTo(before.dx + 80, 1));
      expect(c.viewport.origin.dy, closeTo(before.dy + 40, 1));
      expect(c.model.walls, isEmpty);
    });
  });

  group('demo plans', () {
    testWidgets('the stress plan loads 220 walls and fits them on screen', (tester) async {
      final c = await pumpApp(tester);

      await tester.tap(find.byKey(const ValueKey('action-demo')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Stress: 220 walls'));
      await tester.pumpAndSettle();

      expect(c.model.walls, hasLength(220));
      expect(find.text('220 walls, 121 corners'), findsOneWidget);
      expect(c.canUndo, isFalse, reason: 'loading a plan starts a fresh history');
    });
  });

  group('screen size changes', () {
    bool planFitsCanvas(WidgetTester tester, PlanController c) {
      final area = tester.getRect(canvas());
      final corners = [const Vec2(0, 0), const Vec2(8200, 4200)].map((p) => c.viewport.toScreen(p));
      return corners.every((p) => p.dx >= 0 && p.dx <= area.width && p.dy >= 0 && p.dy <= area.height);
    }

    testWidgets('rotating the device re-fits the plan while the user has not moved the view', (tester) async {
      final c = await pumpApp(tester, size: const Size(900, 500), controller: PlanController(model: DemoPlans.apartment()));
      expect(planFitsCanvas(tester, c), isTrue);

      tester.view.physicalSize = const Size(500, 900);
      await tester.pumpAndSettle();

      expect(planFitsCanvas(tester, c), isTrue);
    });

    testWidgets('a view the user moved is left alone when the screen size changes', (tester) async {
      final c = await pumpApp(tester, size: const Size(900, 500), controller: PlanController(model: DemoPlans.apartment()));
      final mouse = TestPointer(1, PointerDeviceKind.mouse);
      await tester.sendEventToBinding(mouse.hover(canvasPoint(tester)));
      await tester.sendEventToBinding(mouse.scroll(const Offset(0, -300)));
      await tester.pump();
      final zoomed = c.viewport.pxPerMm;

      tester.view.physicalSize = const Size(500, 900);
      await tester.pumpAndSettle();

      expect(c.viewport.pxPerMm, zoomed);
      expect(c.viewportTouched, isTrue);
    });
  });
}
