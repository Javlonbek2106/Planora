import '../geometry/model.dart';
import 'api_client.dart';
import 'local_store.dart';
import 'models.dart';
import 'server_commands.dart';

class SyncReport {
  const SyncReport(this.status, {this.pushed = 0, this.pulled = 0, this.newModel, this.message});

  final SyncStatus status;

  /// How many of this device's edits the server accepted.
  final int pushed;

  /// How many edits made elsewhere were applied here.
  final int pulled;

  /// Set when the plan on screen has to be replaced (the server had changes, or the user chose a side).
  final PlanModel? newModel;
  final String? message;
}

/// Sends the outbox, then brings in what changed on the server (PRD 14.2). It never merges geometry on its own:
/// when the server moved on while this device had edits, it stops and reports a conflict.
class SyncService {
  SyncService({required this.api, required this.store, required this.deviceId});

  final ApiClient api;
  final LocalStore store;
  final String deviceId;

  Future<SyncReport> sync(String variantId) async {
    final document = (await store.loadDocument(variantId))!;
    final outbox = await store.outbox(variantId);

    final blocked = outbox.where((o) => o.status != OutboxStatus.pending).toList();
    if (blocked.isNotEmpty) return _blockedReport(blocked);

    try {
      var pushed = 0;
      var current = document;
      if (outbox.isNotEmpty) {
        final push = await _push(current, outbox);
        pushed = push.acknowledged;
        current = push.document;
        if (push.stopped != null) return SyncReport(push.stopped!.status, pushed: pushed, message: push.stopped!.message);
      }

      final pull = await _pull(current);
      return SyncReport(SyncStatus.upToDate, pushed: pushed, pulled: pull.applied, newModel: pull.model, message: pull.message);
    } on OfflineException catch (e) {
      return SyncReport(SyncStatus.offline, message: e.reason);
    } on ApiException catch (e) {
      return SyncReport(SyncStatus.failed, message: '${e.code}: ${e.message}');
    }
  }

  SyncReport _blockedReport(List<OutboxOperation> blocked) {
    final first = blocked.first;
    return SyncReport(
      first.status == OutboxStatus.conflict ? SyncStatus.conflict : SyncStatus.failed,
      message: first.errorMessage,
    );
  }

  Future<({int acknowledged, StoredDocument document, SyncReport? stopped})> _push(StoredDocument document, List<OutboxOperation> outbox) async {
    final results = await api.push(outbox, deviceId: deviceId);
    final byId = {for (final r in results) r.operationId: r};

    final acknowledged = <String>[];
    var version = document.serverVersion;
    SyncReport? stopped;

    for (final operation in outbox) {
      final result = byId[operation.operationId];
      if (stopped == null && result != null && result.acknowledged) {
        acknowledged.add(operation.operationId);
        version = result.version ?? version;
        continue;
      }

      // The first refused edit and everything after it stays in the outbox, marked, for the user to resolve.
      final status = result?.status == 'conflict' ? OutboxStatus.conflict : OutboxStatus.failed;
      stopped ??= SyncReport(
        status == OutboxStatus.conflict ? SyncStatus.conflict : SyncStatus.failed,
        message: result?.message ?? 'The server did not accept this edit.',
      );
      await store.updateOperation(operation.withStatus(status, errorCode: result?.errorCode, errorMessage: result?.message));
    }

    await store.removeOperations(acknowledged);
    final updated = document.copyWith(serverVersion: version);
    await store.saveDocument(updated);
    return (acknowledged: acknowledged.length, document: updated, stopped: stopped);
  }

  /// Brings in changes made elsewhere. Only called when the outbox is empty, so the local plan equals the server's
  /// plan at [StoredDocument.serverVersion] and replaying new commands on it is safe.
  Future<({int applied, PlanModel? model, String? message})> _pull(StoredDocument document) async {
    final pull = await api.pull(document.variantId, document.serverVersion);
    if (!pull.found) throw const ApiException(404, 'NOT_FOUND', 'This plan no longer exists on the server.');
    if (pull.currentVersion == document.serverVersion) return (applied: 0, model: null, message: null);

    PlanModel? replayed;
    if (!pull.snapshotRequired) {
      try {
        replayed = pull.operations.fold<PlanModel>(document.model, (model, op) {
          final command = decodeServerCommand(op.commandType, op.payload);
          if (command == null) throw const FormatException('unsupported');
          return command.apply(model);
        });
      } on Object {
        replayed = null; // unknown command or one that does not fit: reload everything instead
      }
    }

    if (replayed != null) {
      await store.saveDocument(document.copyWith(serverVersion: pull.currentVersion, model: replayed));
      return (applied: pull.operations.length, model: replayed, message: null);
    }

    final reloaded = await _reload(document);
    return (applied: 0, model: reloaded, message: 'Reloaded the plan from the server.');
  }

  Future<PlanModel> _reload(StoredDocument document) async {
    final geometry = await api.geometry(document.variantId);
    final history = await api.measurements(document.projectId, document.variantId);
    final model = modelFromSnapshot(geometry, document.floorId).copyWith(measurements: measurementsFromServer(history));
    await store.saveDocument(document.copyWith(serverVersion: geometry['version']! as int, model: model));
    return model;
  }

  // ---- conflict resolution: the user's explicit choice ----

  /// Drops this device's unsent edits and takes the server's plan.
  Future<SyncReport> keepServer(String variantId) async {
    final document = (await store.loadDocument(variantId))!;
    try {
      await api.resolveKeepServer(variantId, deviceId: deviceId);
      await store.removeOperations([for (final o in await store.outbox(variantId)) o.operationId]);
      final model = await _reload(document);
      return SyncReport(SyncStatus.upToDate, newModel: model, message: 'Kept the server version.');
    } on OfflineException catch (e) {
      return SyncReport(SyncStatus.offline, message: e.reason);
    } on ApiException catch (e) {
      return SyncReport(SyncStatus.failed, message: '${e.code}: ${e.message}');
    }
  }

  /// Replays this device's edits on top of the server's plan, all or nothing.
  Future<SyncReport> keepMine(String variantId) async {
    final document = (await store.loadDocument(variantId))!;
    final outbox = await store.outbox(variantId);
    try {
      final results = await api.resolveKeepMine(variantId, outbox, deviceId: deviceId);
      if (results.any((r) => !r.acknowledged)) {
        final byId = {for (final r in results) r.operationId: r};
        for (final operation in outbox) {
          final result = byId[operation.operationId];
          await store.updateOperation(operation.withStatus(OutboxStatus.failed, errorCode: result?.errorCode, errorMessage: result?.message));
        }
        final reason = results.firstWhere((r) => r.errorCode != 'ROLLED_BACK' && r.errorCode != 'BLOCKED_BY_PREVIOUS_OPERATION', orElse: () => results.first);
        return SyncReport(SyncStatus.failed, message: 'Your edits could not be applied on top of the server version: ${reason.message ?? reason.errorCode}');
      }

      await store.removeOperations([for (final o in outbox) o.operationId]);
      // The server now holds its own changes plus ours: show exactly that.
      final model = await _reload(document);
      return SyncReport(SyncStatus.upToDate, pushed: outbox.length, newModel: model, message: 'Applied your edits on the server version.');
    } on OfflineException catch (e) {
      return SyncReport(SyncStatus.offline, message: e.reason);
    } on ApiException catch (e) {
      return SyncReport(SyncStatus.failed, message: '${e.code}: ${e.message}');
    }
  }
}
