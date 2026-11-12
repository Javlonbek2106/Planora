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

const url = 'http://localhost:5276';

AppController newApp(FakeServer server, {MemoryLocalStore? store, PlanController? editor}) => AppController(
      store: store ?? MemoryLocalStore(),
      editor: editor ?? PlanController(model: PlanModel(floorId: 'scratch')),
      clientFactory: () => server.client,
    );

FakeServer serverWithWall() {
  final server = FakeServer()..otherDeviceEdits(const CreateWall(wallId: 'w1', startNodeId: 'n1', start: Vec2(0, 0), endNodeId: 'n2', end: Vec2(4000, 0)));
  return server;
}

Future<void> pumpHome(WidgetTester tester, AppController app) async {
  tester.view.physicalSize = const Size(1100, 800);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(MaterialApp(home: HomePage(app: app)));
  await tester.pumpAndSettle();
}

Future<void> loadProjects(WidgetTester tester) async {
  await tester.tap(find.byKey(const ValueKey('action-projects')));
  await tester.pumpAndSettle();
  await tester.tap(find.byKey(const ValueKey('projects-load')));
  await tester.pumpAndSettle();
}

void main() {
  group('opening a project from the server', () {
    test('a plan that is not on this device is downloaded, with its history, and opened', () async {
      final server = serverWithWall();
      final app = newApp(server);
      final projects = (await app.listProjects(url: url))!;

      final opened = await app.openProject(url: url, project: projects.single, variant: projects.single.variants.single);

      expect(opened, isTrue, reason: app.connectionError);
      expect(app.connected, isTrue);
      expect(app.editor.model.walls.keys, ['w1']);
      expect(app.session!.document.serverVersion, server.version);
      expect(app.session!.status, SyncStatus.upToDate);
    });

    test('the lowest floor of the project is the one that is opened', () async {
      final server = serverWithWall();
      final app = newApp(server);
      final project = (await app.listProjects(url: url))!.single;

      expect(project.floorId, FakeServer.floorId, reason: 'the fake lists floor 2 first, floor 1 is the lowest');
      await app.openProject(url: url, project: project, variant: project.variants.single);

      expect(app.editor.model.floorId, FakeServer.floorId);
    });

    test('the opened plan is remembered, so the next start reopens it', () async {
      final server = serverWithWall();
      final store = MemoryLocalStore();
      final first = newApp(server, store: store);
      final project = (await first.listProjects(url: url))!.single;
      await first.openProject(url: url, project: project, variant: project.variants.single);

      final second = newApp(server, store: store);
      await second.restore();

      expect(second.connected, isTrue);
      expect(second.editor.model.walls.keys, ['w1']);
    });

    test('a plan already stored here is opened as it is: unsent edits are not overwritten by a fresh download', () async {
      final server = serverWithWall();
      final store = MemoryLocalStore();
      final app = newApp(server, store: store);
      var project = (await app.listProjects(url: url))!.single;
      await app.openProject(url: url, project: project, variant: project.variants.single);
      app.editor.execute(const CreateWall(wallId: 'mine', startNodeId: 'n2', start: Vec2(4000, 0), endNodeId: 'n3', end: Vec2(4000, 3000)));
      await app.session!.flush();

      final again = newApp(server, store: store);
      project = (await again.listProjects(url: url))!.single;
      await again.openProject(url: url, project: project, variant: project.variants.single);

      expect(again.editor.model.walls.keys, unorderedEquals(['w1', 'mine']));
      expect(again.session!.pendingCount, 1);
    });

    test('an unreachable server is reported and nothing is stored', () async {
      final server = serverWithWall();
      final store = MemoryLocalStore();
      final app = newApp(server, store: store);
      final project = (await app.listProjects(url: url))!.single;
      server.offline = true;

      final opened = await app.openProject(url: url, project: project, variant: project.variants.single);

      expect(opened, isFalse);
      expect(app.connectionError, contains('Cannot reach the server'));
      expect(app.connected, isFalse);
      expect(await store.listDocuments(), isEmpty, reason: 'no half-empty plan is left behind');
    });

    test('listing projects on an unreachable server says so and returns nothing', () async {
      final server = FakeServer()..offline = true;
      final app = newApp(server);

      final projects = await app.listProjects(url: url);

      expect(projects, isNull);
      expect(app.connectionError, contains('Cannot reach the server'));
    });

    test('a project without a floor has no plan to open', () async {
      final server = serverWithWall()
        ..extraProjects = [
          {'id': 'p2', 'name': 'Empty', 'floors': <Object>[], 'variants': [{'id': 'v2', 'name': 'Original', 'type': 'original', 'currentVersion': 1, 'isLocked': false}]},
        ];
      final app = newApp(server);
      final empty = (await app.listProjects(url: url))!.firstWhere((p) => p.id == 'p2');

      final opened = await app.openProject(url: url, project: empty, variant: empty.variants.single);

      expect(opened, isFalse);
      expect(app.connectionError, contains('no floor'));
    });
  });

  group('the open-project dialog', () {
    testWidgets('lists the server projects and opens the one that is tapped', (tester) async {
      final server = serverWithWall();
      final app = newApp(server);
      await pumpHome(tester, app);

      await loadProjects(tester);
      expect(find.text('Flat 12'), findsOneWidget);
      expect(find.textContaining('Amir Temur 5'), findsOneWidget);

      await tester.tap(find.byKey(const ValueKey('project-${FakeServer.projectId}')));
      await tester.pumpAndSettle();

      expect(find.text('Open a project'), findsNothing, reason: 'the dialog closes once the plan is open');
      expect(app.connected, isTrue);
      expect(app.editor.model.walls.keys, ['w1']);
    });

    testWidgets('a project with several variants asks which one to open', (tester) async {
      final server = serverWithWall()
        ..extraProjects = [
          {
            'id': 'p2',
            'name': 'Two options',
            'floors': [{'id': 'f2', 'floorNumber': 1}],
            'variants': [
              {'id': FakeServer.variantId, 'name': 'Original Layout', 'type': 'original', 'currentVersion': 2, 'isLocked': false},
              {'id': 'other', 'name': 'Open kitchen', 'type': 'proposed', 'currentVersion': 5, 'isLocked': false},
            ],
          },
        ];
      final app = newApp(server);
      await pumpHome(tester, app);
      await loadProjects(tester);

      await tester.tap(find.byKey(const ValueKey('project-p2')));
      await tester.pumpAndSettle();

      expect(find.byKey(const ValueKey('variant-picker')), findsOneWidget);
      expect(find.textContaining('Open kitchen'), findsOneWidget);
      expect(find.textContaining('v5'), findsOneWidget);
    });

    testWidgets('a drawing that was never saved is flagged before it can be replaced', (tester) async {
      final server = serverWithWall();
      final app = newApp(server);
      app.editor.execute(const CreateWall(wallId: 'scratch-wall', startNodeId: 'a', start: Vec2(0, 0), endNodeId: 'b', end: Vec2(1000, 0)));
      await pumpHome(tester, app);

      await tester.tap(find.byKey(const ValueKey('action-projects')));
      await tester.pumpAndSettle();

      expect(find.byKey(const ValueKey('projects-warning')), findsOneWidget);
    });

    testWidgets('an empty canvas needs no warning', (tester) async {
      final app = newApp(serverWithWall());
      await pumpHome(tester, app);

      await tester.tap(find.byKey(const ValueKey('action-projects')));
      await tester.pumpAndSettle();

      expect(find.byKey(const ValueKey('projects-warning')), findsNothing);
    });

    testWidgets('an unreachable server leaves the dialog open with the reason', (tester) async {
      final server = FakeServer()..offline = true;
      final app = newApp(server);
      await pumpHome(tester, app);

      await loadProjects(tester);

      expect(find.byKey(const ValueKey('projects-error')), findsOneWidget);
      expect(find.textContaining('Cannot reach the server'), findsOneWidget);
    });
  });
}
