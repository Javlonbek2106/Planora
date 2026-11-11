import 'package:building_draft_app/app/app_controller.dart';
import 'package:building_draft_app/geometry/commands.dart';
import 'package:building_draft_app/geometry/model.dart';
import 'package:building_draft_app/geometry/vec2.dart';
import 'package:building_draft_app/state/plan_controller.dart';
import 'package:building_draft_app/sync/local_store.dart';
import 'package:building_draft_app/sync/models.dart';
import 'package:building_draft_app/ui/home_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support/fake_server.dart';

class Setup {
  Setup(this.server, this.app, this.editor, this.store);

  final FakeServer server;
  final AppController app;
  final PlanController editor;
  final LocalStore store;
}

Future<Setup> pumpApp(WidgetTester tester) async {
  tester.view.physicalSize = const Size(1100, 800);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);

  final server = FakeServer();
  final store = MemoryLocalStore();
  final editor = PlanController(model: PlanModel(floorId: 'scratch'));
  final app = AppController(store: store, editor: editor, clientFactory: () => server.client);
  await tester.pumpWidget(MaterialApp(home: HomePage(app: app)));
  await tester.pumpAndSettle();
  return Setup(server, app, editor, store);
}

Future<void> connect(WidgetTester tester) async {
  await tester.tap(find.byKey(const ValueKey('action-sync')));
  await tester.pumpAndSettle();
  await tester.tap(find.byKey(const ValueKey('connect-submit')));
  await tester.pumpAndSettle();
}

Future<void> drawWall(WidgetTester tester, {double dy = 0}) async {
  await tester.tap(find.byKey(const ValueKey('tool-wall')));
  await tester.pump();
  final centre = tester.getCenter(find.byKey(const ValueKey('plan-canvas')));
  await tester.dragFrom(centre + Offset(-100, dy), const Offset(200, 0));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('before connecting the sync button offers to connect', (tester) async {
    final s = await pumpApp(tester);

    await tester.tap(find.byKey(const ValueKey('action-sync')));
    await tester.pumpAndSettle();

    expect(find.text('Connect to a server'), findsOneWidget);
    expect(s.app.connected, isFalse);
  });

  testWidgets('connecting creates the project and the dialog closes', (tester) async {
    final s = await pumpApp(tester);

    await connect(tester);

    expect(s.app.connected, isTrue);
    expect(find.text('Connect to a server'), findsNothing);
    expect(find.byIcon(Icons.cloud_done_outlined), findsOneWidget);
    expect(find.text('Sync: Up to date'), findsOneWidget);
  });

  testWidgets('an unreachable server keeps the dialog open and says why', (tester) async {
    final s = await pumpApp(tester);
    s.server.offline = true;

    await connect(tester);

    expect(s.app.connected, isFalse);
    expect(find.byKey(const ValueKey('connect-error')), findsOneWidget);
    expect(find.textContaining('Cannot reach the server'), findsOneWidget);
  });

  testWidgets('drawing offline shows a badge with the number of unsent changes, and Sync sends them', (tester) async {
    final s = await pumpApp(tester);
    await connect(tester);

    await drawWall(tester);
    expect(find.descendant(of: find.byKey(const ValueKey('sync-badge')), matching: find.text('1')), findsOneWidget);
    expect(find.text('Unsent changes: 1'), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('action-sync')));
    await tester.pumpAndSettle();

    expect(s.server.model.walls, hasLength(1));
    expect(find.byKey(const ValueKey('sync-badge')), findsNothing);
    expect(find.text('Unsent changes: 0'), findsOneWidget);
    expect(find.byKey(const ValueKey('action-undo')).evaluate().isNotEmpty, isTrue);
  });

  testWidgets('with no network the change is kept and the button shows offline', (tester) async {
    final s = await pumpApp(tester);
    await connect(tester);
    await drawWall(tester);

    s.server.offline = true;
    await tester.tap(find.byKey(const ValueKey('action-sync')));
    await tester.pumpAndSettle();

    expect(find.byIcon(Icons.cloud_off_outlined), findsOneWidget);
    expect(s.editor.model.walls, hasLength(1));
    expect(find.text('Unsent changes: 1'), findsOneWidget);
    expect(find.textContaining('Offline'), findsWidgets);
  });

  testWidgets('a conflict opens a dialog; "Keep server version" shows the server plan', (tester) async {
    final s = await pumpApp(tester);
    await connect(tester);
    await drawWall(tester);
    s.server.otherDeviceEdits(const CreateWall(wallId: 'theirs', startNodeId: 'ta', start: Vec2(0, 5000), endNodeId: 'tb', end: Vec2(4000, 5000)));

    await tester.tap(find.byKey(const ValueKey('action-sync')));
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey('conflict-dialog')), findsOneWidget);
    expect(find.textContaining('1 unsent'), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('conflict-keep-server')));
    await tester.pumpAndSettle();

    expect(s.editor.model.walls.keys, ['theirs']);
    expect(find.byKey(const ValueKey('conflict-dialog')), findsNothing);
    expect(find.text('Sync: Up to date'), findsOneWidget);
  });

  testWidgets('a conflict resolved with "Keep my changes" leaves both on the server', (tester) async {
    final s = await pumpApp(tester);
    await connect(tester);
    await drawWall(tester);
    s.server.otherDeviceEdits(const CreateWall(wallId: 'theirs', startNodeId: 'ta', start: Vec2(0, 5000), endNodeId: 'tb', end: Vec2(4000, 5000)));
    await tester.tap(find.byKey(const ValueKey('action-sync')));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const ValueKey('conflict-keep-mine')));
    await tester.pumpAndSettle();

    expect(s.server.model.walls, hasLength(2));
    expect(s.editor.model.walls, hasLength(2));
    expect(s.app.status, SyncStatus.upToDate);
  });

  testWidgets('"Decide later" keeps the conflict; the button stays red and the next tap reopens the dialog', (tester) async {
    final s = await pumpApp(tester);
    await connect(tester);
    await drawWall(tester);
    s.server.otherDeviceEdits(const CreateWall(wallId: 'theirs', startNodeId: 'ta', start: Vec2(0, 5000), endNodeId: 'tb', end: Vec2(4000, 5000)));
    await tester.tap(find.byKey(const ValueKey('action-sync')));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Decide later'));
    await tester.pumpAndSettle();
    expect(find.byIcon(Icons.warning_amber_rounded), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('action-sync')));
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey('conflict-dialog')), findsOneWidget);
  });

  testWidgets('restarting the app restores the plan and the unsent changes from the database', (tester) async {
    final s = await pumpApp(tester);
    await connect(tester);
    await drawWall(tester);

    final reopenedEditor = PlanController();
    final reopened = AppController(store: s.store, editor: reopenedEditor, clientFactory: () => s.server.client);
    await tester.runAsync(reopened.restore);

    expect(reopened.connected, isTrue);
    expect(reopenedEditor.model.walls, hasLength(1));
    expect(reopened.session!.pendingCount, 1);
  });

  testWidgets('demo plans are disabled once connected, so a synced plan cannot be replaced by accident', (tester) async {
    await pumpApp(tester);
    await connect(tester);

    final menu = tester.widget<PopupMenuButton<String>>(find.byKey(const ValueKey('action-demo')));

    expect(menu.enabled, isFalse);
  });
}
