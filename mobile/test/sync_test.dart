import 'package:building_draft_app/geometry/commands.dart';
import 'package:building_draft_app/geometry/model.dart';
import 'package:building_draft_app/geometry/vec2.dart';
import 'package:building_draft_app/state/plan_controller.dart';
import 'package:building_draft_app/sync/api_client.dart';
import 'package:building_draft_app/sync/local_store.dart';
import 'package:building_draft_app/sync/models.dart';
import 'package:building_draft_app/sync/plan_session.dart';
import 'package:building_draft_app/sync/sqlite_store.dart';
import 'package:building_draft_app/sync/sync_service.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support/fake_server.dart';

CreateWall wall(String id, Vec2 a, Vec2 b, {String? na, String? nb}) =>
    CreateWall(wallId: id, startNodeId: na ?? 'n-$id-a', start: a, endNodeId: nb ?? 'n-$id-b', end: b);

class Harness {
  Harness._(this.server, this.store, this.api, this.service, this.controller, this.session);

  final FakeServer server;
  final LocalStore store;
  final ApiClient api;
  final SyncService service;
  final PlanController controller;
  PlanSession session;

  static Future<Harness> create({LocalStore? store, PlanController? controller}) async {
    final server = FakeServer();
    final localStore = store ?? MemoryLocalStore();
    final api = ApiClient(client: server.client, baseUrl: 'http://test');
    final service = SyncService(api: api, store: localStore, deviceId: 'device-1');
    final editor = controller ?? PlanController(model: PlanModel(floorId: 'scratch'));
    final session = await PlanSession.connect(controller: editor, store: localStore, service: service, api: api, projectName: 'Flat');
    return Harness._(server, localStore, api, service, editor, session);
  }

  Future<List<OutboxOperation>> outbox() async {
    await session.flush();
    return store.outbox(FakeServer.variantId);
  }

  Future<void> edit(PlanCommand command) async {
    expect(controller.execute(command), isTrue, reason: controller.lastError);
    await session.flush();
  }
}

