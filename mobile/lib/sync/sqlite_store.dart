import 'dart:convert';

import 'package:sqlite3/sqlite3.dart';

import '../geometry/model.dart';
import 'local_store.dart';
import 'models.dart';

/// SQLite-backed [LocalStore]. Pass a file path on a device, or `':memory:'` in tests.
class SqliteLocalStore implements LocalStore {
  SqliteLocalStore._(this._db);

  factory SqliteLocalStore.open(String path) {
    final db = path == ':memory:' ? sqlite3.openInMemory() : sqlite3.open(path);
    final store = SqliteLocalStore._(db).._migrate();
    return store;
  }

  final Database _db;

  static const _schemaVersion = 1;

  void _migrate() {
    final version = _db.select('PRAGMA user_version').first.values.first as int;
    if (version >= _schemaVersion) return;

    _db.execute('''
      CREATE TABLE documents (
        variant_id TEXT PRIMARY KEY,
        project_id TEXT NOT NULL,
        floor_id TEXT NOT NULL,
        server_version INTEGER NOT NULL,
        model_json TEXT NOT NULL
      );
      CREATE TABLE outbox (
        seq INTEGER PRIMARY KEY AUTOINCREMENT,
        operation_id TEXT NOT NULL UNIQUE,
        variant_id TEXT NOT NULL,
        expected_version INTEGER NOT NULL,
        command_type TEXT NOT NULL,
        payload_json TEXT NOT NULL,
        status TEXT NOT NULL,
        error_code TEXT,
        error_message TEXT
      );
      CREATE INDEX outbox_variant ON outbox (variant_id, seq);
      CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
    ''');
    _db.execute('PRAGMA user_version = $_schemaVersion');
  }

  StoredDocument _document(Row r) => StoredDocument(
        variantId: r['variant_id'] as String,
        projectId: r['project_id'] as String,
        floorId: r['floor_id'] as String,
        serverVersion: r['server_version'] as int,
        model: PlanModel.fromJson(jsonDecode(r['model_json'] as String) as Map<String, Object?>),
      );

  OutboxOperation _operation(Row r) => OutboxOperation(
        operationId: r['operation_id'] as String,
        variantId: r['variant_id'] as String,
        expectedVersion: r['expected_version'] as int,
        commandType: r['command_type'] as String,
        payload: (jsonDecode(r['payload_json'] as String) as Map).cast<String, Object?>(),
        status: OutboxStatus.values.byName(r['status'] as String),
        errorCode: r['error_code'] as String?,
        errorMessage: r['error_message'] as String?,
      );

  @override
  Future<void> saveDocument(StoredDocument d) async => _db.execute(
        'INSERT INTO documents (variant_id, project_id, floor_id, server_version, model_json) VALUES (?, ?, ?, ?, ?) '
        'ON CONFLICT(variant_id) DO UPDATE SET project_id = excluded.project_id, floor_id = excluded.floor_id, '
        'server_version = excluded.server_version, model_json = excluded.model_json',
        [d.variantId, d.projectId, d.floorId, d.serverVersion, jsonEncode(d.model.toJson())],
      );

  @override
  Future<StoredDocument?> loadDocument(String variantId) async {
    final rows = _db.select('SELECT * FROM documents WHERE variant_id = ?', [variantId]);
    return rows.isEmpty ? null : _document(rows.first);
  }

  @override
  Future<List<StoredDocument>> listDocuments() async => _db.select('SELECT * FROM documents').map(_document).toList();

  @override
  Future<void> enqueue(OutboxOperation o) async => _db.execute(
        'INSERT INTO outbox (operation_id, variant_id, expected_version, command_type, payload_json, status, error_code, error_message) '
        'VALUES (?, ?, ?, ?, ?, ?, ?, ?)',
        [o.operationId, o.variantId, o.expectedVersion, o.commandType, jsonEncode(o.payload), o.status.name, o.errorCode, o.errorMessage],
      );

  @override
  Future<List<OutboxOperation>> outbox(String variantId) async =>
      _db.select('SELECT * FROM outbox WHERE variant_id = ? ORDER BY seq', [variantId]).map(_operation).toList();

  @override
  Future<void> updateOperation(OutboxOperation o) async => _db.execute(
        'UPDATE outbox SET status = ?, error_code = ?, error_message = ? WHERE operation_id = ?',
        [o.status.name, o.errorCode, o.errorMessage, o.operationId],
      );

  @override
  Future<void> removeOperations(Iterable<String> operationIds) async {
    final statement = _db.prepare('DELETE FROM outbox WHERE operation_id = ?');
    try {
      for (final id in operationIds) {
        statement.execute([id]);
      }
    } finally {
      statement.close();
    }
  }

  @override
  Future<String?> getSetting(String key) async {
    final rows = _db.select('SELECT value FROM settings WHERE key = ?', [key]);
    return rows.isEmpty ? null : rows.first['value'] as String;
  }

  @override
  Future<void> setSetting(String key, String value) async => _db.execute(
        'INSERT INTO settings (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value',
        [key, value],
      );

  @override
  Future<void> close() async => _db.close();
}
