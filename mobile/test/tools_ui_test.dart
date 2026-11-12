import 'package:building_draft_app/geometry/commands.dart';
import 'package:building_draft_app/geometry/model.dart';
import 'package:building_draft_app/geometry/vec2.dart';
import 'package:building_draft_app/state/plan_controller.dart';
import 'package:building_draft_app/ui/home_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

PlanController rectangleController() {
  var model = PlanModel(floorId: 'f');
  const corners = [Vec2(0, 0), Vec2(4000, 0), Vec2(4000, 3000), Vec2(0, 3000)];
  for (var i = 0; i < 4; i++) {
    final next = (i + 1) % 4;
    model = CreateWall(wallId: 'w$i', startNodeId: 'n$i', start: corners[i], endNodeId: 'n$next', end: corners[next]).apply(model);
  }
  return PlanController(model: model);
}

Future<PlanController> pumpApp(WidgetTester tester, {Size size = const Size(1100, 800), PlanController? controller}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);

  final c = controller ?? rectangleController();
  await tester.pumpWidget(MaterialApp(home: HomePage(controller: c)));
  await tester.pumpAndSettle();
  return c;
}

/// Where a plan point is on screen, with the plan fitted to the canvas.
Offset screenOf(WidgetTester tester, PlanController c, Vec2 plan) =>
    tester.getTopLeft(find.byKey(const ValueKey('plan-canvas'))) + c.viewport.toScreen(plan);

Future<void> useTool(WidgetTester tester, String key) async {
  await tester.tap(find.byKey(ValueKey(key)));
  await tester.pump();
}

Future<void> tapPlan(WidgetTester tester, PlanController c, Vec2 plan) async {
  await tester.tapAt(screenOf(tester, c, plan));
  await tester.pumpAndSettle();
}

