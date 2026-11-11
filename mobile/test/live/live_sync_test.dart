@Tags(['live'])
library;

import 'dart:convert';

import 'package:building_draft_app/geometry/commands.dart';
import 'package:building_draft_app/geometry/model.dart';
import 'package:building_draft_app/geometry/rooms.dart';
import 'package:building_draft_app/geometry/vec2.dart';
import 'package:building_draft_app/state/plan_controller.dart';
import 'package:building_draft_app/sync/api_client.dart';
import 'package:building_draft_app/sync/local_store.dart';
import 'package:building_draft_app/sync/models.dart';
import 'package:building_draft_app/sync/plan_session.dart';
import 'package:building_draft_app/sync/server_commands.dart';
import 'package:building_draft_app/sync/sqlite_store.dart';
import 'package:building_draft_app/sync/sync_service.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

// Runs the app's sync code against the REAL backend (and its real geometry pipeline and database).
//   1. start the API in Development mode, for example:  dotnet run --project src/BuildingDraft.Api
//   2. flutter test test/live --dart-define=BD_API_URL=http://localhost:5276
// Without BD_API_URL these tests are skipped.
const apiUrl = String.fromEnvironment('BD_API_URL');

class Device {
  Device(this.name, this.store, this.editor, this.service, this.session);

  final String name;
  final LocalStore store;
  final PlanController editor;
  final SyncService service;
  PlanSession session;

  Future<void> edit(PlanCommand command) async {
    expect(editor.execute(command), isTrue, reason: '$name: ${editor.lastError}');
    await session.flush();
  }
}

ApiClient newApi() => ApiClient(client: http.Client(), baseUrl: apiUrl);

int _ids = 0;
String id() => '${DateTime.now().microsecondsSinceEpoch.toRadixString(16).padLeft(12, '0').substring(0, 8)}-0000-4000-8000-${(_ids++).toRadixString(16).padLeft(12, '0')}';

CreateWall wall(Vec2 a, Vec2 b, {String? na, String? nb}) =>
    CreateWall(wallId: id(), startNodeId: na ?? id(), start: a, endNodeId: nb ?? id(), end: b);

/// Device A creates the project and uploads a rectangle.
Future<Device> deviceA(ApiClient api) async {
  final store = SqliteLocalStore.open(':memory:');
  final service = SyncService(api: api, store: store, deviceId: 'device-a');
  final editor = PlanController(model: PlanModel(floorId: 'scratch'));
  final session = await PlanSession.connect(controller: editor, store: store, service: service, api: api, projectName: 'Live test ${DateTime.now().toIso8601String()}');
  return Device('A', store, editor, service, session);
}

/// Device B starts from the server's current plan, as a second phone would after downloading it.
Future<Device> deviceB(ApiClient api, Device a) async {
  final geometry = await api.geometry(a.session.variantId);
  final store = SqliteLocalStore.open(':memory:');
  await store.saveDocument(StoredDocument(
    variantId: a.session.variantId,
    projectId: a.session.document.projectId,
    floorId: a.session.document.floorId,
    serverVersion: geometry['version']! as int,
    model: modelFromSnapshot(geometry, a.session.document.floorId),
  ));
  final service = SyncService(api: api, store: store, deviceId: 'device-b');
  final editor = PlanController();
  final session = await PlanSession.open(controller: editor, store: store, service: service, variantId: a.session.variantId);
  return Device('B', store, editor, service, session);
}

Future<Map<String, Object?>> serverGeometry(String variantId) async {
  final response = await http.get(Uri.parse('$apiUrl/api/v1/variants/$variantId/geometry'));
  expect(response.statusCode, 200);
  return (jsonDecode(response.body) as Map).cast<String, Object?>();
}

Future<void> drawRectangle(Device d) async {
  final corners = [const Vec2(0, 0), const Vec2(4000, 0), const Vec2(4000, 3000), const Vec2(0, 3000)];
  final nodes = [for (var i = 0; i < 4; i++) id()];
  for (var i = 0; i < 4; i++) {
    final next = (i + 1) % 4;
    await d.edit(wall(corners[i], corners[next], na: nodes[i], nb: nodes[next]));
  }
}

