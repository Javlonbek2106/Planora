import '../geometry/model.dart';

/// A plan stored on the device, with the server version it was last in step with.
class StoredDocument {
  const StoredDocument({
    required this.variantId,
    required this.projectId,
    required this.floorId,
    required this.serverVersion,
    required this.model,
  });

  final String variantId;
  final String projectId;
  final String floorId;

  /// The geometry version on the server this device last saw (the base for new operations).
  final int serverVersion;
  final PlanModel model;

  StoredDocument copyWith({int? serverVersion, PlanModel? model}) => StoredDocument(
        variantId: variantId,
        projectId: projectId,
        floorId: floorId,
        serverVersion: serverVersion ?? this.serverVersion,
        model: model ?? this.model,
      );
}

enum OutboxStatus { pending, conflict, failed }

/// One edit waiting to be sent. Edits of a variant are sent in order; a conflict or failure blocks the ones after it.
class OutboxOperation {
  const OutboxOperation({
    required this.operationId,
    required this.variantId,
    required this.expectedVersion,
    required this.commandType,
    required this.payload,
    this.status = OutboxStatus.pending,
    this.errorCode,
    this.errorMessage,
  });

  final String operationId;
  final String variantId;
  final int expectedVersion;
  final String commandType;
  final Map<String, Object?> payload;
  final OutboxStatus status;
  final String? errorCode;
  final String? errorMessage;

  OutboxOperation withStatus(OutboxStatus status, {String? errorCode, String? errorMessage}) => OutboxOperation(
        operationId: operationId,
        variantId: variantId,
        expectedVersion: expectedVersion,
        commandType: commandType,
        payload: payload,
        status: status,
        errorCode: errorCode,
        errorMessage: errorMessage,
      );

  /// The shape the server's `/sync/push` expects.
  Map<String, Object?> toServer() => {
        'operationId': operationId,
        'variantId': variantId,
        'expectedVersion': expectedVersion,
        'commandType': commandType,
        'payload': payload,
      };
}

/// What the user should be told about synchronization.
enum SyncStatus {
  /// Nothing waiting and nothing newer on the server.
  upToDate,

  /// Edits are waiting to be sent (not tried yet, or the device is offline).
  pending,
  syncing,
  offline,

  /// The server changed the same plan meanwhile; the user must choose what to keep.
  conflict,

  /// The server refused an edit (for example the Original Layout is approved and locked).
  failed,
}
