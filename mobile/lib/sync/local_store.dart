import 'models.dart';

/// The device's local database (PRD 14.2): plans, the outbox of unsent edits, and a few settings.
/// Edits are written here before anything is sent, so nothing is lost when the app closes or the network drops.
abstract class LocalStore {
  Future<void> saveDocument(StoredDocument document);

  Future<StoredDocument?> loadDocument(String variantId);

  Future<List<StoredDocument>> listDocuments();

  /// Appends to the outbox and returns nothing: order is the order of calls.
  Future<void> enqueue(OutboxOperation operation);

  /// All unsent operations of a variant, oldest first (any status).
  Future<List<OutboxOperation>> outbox(String variantId);

  Future<void> updateOperation(OutboxOperation operation);

  Future<void> removeOperations(Iterable<String> operationIds);

  Future<String?> getSetting(String key);

  Future<void> setSetting(String key, String value);

  Future<void> close();
}

class MemoryLocalStore implements LocalStore {
  final Map<String, StoredDocument> _documents = {};
  final List<OutboxOperation> _outbox = [];
  final Map<String, String> _settings = {};

  @override
  Future<void> saveDocument(StoredDocument document) async => _documents[document.variantId] = document;

  @override
  Future<StoredDocument?> loadDocument(String variantId) async => _documents[variantId];

  @override
  Future<List<StoredDocument>> listDocuments() async => _documents.values.toList();

  @override
  Future<void> enqueue(OutboxOperation operation) async => _outbox.add(operation);

  @override
  Future<List<OutboxOperation>> outbox(String variantId) async => _outbox.where((o) => o.variantId == variantId).toList();

  @override
  Future<void> updateOperation(OutboxOperation operation) async {
    final index = _outbox.indexWhere((o) => o.operationId == operation.operationId);
    if (index >= 0) _outbox[index] = operation;
  }

  @override
  Future<void> removeOperations(Iterable<String> operationIds) async {
    final ids = operationIds.toSet();
    _outbox.removeWhere((o) => ids.contains(o.operationId));
  }

  @override
  Future<String?> getSetting(String key) async => _settings[key];

  @override
  Future<void> setSetting(String key, String value) async => _settings[key] = value;

  @override
  Future<void> close() async {}
}