void main() {
  group('rooms', () {
    testWidgets('tapping inside the loop with the room tool asks for a name and creates the room', (tester) async {
      final c = await pumpApp(tester);
      await useTool(tester, 'tool-room');

      await tapPlan(tester, c, const Vec2(2000, 1500));
      expect(find.byKey(const ValueKey('room-dialog')), findsOneWidget);

      await tester.enterText(find.byKey(const ValueKey('room-name')), 'Master bedroom');
      await tester.tap(find.byKey(const ValueKey('room-create')));
      await tester.pumpAndSettle();

      expect(c.model.rooms.values.single.name, 'Master bedroom');
      expect(find.byKey(const ValueKey('room-dialog')), findsNothing);
      expect(find.text('Enclosed area: 10.64 m²'), findsOneWidget);
      expect(find.text('Open areas: 0.00 m²'), findsOneWidget);
    });

    testWidgets('choosing a balcony counts it as an open area, apart from the enclosed area', (tester) async {
      final c = await pumpApp(tester);
      await useTool(tester, 'tool-room');
      await tapPlan(tester, c, const Vec2(2000, 1500));

      await tester.tap(find.byKey(const ValueKey('room-type')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Balcony (open area)').last);
      await tester.pumpAndSettle();
      expect(find.text('Counted separately from the enclosed area.'), findsOneWidget);
      await tester.tap(find.byKey(const ValueKey('room-create')));
      await tester.pumpAndSettle();

      expect(c.model.rooms.values.single.areaType, RoomAreaType.openArea);
      expect(find.text('Enclosed area: 0.00 m²'), findsOneWidget);
      expect(find.text('Open areas: 10.64 m²'), findsOneWidget);
    });

    testWidgets('cancelling the dialog creates nothing and the area can be tapped again', (tester) async {
      final c = await pumpApp(tester);
      await useTool(tester, 'tool-room');
      await tapPlan(tester, c, const Vec2(2000, 1500));

      await tester.tap(find.byKey(const ValueKey('room-cancel')));
      await tester.pumpAndSettle();
      expect(c.model.rooms, isEmpty);

      await tapPlan(tester, c, const Vec2(2000, 1500));
      expect(find.byKey(const ValueKey('room-dialog')), findsOneWidget);
    });

    testWidgets('an empty name keeps the dialog open', (tester) async {
      final c = await pumpApp(tester);
      await useTool(tester, 'tool-room');
      await tapPlan(tester, c, const Vec2(2000, 1500));

      await tester.enterText(find.byKey(const ValueKey('room-name')), '  ');
      await tester.tap(find.byKey(const ValueKey('room-create')));
      await tester.pumpAndSettle();

      expect(find.byKey(const ValueKey('room-dialog')), findsOneWidget);
      expect(c.model.rooms, isEmpty);
    });

    testWidgets('tapping outside any loop shows the reason', (tester) async {
      final c = await pumpApp(tester);
      await useTool(tester, 'tool-room');

      await tapPlan(tester, c, const Vec2(-1500, -1500));

      expect(find.byKey(const ValueKey('room-dialog')), findsNothing);
      expect(find.textContaining('closed loop'), findsWidgets);
    });

    testWidgets('selecting a room shows its name, area and perimeter', (tester) async {
      final c = await pumpApp(tester);
      await useTool(tester, 'tool-room');
      await tapPlan(tester, c, const Vec2(2000, 1500));
      await tester.enterText(find.byKey(const ValueKey('room-name')), 'Kitchen');
      await tester.tap(find.byKey(const ValueKey('room-create')));
      await tester.pumpAndSettle();

      await useTool(tester, 'tool-select');
      await tapPlan(tester, c, const Vec2(2000, 1500));

      expect(find.byKey(const ValueKey('prop-room-name')), findsOneWidget);
      expect(find.text('Area: 10.64 m²'), findsOneWidget);
      expect(find.text('Perimeter: 13.20 m'), findsOneWidget);
    });
  });

  group('doors, windows and measuring', () {
    testWidgets('placing a door shows it in the plan summary, selecting it shows its size', (tester) async {
      final c = await pumpApp(tester);
      await useTool(tester, 'tool-door');

      await tapPlan(tester, c, const Vec2(2000, 0));
      expect(find.text('0 rooms, 1 doors and windows'), findsOneWidget);

      await useTool(tester, 'tool-select');
      await tapPlan(tester, c, const Vec2(2000, 0));
      expect(find.text('Width: 900 mm'), findsOneWidget);
    });

    testWidgets('an overlapping door shows the reason in the banner', (tester) async {
      final c = await pumpApp(tester);
      await useTool(tester, 'tool-door');
      await tapPlan(tester, c, const Vec2(2000, 0));

      await tapPlan(tester, c, const Vec2(2300, 0));

      expect(find.byKey(const ValueKey('error-banner')), findsOneWidget);
      expect(find.textContaining('overlap'), findsOneWidget);
      expect(c.model.openings, hasLength(1));
    });

    testWidgets('Measure is only available with a wall selected', (tester) async {
      final c = await pumpApp(tester);

      expect(tester.widget<IconButton>(find.byKey(const ValueKey('action-measure'))).onPressed, isNull);

      await tapPlan(tester, c, const Vec2(2000, 0));
      expect(tester.widget<IconButton>(find.byKey(const ValueKey('action-measure'))).onPressed, isNotNull);
    });

    testWidgets('the measure dialog previews the result and applies a laser value between the wall faces', (tester) async {
      final c = await pumpApp(tester);
      await tapPlan(tester, c, const Vec2(2000, 0));

      await tester.tap(find.byKey(const ValueKey('action-measure')));
      await tester.pumpAndSettle();
      expect(find.textContaining('3800 mm between the wall faces'), findsOneWidget);

      await tester.enterText(find.byKey(const ValueKey('measure-value')), '3900');
      await tester.pump();
      expect(find.textContaining('set to 4100 mm along its centerline'), findsOneWidget);

      await tester.tap(find.byKey(const ValueKey('measure-apply')));
      await tester.pumpAndSettle();

      expect(c.model.fixedLengths['w0'], 4100);
      expect(find.byKey(const ValueKey('prop-fixed')), findsOneWidget);
    });

    testWidgets('a centerline reading is used as typed', (tester) async {
      final c = await pumpApp(tester);
      await tapPlan(tester, c, const Vec2(2000, 0));
      await tester.tap(find.byKey(const ValueKey('action-measure')));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Centerline'));
      await tester.enterText(find.byKey(const ValueKey('measure-value')), '4250');
      await tester.pump();
      await tester.tap(find.byKey(const ValueKey('measure-apply')));
      await tester.pumpAndSettle();

      expect(c.model.fixedLengths['w0'], 4250);
    });

    testWidgets('the measurement is recorded with the chosen source, a note and a confirmation, and shown in the wall history', (tester) async {
      final c = await pumpApp(tester);
      await tapPlan(tester, c, const Vec2(2000, 0));
      await tester.tap(find.byKey(const ValueKey('action-measure')));
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const ValueKey('measure-source')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Tape / manual').last);
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const ValueKey('measure-notes')), 'north wall, tape');
      await tester.tap(find.byKey(const ValueKey('measure-verified')));
      await tester.pump();
      await tester.tap(find.byKey(const ValueKey('measure-apply')));
      await tester.pumpAndSettle();

      final entry = c.model.measurements.values.single;
      expect((entry.source, entry.status, entry.notes), (MeasurementSource.manual, MeasurementStatus.verified, 'north wall, tape'));
      expect(find.byKey(const ValueKey('measurement-history')), findsOneWidget);
      expect(find.textContaining('Tape / manual'), findsWidgets);
      expect(find.textContaining('verified'), findsWidgets);
      expect(find.textContaining('north wall, tape'), findsOneWidget);
    });

    testWidgets('by default a measurement is a typed-in laser value that is not yet verified', (tester) async {
      final c = await pumpApp(tester);
      await tapPlan(tester, c, const Vec2(2000, 0));
      await tester.tap(find.byKey(const ValueKey('action-measure')));
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const ValueKey('measure-apply')));
      await tester.pumpAndSettle();

      final entry = c.model.measurements.values.single;
      expect((entry.source, entry.status), (MeasurementSource.laserManual, MeasurementStatus.unverified));
    });

    testWidgets('measuring again keeps the old entry in the history, struck through', (tester) async {
      final c = await pumpApp(tester);
      for (var i = 0; i < 2; i++) {
        await tapPlan(tester, c, const Vec2(2000, 0));
        await tester.tap(find.byKey(const ValueKey('action-measure')));
        await tester.pumpAndSettle();
        await tester.enterText(find.byKey(const ValueKey('measure-value')), i == 0 ? '3800' : '3850');
        await tester.pump();
        await tester.tap(find.byKey(const ValueKey('measure-apply')));
        await tester.pumpAndSettle();
      }

      expect(c.model.measurementsOf('w0'), hasLength(2));
      expect(find.textContaining('superseded'), findsOneWidget);
      final struck = tester.widgetList<Text>(find.byType(Text)).where((t) => t.style?.decoration == TextDecoration.lineThrough);
      expect(struck, hasLength(1));
    });

    testWidgets('selecting a door turns Measure into Edit size, and the dialog changes the door', (tester) async {
      final c = await pumpApp(tester);
      await useTool(tester, 'tool-door');
      await tapPlan(tester, c, const Vec2(1000, 0));
      await useTool(tester, 'tool-select');
      expect(find.byKey(const ValueKey('action-resize-opening')), findsNothing);

      await tapPlan(tester, c, const Vec2(1000, 0));
      expect(find.byKey(const ValueKey('action-measure')), findsNothing);
      await tester.tap(find.byKey(const ValueKey('action-resize-opening')));
      await tester.pumpAndSettle();
      expect(find.byKey(const ValueKey('opening-sill')), findsNothing, reason: 'a door has no sill');

      await tester.enterText(find.byKey(const ValueKey('opening-width')), '1000');
      await tester.pump();
      await tester.tap(find.byKey(const ValueKey('opening-apply')));
      await tester.pumpAndSettle();

      expect(c.model.openings.values.single.widthMm, 1000);
      expect(find.byKey(const ValueKey('opening-dialog')), findsNothing);
      expect(find.byKey(const ValueKey('prop-opening-width')), findsOneWidget);
      expect(find.text('Width: 1000 mm'), findsOneWidget);
    });

    testWidgets('a size that does not fit keeps the dialog open and says why', (tester) async {
      final c = await pumpApp(tester);
      await useTool(tester, 'tool-window');
      await tapPlan(tester, c, const Vec2(1000, 0));
      await useTool(tester, 'tool-select');
      await tapPlan(tester, c, const Vec2(1000, 0));
      await tester.tap(find.byKey(const ValueKey('action-resize-opening')));
      await tester.pumpAndSettle();
      expect(find.byKey(const ValueKey('opening-sill')), findsOneWidget);

      await tester.enterText(find.byKey(const ValueKey('opening-width')), '9000');
      await tester.pump();
      await tester.tap(find.byKey(const ValueKey('opening-apply')));
      await tester.pumpAndSettle();

      expect(find.byKey(const ValueKey('opening-dialog')), findsOneWidget);
      expect(find.byKey(const ValueKey('opening-error')), findsOneWidget);
      expect(c.model.openings.values.single.widthMm, 1200);
    });

    testWidgets('the apply button waits for a valid number', (tester) async {
      final c = await pumpApp(tester);
      await tapPlan(tester, c, const Vec2(2000, 0));
      await tester.tap(find.byKey(const ValueKey('action-measure')));
      await tester.pumpAndSettle();

      await tester.enterText(find.byKey(const ValueKey('measure-value')), 'abc');
      await tester.pump();

      expect(tester.widget<FilledButton>(find.byKey(const ValueKey('measure-apply'))).onPressed, isNull);
    });
  });

  group('on a phone', () {
    testWidgets('the seven tools scroll in the bottom bar without overflowing', (tester) async {
      await pumpApp(tester, size: const Size(390, 800));

      expect(tester.takeException(), isNull);
      expect(find.byKey(const ValueKey('tool-select')), findsOneWidget);

      await tester.drag(find.byKey(const ValueKey('tool-select')), const Offset(-400, 0));
      await tester.pumpAndSettle();

      expect(find.byKey(const ValueKey('action-delete')).hitTestable(), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('a room can be made on a phone too', (tester) async {
      final c = await pumpApp(tester, size: const Size(390, 800));
      await useTool(tester, 'tool-room');

      await tapPlan(tester, c, const Vec2(2000, 1500));

      expect(find.byKey(const ValueKey('room-dialog')), findsOneWidget);
    });
  });
}