void main() {
  group('working offline', () {
    test('every edit is stored and queued at once, with consecutive expected versions', () async {
      final h = await Harness.create();

      await h.edit(wall('w1', const Vec2(0, 0), const Vec2(4000, 0)));
      await h.edit(wall('w2', const Vec2(4000, 0), const Vec2(4000, 3000), na: 'n-w1-b'));

      final outbox = await h.outbox();
      expect(outbox.map((o) => o.commandType), ['create_wall', 'create_wall']);
      expect(outbox.map((o) => o.expectedVersion), [1, 2]);
      expect(outbox.map((o) => o.operationId).toSet(), hasLength(2));
      expect(h.session.pendingCount, 2);
      expect(h.session.status, SyncStatus.pending);
      expect(h.server.requests, 2, reason: 'only the project and floor were created; nothing else was sent');
    });

    test('a step made of several server commands is queued as several operations', () async {
      final h = await Harness.create();
      await h.edit(wall('w1', const Vec2(0, 0), const Vec2(4000, 0)));
      h.controller.selectTool(PlanTool.wall);
      for (final p in const [Vec2(2000, 0), Vec2(2000, 3000)]) {
        h.controller.pointerDown(p);
        h.controller.pointerUp(p);
      }
      await h.session.flush();

      final types = (await h.outbox()).map((o) => o.commandType).toList();

      expect(types, ['create_wall', 'split_wall', 'create_wall']);
      expect((await h.outbox()).map((o) => o.expectedVersion), [1, 2, 3]);
    });

    test('undo removes the queued edit and redo queues it again under a new id', () async {
      final h = await Harness.create();
      await h.edit(wall('w1', const Vec2(0, 0), const Vec2(4000, 0)));
      final original = (await h.outbox()).single.operationId;

      h.controller.undo();
      expect(await h.outbox(), isEmpty);

      h.controller.redo();
      final again = (await h.outbox()).single;
      expect(again.operationId, isNot(original));
      expect(again.expectedVersion, 1);
    });

    test('closing and reopening the app restores the plan and the unsent edits', () async {
      final store = SqliteLocalStore.open(':memory:');
      final h = await Harness.create(store: store);
      await h.edit(wall('w1', const Vec2(0, 0), const Vec2(4000, 0)));

      final reopened = PlanController();
      final session = await PlanSession.open(controller: reopened, store: store, service: h.service, variantId: FakeServer.variantId);

      expect(reopened.model.walls.keys, ['w1']);
      expect(session.pendingCount, 1);
      expect(session.status, SyncStatus.pending);
    });

    test('a drawing made before connecting is uploaded by the first sync', () async {
      final scratch = PlanController(model: wall('w1', const Vec2(0, 0), const Vec2(4000, 0)).apply(PlanModel(floorId: 'scratch')));
      final h = await Harness.create(controller: scratch);

      await h.session.syncNow();

      expect(h.server.model.walls.keys, ['w1']);
      expect(h.server.version, 2);
      expect(h.session.status, SyncStatus.upToDate);
      expect(h.controller.model.floorId, FakeServer.floorId);
    });
  });

  group('synchronizing', () {
    test('the outbox is sent in order, emptied, and the server ends with the same plan', () async {
      final h = await Harness.create();
      await h.edit(wall('w1', const Vec2(0, 0), const Vec2(4000, 0)));
      await h.edit(wall('w2', const Vec2(4000, 0), const Vec2(4000, 3000), na: 'n-w1-b'));

      await h.session.syncNow();

      expect(h.server.version, 3);
      expect(h.server.model.walls.keys, containsAll(['w1', 'w2']));
      expect(await h.outbox(), isEmpty);
      expect(h.session.status, SyncStatus.upToDate);
      expect(h.session.document.serverVersion, 3);
      expect(h.session.lastSyncedAt, isNotNull);
    });

    test('synced edits can no longer be undone, unsent ones still can', () async {
      final h = await Harness.create();
      await h.edit(wall('w1', const Vec2(0, 0), const Vec2(4000, 0)));
      await h.session.syncNow();
      expect(h.controller.canUndo, isFalse);

      await h.edit(wall('w2', const Vec2(0, 1000), const Vec2(4000, 1000)));
      expect(h.controller.canUndo, isTrue);
    });

    test('offline: the edits stay queued and a later sync sends them', () async {
      final h = await Harness.create();
      await h.edit(wall('w1', const Vec2(0, 0), const Vec2(4000, 0)));

      h.server.offline = true;
      await h.session.syncNow();
      expect(h.session.status, SyncStatus.offline);
      expect(h.session.pendingCount, 1);
      expect(h.controller.model.walls, isNotEmpty);

      h.server.offline = false;
      await h.session.syncNow();
      expect(h.session.status, SyncStatus.upToDate);
      expect(h.server.model.walls.keys, ['w1']);
    });

    test('sending the same queue twice does not apply it twice', () async {
      final h = await Harness.create();
      await h.edit(wall('w1', const Vec2(0, 0), const Vec2(4000, 0)));
      final operations = await h.outbox();

      await h.api.push(operations);
      await h.api.push(operations);

      expect(h.server.version, 2);
    });

    test('edits are refused while a sync is running', () async {
      final h = await Harness.create();
      await h.edit(wall('w1', const Vec2(0, 0), const Vec2(4000, 0)));

      final running = h.session.syncNow();
      final refused = h.controller.execute(wall('w2', const Vec2(0, 1000), const Vec2(4000, 1000)));
      await running;

      expect(refused, isFalse);
      expect(h.controller.lastError, contains('Synchronizing'));
      expect(h.controller.editingLocked, isFalse);
    });
  });

  group('changes from other devices', () {
    test('new edits made elsewhere are replayed on the local plan', () async {
      final h = await Harness.create();
      await h.edit(wall('w1', const Vec2(0, 0), const Vec2(4000, 0)));
      await h.session.syncNow();

      h.server.otherDeviceEdits(wall('w9', const Vec2(0, 5000), const Vec2(4000, 5000)));
      h.server.otherDeviceEdits(const MoveNode('n-w1-b', Vec2(4500, 0)));
      await h.session.syncNow();

      expect(h.controller.model.walls.keys, containsAll(['w1', 'w9']));
      expect(h.controller.model.pos('n-w1-b'), const Vec2(4500, 0));
      expect(h.session.document.serverVersion, h.server.version);
      expect(h.session.status, SyncStatus.upToDate);
    });

    test('a command the editor cannot replay makes it reload the whole plan', () async {
      final h = await Harness.create();
      await h.edit(wall('w1', const Vec2(0, 0), const Vec2(4000, 0)));
      await h.session.syncNow();

      h.server.otherDeviceEdits(wall('w9', const Vec2(0, 5000), const Vec2(4000, 5000)));
      h.server.otherDeviceRunsUnknownCommand();
      await h.session.syncNow();

      expect(h.controller.model.walls.keys, containsAll(['w1', 'w9']));
      expect(h.session.document.serverVersion, h.server.version);
      expect(h.session.message, contains('Reloaded'));
    });

    test('when the server asks for a snapshot (history restored) the plan is reloaded', () async {
      final h = await Harness.create();
      h.server.snapshotOnlyPull = true;
      h.server.otherDeviceEdits(wall('w9', const Vec2(0, 5000), const Vec2(4000, 5000)));

      await h.session.syncNow();

      expect(h.controller.model.walls.keys, ['w9']);
    });
  });

  group('rooms, openings and measurements', () {
    Future<Harness> withRectangle() async {
      final h = await Harness.create();
      await h.edit(const CreateWall(wallId: 'w1', startNodeId: 'n1', start: Vec2(0, 0), endNodeId: 'n2', end: Vec2(4000, 0)));
      await h.edit(const CreateWall(wallId: 'w2', startNodeId: 'n2', start: Vec2(4000, 0), endNodeId: 'n3', end: Vec2(4000, 3000)));
      await h.edit(const CreateWall(wallId: 'w3', startNodeId: 'n3', start: Vec2(4000, 3000), endNodeId: 'n4', end: Vec2(0, 3000)));
      await h.edit(const CreateWall(wallId: 'w4', startNodeId: 'n4', start: Vec2(0, 3000), endNodeId: 'n1', end: Vec2(0, 0)));
      return h;
    }

    test('a room, a door and a measurement are queued offline and sent in order', () async {
      final h = await withRectangle();
      await h.edit(const CreateRoom(roomId: 'r1', name: 'Living', wallIds: ['w1', 'w2', 'w3', 'w4']));
      await h.edit(const AttachOpening(openingId: 'o1', wallId: 'w1', kind: OpeningKind.door, offsetMm: 500, widthMm: 900, heightMm: 2100));
      await h.edit(const ApplyMeasurement(wallId: 'w4', valueMm: 2800));

      expect((await h.outbox()).map((o) => o.commandType).skip(4), ['create_room', 'attach_opening', 'apply_measurement']);
      await h.session.syncNow();

      expect(h.session.status, SyncStatus.upToDate, reason: h.session.message);
      expect(h.server.model.rooms.keys, ['r1']);
      expect(h.server.model.openings.keys, ['o1']);
      expect(h.server.model.fixedLengths['w4'], 3000);
    });

    test('a room and a door made on another device are replayed, not reloaded', () async {
      final h = await withRectangle();
      await h.session.syncNow();

      h.server.otherDeviceEdits(const CreateRoom(roomId: 'r9', name: 'Kitchen', wallIds: ['w1', 'w2', 'w3', 'w4']));
      h.server.otherDeviceEdits(const AttachOpening(openingId: 'o9', wallId: 'w2', kind: OpeningKind.window, offsetMm: 800, widthMm: 1200, heightMm: 1200, sillMm: 900));
      await h.session.syncNow();

      expect(h.controller.model.rooms['r9']!.name, 'Kitchen');
      expect(h.controller.model.openings['o9']!.kind, OpeningKind.window);
      expect(h.session.message, isNull, reason: 'a plain replay');
    });

    test('a snapshot brings rooms, openings and measured lengths along', () async {
      final h = await withRectangle();
      await h.session.syncNow();
      h.server.otherDeviceEdits(const CreateRoom(roomId: 'r9', name: 'Kitchen', wallIds: ['w1', 'w2', 'w3', 'w4']));
      h.server.otherDeviceEdits(const AttachOpening(openingId: 'o9', wallId: 'w2', kind: OpeningKind.door, offsetMm: 500, widthMm: 900, heightMm: 2100));
      h.server.otherDeviceEdits(const ApplyMeasurement(wallId: 'w1', valueMm: 3800));
      h.server.snapshotOnlyPull = true;

      await h.session.syncNow();

      final m = h.controller.model;
      expect(m.rooms['r9']!.wallIds, hasLength(4));
      expect(m.openings['o9']!.offsetMm, 500);
      expect(m.fixedLengths['w1'], 4000);
    });

    test('after a pull the measured wall is locked here too', () async {
      final h = await withRectangle();
      await h.session.syncNow();
      h.server.otherDeviceEdits(const ApplyMeasurement(wallId: 'w1', valueMm: 3800));
      await h.session.syncNow();

      final dragged = h.controller.execute(const MoveNode('n2', Vec2(4500, 0)));

      expect(dragged, isFalse);
      expect(h.controller.lastError, contains('fixed'));
    });

    test('a drawing with rooms, openings and measurements made before connecting is uploaded whole', () async {
      var scratch = PlanModel(floorId: 'scratch');
      for (final c in const [
        CreateWall(wallId: 'w1', startNodeId: 'n1', start: Vec2(0, 0), endNodeId: 'n2', end: Vec2(4000, 0)),
        CreateWall(wallId: 'w2', startNodeId: 'n2', start: Vec2(4000, 0), endNodeId: 'n3', end: Vec2(4000, 3000)),
        CreateWall(wallId: 'w3', startNodeId: 'n3', start: Vec2(4000, 3000), endNodeId: 'n4', end: Vec2(0, 3000)),
        CreateWall(wallId: 'w4', startNodeId: 'n4', start: Vec2(0, 3000), endNodeId: 'n1', end: Vec2(0, 0)),
        CreateRoom(roomId: 'r1', name: 'Living', wallIds: ['w1', 'w2', 'w3', 'w4']),
        AttachOpening(openingId: 'o1', wallId: 'w1', kind: OpeningKind.door, offsetMm: 500, widthMm: 900, heightMm: 2100),
        ApplyMeasurement(wallId: 'w2', valueMm: 2800, inner: false),
      ]) {
        scratch = c.apply(scratch);
      }
      final h = await Harness.create(controller: PlanController(model: scratch));

      await h.session.syncNow();

      expect(h.session.status, SyncStatus.upToDate, reason: h.session.message);
      expect(h.server.model.walls, hasLength(4));
      expect(h.server.model.rooms.keys, ['r1']);
      expect(h.server.model.openings.keys, ['o1']);
      expect(h.server.model.fixedLengths['w2'], 2800);
    });
  });

  group('measurement history', () {
    Future<Harness> withWall() async {
      final h = await Harness.create();
      await h.edit(const CreateWall(wallId: 'w1', startNodeId: 'n1', start: Vec2(0, 0), endNodeId: 'n2', end: Vec2(4000, 0)));
      await h.edit(const CreateWall(wallId: 'w2', startNodeId: 'n2', start: Vec2(4000, 0), endNodeId: 'n3', end: Vec2(4000, 3000)));
      await h.session.syncNow();
      return h;
    }

    test('a measurement taken offline reaches the server with its source, note and time', () async {
      final h = await withWall();
      h.controller.pointerDown(const Vec2(2000, 0));
      h.controller.pointerUp(const Vec2(2000, 0));
      h.controller.applyMeasurement(3800, source: MeasurementSource.manual, notes: 'tape, north wall', verified: true);
      await h.session.flush();

      await h.session.syncNow();

      expect(h.session.status, SyncStatus.upToDate, reason: h.session.message);
      final recorded = h.server.model.measurements.values.single;
      expect((recorded.source, recorded.status, recorded.notes, recorded.valueMm), (MeasurementSource.manual, MeasurementStatus.verified, 'tape, north wall', 3800.0));
      expect(h.server.model.fixedLengths['w1'], 4000);
    });

    test('a measurement made on another device is replayed with its history entry', () async {
      final h = await withWall();

      h.server.otherDeviceEdits(ApplyMeasurement(wallId: 'w2', valueMm: 2800, measurementId: 'theirs', source: MeasurementSource.laserBluetooth, capturedAt: DateTime.utc(2026, 11, 10, 9)));
      await h.session.syncNow();

      final history = h.controller.model.measurements['theirs']!;
      expect((history.source, history.wallId), (MeasurementSource.laserBluetooth, 'w2'));
      expect(h.controller.model.fixedLengths['w2'], 3000);
      expect(h.session.message, isNull, reason: 'a plain replay');
    });

    test('a snapshot reload fetches the history from the server too', () async {
      final h = await withWall();
      h.server.otherDeviceEdits(ApplyMeasurement(wallId: 'w2', valueMm: 2800, measurementId: 'first', capturedAt: DateTime.utc(2026, 11, 10, 9)));
      h.server.otherDeviceEdits(ApplyMeasurement(wallId: 'w2', valueMm: 2850, measurementId: 'second', notes: 'redone', capturedAt: DateTime.utc(2026, 11, 10, 10)));
      h.server.snapshotOnlyPull = true;

      await h.session.syncNow();

      final model = h.controller.model;
      expect(model.measurementsOf('w2').map((m) => m.id), ['second', 'first'], reason: 'newest first');
      expect(model.measurements['first']!.status, MeasurementStatus.superseded);
      expect(model.measurements['second']!.notes, 'redone');
    });

    test('a measurement taken offline on a plan that is later pushed twice is recorded once', () async {
      final h = await withWall();
      h.controller.pointerDown(const Vec2(2000, 0));
      h.controller.pointerUp(const Vec2(2000, 0));
      h.controller.applyMeasurement(3800);
      await h.session.flush();
      final operations = await h.outbox();

      await h.api.push(operations);
      await h.api.push(operations);

      expect(h.server.model.measurements, hasLength(1));
    });

    group('verifying later', () {
      Future<(Harness, String)> withSyncedMeasurement() async {
        final h = await withWall();
        h.controller.pointerDown(const Vec2(2000, 0));
        h.controller.pointerUp(const Vec2(2000, 0));
        h.controller.applyMeasurement(3800);
        await h.session.flush();
        await h.session.syncNow();
        return (h, h.controller.model.measurements.keys.single);
      }

      test('a synced measurement is verified on the server and on this device, without a new version or an edit', () async {
        final (h, id) = await withSyncedMeasurement();
        final version = h.server.version;

        final problem = await h.session.verifyMeasurement(id);

        expect(problem, isNull);
        expect(h.server.statusChanges.single, (id: id, status: 'verified'));
        expect(h.controller.model.measurements[id]!.status, MeasurementStatus.verified);
        expect(h.server.version, version, reason: 'a status is not a geometry change');
        expect(await h.outbox(), isEmpty);
        expect((await h.store.loadDocument(FakeServer.variantId))!.model.measurements[id]!.status, MeasurementStatus.verified, reason: 'stored, so it survives a restart');
      });

      test('a measurement that is still queued cannot be verified yet', () async {
        final h = await withWall();
        h.controller.pointerDown(const Vec2(2000, 0));
        h.controller.pointerUp(const Vec2(2000, 0));
        h.controller.applyMeasurement(3800);
        await h.session.flush();

        final problem = await h.session.verifyMeasurement(h.controller.model.measurements.keys.single);

        expect(problem, contains('Sync the plan first'));
        expect(h.server.statusChanges, isEmpty);
        expect(h.controller.model.measurements.values.single.status, MeasurementStatus.unverified);
      });

      test('an unreachable server leaves the measurement unverified', () async {
        final (h, id) = await withSyncedMeasurement();
        h.server.offline = true;

        final problem = await h.session.verifyMeasurement(id);

        expect(problem, contains('Cannot reach the server'));
        expect(h.controller.model.measurements[id]!.status, MeasurementStatus.unverified);
      });

      test('a refusal from the server is shown and nothing changes locally', () async {
        final (h, id) = await withSyncedMeasurement();
        h.server.model = h.server.model.copyWith(measurements: {id: h.server.model.measurements[id]!.withStatus(MeasurementStatus.superseded)});

        final problem = await h.session.verifyMeasurement(id);

        expect(problem, contains('MEASUREMENT_STATUS_FINAL'));
        expect(h.controller.model.measurements[id]!.status, MeasurementStatus.unverified);
      });
    });
  });

  group('conflicts are resolved by the user, never merged silently', () {
    Future<Harness> conflicted() async {
      final h = await Harness.create();
      await h.edit(wall('mine', const Vec2(0, 0), const Vec2(4000, 0)));
      h.server.otherDeviceEdits(wall('theirs', const Vec2(0, 5000), const Vec2(4000, 5000)));
      await h.session.syncNow();
      return h;
    }

    test('the server moved on: the edit is marked as a conflict and nothing is lost or applied', () async {
      final h = await conflicted();

      expect(h.session.status, SyncStatus.conflict);
      expect((await h.outbox()).single.status, OutboxStatus.conflict);
      expect(h.controller.model.walls.keys, ['mine']);
      expect(h.server.model.walls.keys, ['theirs']);
    });

    test('a conflicted plan is not sent again until the user decides', () async {
      final h = await conflicted();
      final before = h.server.requests;

      await h.session.syncNow();

      expect(h.server.requests, before);
      expect(h.session.status, SyncStatus.conflict);
    });

    test('keep server: local edits are dropped and the server plan is shown', () async {
      final h = await conflicted();

      await h.session.keepServer();

      expect(h.controller.model.walls.keys, ['theirs']);
      expect(await h.outbox(), isEmpty);
      expect(h.session.status, SyncStatus.upToDate);
      expect(h.session.document.serverVersion, h.server.version);
    });

    test('keep mine: local edits are replayed on top of the server plan, which then holds both', () async {
      final h = await conflicted();

      await h.session.keepMine();

      expect(h.server.model.walls.keys, containsAll(['mine', 'theirs']));
      expect(h.controller.model.walls.keys, containsAll(['mine', 'theirs']));
      expect(await h.outbox(), isEmpty);
      expect(h.session.status, SyncStatus.upToDate);
    });

    test('keep mine is all or nothing: if one edit cannot be applied nothing is', () async {
      final h = await Harness.create();
      await h.edit(wall('mine1', const Vec2(0, 0), const Vec2(4000, 0)));
      await h.edit(wall('mine2', const Vec2(0, 2000), const Vec2(4000, 2000)));
      // Someone else built a wall across the second one.
      h.server.otherDeviceEdits(wall('theirs', const Vec2(2000, -1000), const Vec2(2000, 3000)));
      await h.session.syncNow();
      final versionBefore = h.server.version;

      await h.session.keepMine();

      expect(h.server.version, versionBefore);
      expect(h.server.model.walls.keys, ['theirs']);
      expect(h.session.status, SyncStatus.failed);
      expect(h.session.message, contains('could not be applied'));
      expect((await h.outbox()).every((o) => o.status == OutboxStatus.failed), isTrue);
    });
  });

  group('refusals', () {
    test('an approved Original Layout refuses the edit and the reason is kept', () async {
      final h = await Harness.create();
      await h.edit(wall('w1', const Vec2(0, 0), const Vec2(4000, 0)));
      h.server.locked = true;

      await h.session.syncNow();

      expect(h.session.status, SyncStatus.failed);
      final refused = (await h.outbox()).single;
      expect(refused.status, OutboxStatus.failed);
      expect(refused.errorCode, 'ORIGINAL_LAYOUT_PROTECTED');
      expect(h.session.message, contains('Original Layout'));
    });
  });

  group('the API client', () {
    test('error responses become ApiException with the server code', () async {
      final h = await Harness.create();

      expect(() => h.api.geometry('unknown'), throwsA(isA<ApiException>().having((e) => e.code, 'code', 'NOT_FOUND')));
    });

    test('an unreachable server becomes OfflineException', () async {
      final h = await Harness.create();
      h.server.offline = true;

      expect(() => h.api.geometry(FakeServer.variantId), throwsA(isA<OfflineException>()));
    });
  });
}