void main() {
  group('the app against the real server', () {
    test('offline drawing, syncing, two devices, conflict and refusal', () async {
      final api = newApi();

      // --- A draws a rectangle and a T-junction (create, split, create) offline, then syncs ---
      final a = await deviceA(api);
      await drawRectangle(a);
      a.editor.selectTool(PlanTool.wall);
      for (final p in const [Vec2(2000, 0), Vec2(2000, 3000)]) {
        a.editor.pointerDown(p);
        a.editor.pointerUp(p);
      }
      a.editor.cancelInteraction();
      await a.session.flush();
      expect(a.editor.lastError, isNull);
      final queued = await a.store.outbox(a.session.variantId);
      expect(queued.map((o) => o.commandType), ['create_wall', 'create_wall', 'create_wall', 'create_wall', 'split_wall', 'split_wall', 'create_wall']);

      await a.session.syncNow();

      expect(a.session.status, SyncStatus.upToDate, reason: a.session.message);
      var geometry = await serverGeometry(a.session.variantId);
      expect((geometry['walls']! as List), hasLength(7));
      expect(geometry['version'], 8);
      expect(a.session.document.serverVersion, 8);
      expect(a.editor.canUndo, isFalse);

      // --- B downloads the plan; A and B now edit the same version while both are offline ---
      final b = await deviceB(api, a);
      expect(b.editor.model.walls.keys, unorderedEquals(a.editor.model.walls.keys));

      final cornerOfA = a.editor.model.nodes.values.firstWhere((n) => n.x == 4000 && n.y == 3000);
      await a.edit(MoveNode(cornerOfA.id, const Vec2(4200, 3200)));
      await b.edit(wall(const Vec2(0, 5000), const Vec2(4000, 5000)));

      await a.session.syncNow();
      expect(a.session.status, SyncStatus.upToDate, reason: a.session.message);

      await b.session.syncNow();
      expect(b.session.status, SyncStatus.conflict, reason: 'B started from a version A has since changed');
      expect((await b.store.outbox(b.session.variantId)).single.status, OutboxStatus.conflict);
      expect((await serverGeometry(a.session.variantId))['walls'], hasLength(7), reason: 'B\'s wall must not be applied yet');

      // --- B chooses to keep its change: it is applied on top of A's ---
      await b.session.keepMine();

      expect(b.session.status, SyncStatus.upToDate, reason: b.session.message);
      geometry = await serverGeometry(a.session.variantId);
      expect((geometry['walls']! as List), hasLength(8));
      expect((geometry['nodes']! as List).cast<Map>().any((n) => n['x'] == 4200 && n['y'] == 3200), isTrue, reason: 'A\'s move survived');
      expect(b.editor.model.walls, hasLength(8));
      expect(b.editor.model.nodes.values.any((n) => n.x == 4200 && n.y == 3200), isTrue);

      // --- A pulls B's wall: replayed, not reloaded ---
      await a.session.syncNow();
      expect(a.session.status, SyncStatus.upToDate);
      expect(a.editor.model.walls, hasLength(8));
      expect(a.session.message, isNull, reason: 'a plain replay, no snapshot reload');

      // --- the server's audit log recorded the conflict resolution ---
      final audit = await http.get(Uri.parse('$apiUrl/api/v1/projects/${a.session.document.projectId}/audit'));
      expect(audit.body, contains('sync.conflict_resolved'));

      // --- after the Original Layout is approved, further edits are refused with the reason ---
      expect((await http.post(Uri.parse('$apiUrl/api/v1/variants/${a.session.variantId}/approve'))).statusCode, 200);
      await a.edit(wall(const Vec2(0, 8000), const Vec2(4000, 8000)));
      await a.session.syncNow();

      expect(a.session.status, SyncStatus.failed);
      expect((await a.store.outbox(a.session.variantId)).single.errorCode, 'ORIGINAL_LAYOUT_PROTECTED');
      expect(a.session.message, contains('Original Layout'));
      expect((await serverGeometry(a.session.variantId))['walls'], hasLength(8));
    }, skip: apiUrl.isEmpty ? 'set --dart-define=BD_API_URL to run against a real server' : false);

    test('rooms, doors, windows and a measurement: the real server agrees with the app on areas, shape and fixed lengths', () async {
      final api = newApi();
      final a = await deviceA(api);
      await drawRectangle(a);

      // The room is made through the tool, as a user would: tap inside the loop, name it.
      a.editor.selectTool(PlanTool.room);
      a.editor.pointerDown(const Vec2(2000, 1500));
      a.editor.pointerUp(const Vec2(2000, 1500));
      expect(a.editor.createRoom('Living room', roomType: 'living_room'), isTrue, reason: a.editor.lastError);
      a.editor.selectTool(PlanTool.door);
      a.editor.pointerDown(const Vec2(2000, 0));
      a.editor.pointerUp(const Vec2(2000, 0));
      a.editor.selectTool(PlanTool.window);
      a.editor.pointerDown(const Vec2(2000, 3000));
      a.editor.pointerUp(const Vec2(2000, 3000));
      expect(a.editor.lastError, isNull);

      // Measure the right wall with a laser: inner 2800 mm (3000 mm centerline), which keeps its corners in place.
      a.editor.selectTool(PlanTool.select);
      a.editor.pointerDown(const Vec2(4000, 1500));
      a.editor.pointerUp(const Vec2(4000, 1500));
      expect(a.editor.applyMeasurement(2800), isTrue, reason: a.editor.lastError);
      await a.session.flush();

      await a.session.syncNow();
      expect(a.session.status, SyncStatus.upToDate, reason: a.session.message);

      // What the real server stored:
      final geometry = await serverGeometry(a.session.variantId);
      final rooms = (geometry['rooms']! as List).cast<Map<String, Object?>>();
      expect(rooms, hasLength(1));
      expect((geometry['openings']! as List), hasLength(2));
      final fixed = (geometry['constraints']! as List).cast<Map<String, Object?>>().where((c) => c['type'] == 'fixed_length').toList();
      expect(fixed, hasLength(1));
      expect((fixed.single['value']! as num).toDouble(), 3000);

      // Two implementations, one answer: the server's own inner area of the room equals the app's.
      final appArea = roomMetrics(a.editor.model, a.editor.model.rooms.values.single)!.areaSqM;
      expect((rooms.single['innerAreaM2']! as num).toDouble(), closeTo(appArea, 1e-6));
      expect(appArea, closeTo(10.64, 1e-9));
      expect(((geometry['summary']! as Map)['enclosedAreaM2']! as num).toDouble(), closeTo(10.64, 1e-6));

      // A second device that downloads the plan understands the real JSON, including the room nesting and the
      // enum spellings, and ends with the same plan and the same locked wall.
      final b = await deviceB(api, a);
      expect(b.editor.model.rooms.values.single.name, 'Living room');
      expect(b.editor.model.openings.values.map((o) => o.kind), unorderedEquals([OpeningKind.door, OpeningKind.window]));
      expect(b.editor.model.fixedLengths.values.single, 3000);
      expect(roomMetrics(b.editor.model, b.editor.model.rooms.values.single)!.areaSqM, closeTo(appArea, 1e-9));

      // ...and obeys it: dragging a corner of the measured wall is refused on this device without asking the server.
      final measuredWall = b.editor.model.fixedLengths.keys.single;
      final corner = b.editor.model.walls[measuredWall]!.endNodeId;
      final before = b.editor.model.pos(corner);
      expect(b.editor.execute(MoveNode(corner, before + const Vec2(500, 0))), isFalse);
      expect(b.editor.lastError, contains('fixed'));

      // A balcony: made from a snapshot-loaded plan, it is stored as an open area and counted apart.
      b.editor.selectTool(PlanTool.wall);
      for (final p in const [Vec2(4000, 0), Vec2(6000, 0), Vec2(6000, 3000), Vec2(4000, 3000)]) {
        b.editor.pointerDown(p);
        b.editor.pointerUp(p);
      }
      b.editor.cancelInteraction();
      b.editor.selectTool(PlanTool.room);
      b.editor.pointerDown(const Vec2(5000, 1500));
      b.editor.pointerUp(const Vec2(5000, 1500));
      expect(b.editor.createRoom('Balcony', roomType: 'balcony', areaType: RoomAreaType.openArea), isTrue, reason: b.editor.lastError);
      await b.session.syncNow();
      expect(b.session.status, SyncStatus.upToDate, reason: b.session.message);

      final after = await serverGeometry(a.session.variantId);
      final summary = after['summary']! as Map;
      expect((summary['enclosedAreaM2']! as num).toDouble(), closeTo(10.64, 1e-6));
      expect((summary['openAreaM2']! as num).toDouble(), closeTo(1.8 * 2.8, 1e-6), reason: 'the balcony is reported apart from the enclosed area');
    }, skip: apiUrl.isEmpty ? 'set --dart-define=BD_API_URL to run against a real server' : false);

    test('the same push sent twice is applied once, and a down server leaves the edit queued', () async {
      final api = newApi();
      final a = await deviceA(api);
      await a.edit(wall(const Vec2(0, 0), const Vec2(3000, 0)));
      final operations = await a.store.outbox(a.session.variantId);

      await api.push(operations);
      await api.push(operations);
      expect((await serverGeometry(a.session.variantId))['version'], 2);

      await a.edit(wall(const Vec2(0, 1000), const Vec2(3000, 1000)));
      final offline = SyncService(api: ApiClient(client: http.Client(), baseUrl: 'http://127.0.0.1:1'), store: a.store, deviceId: 'device-a');
      final report = await offline.sync(a.session.variantId);

      expect(report.status, SyncStatus.offline);
      expect(await a.store.outbox(a.session.variantId), hasLength(2), reason: 'nothing was acknowledged or lost');
    }, skip: apiUrl.isEmpty ? 'set --dart-define=BD_API_URL to run against a real server' : false);
  });

}
