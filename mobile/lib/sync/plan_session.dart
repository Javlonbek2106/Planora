import 'package:flutter/foundation.dart';

import '../geometry/commands.dart';
import '../geometry/model.dart';
import '../state/plan_controller.dart';
import 'api_client.dart';
import 'local_store.dart';
import 'models.dart';
import 'server_commands.dart';
import 'sync_service.dart';

/// Ties the editor to storage and sync for one plan: every edit is written to the local database and queued in
/// the outbox right away (so it survives a crash or no network), and [syncNow] sends the queue to the server.
class PlanSession extends ChangeNotifier {
  // ignore: prefer_initializing_formals
  PlanSession._({required this.controller, required this.store, required this.service, required StoredDocument document}) : _document = document {
    controller
      ..onExecuted = _executed
      ..onUndone = _undone
      ..onRedone = _redone;
  }

  final PlanController controller;
  final LocalStore store;
  final SyncService service;
  StoredDocument _document;

  /// Outbox operation ids per undoable step, aligned with the controller's undo stack.
  final List<List<String>> _steps = [];
  final List<PlanCommand> _redoSteps = [];
  Future<void> _writes = Future.value();

  SyncStatus status = SyncStatus.upToDate;
  int pendingCount = 0;
  String? message;
  DateTime? lastSyncedAt;

  String get variantId => _document.variantId;

  StoredDocument get document => _document;

  /// Opens the stored plan [variantId] in [controller], with any unsent edits still in the outbox.
  static Future<PlanSession> open({
    required PlanController controller,
    required LocalStore store,
    required SyncService service,
    required String variantId,
  }) async {
    final document = (await store.loadDocument(variantId))!;
    controller
      ..load(document.model)
      ..viewportTouched = false;
    final session = PlanSession._(controller: controller, store: store, service: service, document: document);
    session._queued = (await store.outbox(variantId)).length;
    await session._refreshStatus();
    return session;
  }

  /// Registers a brand-new plan on the server and starts syncing it. A drawing made before connecting is queued
  /// as edits, so it is uploaded by the first sync.
  static Future<PlanSession> connect({
    required PlanController controller,
    required LocalStore store,
    required SyncService service,
    required ApiClient api,
    required String projectName,
  }) async {
    final ref = await api.createProject(projectName);
    final drawn = controller.model;
    final fresh = PlanModel(floorId: ref.floorId);
    await store.saveDocument(StoredDocument(variantId: ref.variantId, projectId: ref.projectId, floorId: ref.floorId, serverVersion: 1, model: fresh));

    final session = await open(controller: controller, store: store, service: service, variantId: ref.variantId);
    // Replay the existing drawing through the controller, so it is validated, stored and queued like any edit.
    for (final command in commandsForModel(drawn)) {
      controller.execute(command);
    }
    await session.flush();
    return session;
  }

  // ---- following the editor ----

  List<OutboxOperation> _operationsFor(PlanCommand command, int outboxSize) {
    final ids = <String>[];
    final operations = <OutboxOperation>[];
    for (final (i, server) in command.toServer(_document.floorId).indexed) {
      final id = randomUuid();
      ids.add(id);
      operations.add(OutboxOperation(
        operationId: id,
        variantId: _document.variantId,
        expectedVersion: _document.serverVersion + outboxSize + i,
        commandType: server['commandType']! as String,
        payload: (server['payload']! as Map).cast<String, Object?>(),
      ));
    }
    _steps.add(ids);
    return operations;
  }

  // Every write goes through one chain so storage sees the edits in the order they were made.
  void _enqueueWrite(Future<void> Function() write) {
    _writes = _writes.then((_) => write()).then((_) => _refreshStatus());
  }

  int _queued = 0;

  void _executed(PlanCommand command) {
    _redoSteps.clear();
    final operations = _operationsFor(command, _queued);
    _queued += operations.length;
    _enqueueWrite(() async {
      for (final o in operations) {
        await store.enqueue(o);
      }
      await _saveModel();
    });
  }

  void _undone(PlanCommand command) {
    final ids = _steps.removeLast();
    _queued -= ids.length;
    _redoSteps.add(command);
    _enqueueWrite(() async {
      await store.removeOperations(ids);
      await _saveModel();
    });
  }

  void _redone(PlanCommand command) {
    _redoSteps.removeLast();
    final operations = _operationsFor(command, _queued);
    _queued += operations.length;
    _enqueueWrite(() async {
      for (final o in operations) {
        await store.enqueue(o);
      }
      await _saveModel();
    });
  }

  Future<void> _saveModel() async {
    _document = _document.copyWith(model: controller.model);
    await store.saveDocument(_document);
  }

  /// Waits until everything the editor did so far is written to the database.
  Future<void> flush() => _writes;

  Future<void> _refreshStatus() async {
    final outbox = await store.outbox(variantId);
    pendingCount = outbox.length;
    if (status != SyncStatus.syncing) {
      final blocked = outbox.where((o) => o.status != OutboxStatus.pending);
      status = blocked.any((o) => o.status == OutboxStatus.conflict)
          ? SyncStatus.conflict
          : blocked.isNotEmpty
              ? SyncStatus.failed
              : (status == SyncStatus.offline && outbox.isNotEmpty ? SyncStatus.offline : (outbox.isEmpty ? SyncStatus.upToDate : SyncStatus.pending));
    }
    notifyListeners();
  }

  // ---- synchronization ----

  Future<void> syncNow() => _run(() => service.sync(variantId));

  Future<void> keepServer() => _run(() => service.keepServer(variantId));

  Future<void> keepMine() => _run(() => service.keepMine(variantId));

  Future<void> _run(Future<SyncReport> Function() action) async {
    // Lock before the first await: an edit slipping in between would be queued after the sync took its snapshot.
    status = SyncStatus.syncing;
    message = null;
    controller.editingLocked = true;
    notifyListeners();

    try {
      await flush();
      final report = await action();
      _document = (await store.loadDocument(variantId))!;
      message = report.message;
      status = report.status;

      if (report.newModel != null) {
        controller.load(report.newModel!);
      } else if (report.pushed > 0 || report.pulled > 0) {
        // These edits are on the server now; taking one back would need an inverse command.
        controller.clearHistory();
      }
      if (report.pushed > 0 || report.pulled > 0 || report.newModel != null) {
        _steps.clear();
        _redoSteps.clear();
      }
      if (report.status == SyncStatus.upToDate) lastSyncedAt = DateTime.now();
    } finally {
      controller.editingLocked = false;
    }

    _queued = (await store.outbox(variantId)).length;
    final reported = status;
    await _refreshStatus();
    // A refusal that is not tied to one queued edit (for example a rejected token) still has to be visible.
    if (reported == SyncStatus.failed && status == SyncStatus.pending) {
      status = SyncStatus.failed;
      notifyListeners();
    }
  }


  @override
  void dispose() {
    controller
      ..onExecuted = null
      ..onUndone = null
      ..onRedone = null;
    super.dispose();
  }
}
