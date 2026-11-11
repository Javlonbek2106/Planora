import 'dart:io';

import 'package:building_draft_app/geometry/commands.dart';
import 'package:building_draft_app/geometry/model.dart';
import 'package:building_draft_app/geometry/vec2.dart';
import 'package:building_draft_app/sync/local_store.dart';
import 'package:building_draft_app/sync/models.dart';
import 'package:building_draft_app/sync/sqlite_store.dart';
import 'package:flutter_test/flutter_test.dart';

PlanModel sample() => const CreateWall(wallId: 'w1', startNodeId: 'a', start: Vec2(0, 0), endNodeId: 'b', end: Vec2(3000, 0))
    .apply(PlanModel(floorId: 'floor'));

StoredDocument document({int version = 1, PlanModel? model}) =>
    StoredDocument(variantId: 'v1', projectId: 'p1', floorId: 'floor', serverVersion: version, model: model ?? sample());

OutboxOperation operation(String id, int expected, {String type = 'create_wall'}) => OutboxOperation(
      operationId: id,
      variantId: 'v1',
      expectedVersion: expected,
      commandType: type,
      payload: {'wallId': id, 'startXMm': 1.5},
    );

/// The same behavior is required of every store, so the tests run against each.
void storeContract(String name, Future<LocalStore> Function() create) {
  group(name, () {
    late LocalStore store;
    setUp(() async => store = await create());
    tearDown(() => store.close());

    test('a document is saved, loaded and replaced', () async {
      expect(await store.loadDocument('v1'), isNull);

      await store.saveDocument(document());
      final loaded = (await store.loadDocument('v1'))!;
      expect(loaded.serverVersion, 1);
      expect(loaded.model.walls.keys, ['w1']);
      expect(loaded.model.pos('b'), const Vec2(3000, 0));

      await store.saveDocument(document(version: 5, model: PlanModel(floorId: 'floor')));
      expect((await store.loadDocument('v1'))!.serverVersion, 5);
      expect((await store.loadDocument('v1'))!.model.walls, isEmpty);
      expect(await store.listDocuments(), hasLength(1));
    });

    test('the outbox keeps the order of enqueueing and the payload intact', () async {
      for (final (i, id) in ['a', 'b', 'c'].indexed) {
        await store.enqueue(operation(id, 1 + i));
      }

      final outbox = await store.outbox('v1');

      expect(outbox.map((o) => o.operationId), ['a', 'b', 'c']);
      expect(outbox.map((o) => o.expectedVersion), [1, 2, 3]);
      expect(outbox.first.payload, {'wallId': 'a', 'startXMm': 1.5});
      expect(outbox.first.status, OutboxStatus.pending);
      expect(await store.outbox('other'), isEmpty);
    });

    test('operation status and error are updated, and operations can be removed', () async {
      await store.enqueue(operation('a', 1));
      await store.enqueue(operation('b', 2));

      await store.updateOperation((await store.outbox('v1')).first.withStatus(OutboxStatus.failed, errorCode: 'X', errorMessage: 'nope'));
      final updated = (await store.outbox('v1')).first;
      expect((updated.status, updated.errorCode, updated.errorMessage), (OutboxStatus.failed, 'X', 'nope'));

      await store.removeOperations(['a']);
      expect((await store.outbox('v1')).map((o) => o.operationId), ['b']);
    });

    test('settings are stored and overwritten', () async {
      expect(await store.getSetting('url'), isNull);
      await store.setSetting('url', 'http://a');
      await store.setSetting('url', 'http://b');
      expect(await store.getSetting('url'), 'http://b');
    });
  });
}

void main() {
  storeContract('MemoryLocalStore', () async => MemoryLocalStore());
  storeContract('SqliteLocalStore (in memory)', () async => SqliteLocalStore.open(':memory:'));

  test('SQLite data survives closing and reopening the database file', () async {
    final dir = Directory.systemTemp.createTempSync('bd_store_');
    addTearDown(() => dir.deleteSync(recursive: true));
    final path = '${dir.path}/plan.db';

    var store = SqliteLocalStore.open(path);
    await store.saveDocument(document(version: 3));
    await store.enqueue(operation('a', 3));
    await store.close();

    store = SqliteLocalStore.open(path);
    expect((await store.loadDocument('v1'))!.serverVersion, 3);
    expect((await store.outbox('v1')).single.operationId, 'a');
    await store.close();
  });

  test('a duplicate operation id is refused by the database', () async {
    final store = SqliteLocalStore.open(':memory:');
    await store.enqueue(operation('a', 1));

    expect(() => store.enqueue(operation('a', 2)), throwsA(anything));
    await store.close();
  });
}
