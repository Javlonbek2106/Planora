import 'dart:async';
import 'dart:convert';

import 'package:http/http.dart' as http;

import 'models.dart';

/// The server answered, but refused (its error contract: errorCode and message).
class ApiException implements Exception {
  const ApiException(this.status, this.code, this.message);

  final int status;
  final String code;
  final String message;

  @override
  String toString() => 'ApiException($status, $code): $message';
}

/// The server could not be reached (no network, timeout). Work stays queued and is sent later.
class OfflineException implements Exception {
  const OfflineException(this.reason);

  final String reason;

  @override
  String toString() => 'Offline: $reason';
}

class PushResult {
  const PushResult({required this.operationId, required this.status, this.version, this.currentVersion, this.errorCode, this.message});

  final String operationId;
  final String status; // acknowledged | conflict | failed
  final int? version;
  final int? currentVersion;
  final String? errorCode;
  final String? message;

  bool get acknowledged => status == 'acknowledged';

  factory PushResult.fromJson(Map<String, Object?> json) => PushResult(
        operationId: json['operationId']! as String,
        status: json['status']! as String,
        version: json['version'] as int?,
        currentVersion: json['currentVersion'] as int?,
        errorCode: json['errorCode'] as String?,
        message: json['message'] as String?,
      );
}

class ServerOperation {
  const ServerOperation(this.commandType, this.payload, this.resultVersion);

  final String commandType;
  final Map<String, Object?> payload;
  final int resultVersion;
}

class PullResult {
  const PullResult({required this.found, required this.currentVersion, required this.snapshotRequired, required this.operations});

  final bool found;
  final int currentVersion;
  final bool snapshotRequired;
  final List<ServerOperation> operations;
}

class ProjectRef {
  const ProjectRef(this.projectId, this.variantId, this.floorId);

  final String projectId;
  final String variantId;
  final String floorId;
}

/// Thin client for the backend's `/api/v1`. It knows the routes and the error contract, nothing about plans.
class ApiClient {
  ApiClient({required this.client, required this.baseUrl, this.token, this.timeout = const Duration(seconds: 20)});

  final http.Client client;
  final String baseUrl;

  /// Supplies the bearer token (or null in the server's dev mode).
  final Future<String?> Function()? token;
  final Duration timeout;

  Future<Map<String, Object?>> _send(String method, String path, [Object? body]) async {
    final headers = {'Content-Type': 'application/json', 'Accept': 'application/json'};
    final bearer = await token?.call();
    if (bearer != null && bearer.isNotEmpty) headers['Authorization'] = 'Bearer $bearer';

    final uri = Uri.parse('${baseUrl.replaceAll(RegExp(r'/+$'), '')}$path');
    final request = http.Request(method, uri)..headers.addAll(headers);
    if (body != null) request.body = jsonEncode(body);

    http.Response response;
    try {
      response = await http.Response.fromStream(await client.send(request).timeout(timeout));
    } on http.ClientException catch (e) {
      throw OfflineException(e.message);
    } on TimeoutException {
      throw const OfflineException('The server did not answer in time.');
    }

    final decoded = response.body.isEmpty ? null : jsonDecode(response.body);
    if (response.statusCode >= 200 && response.statusCode < 300) return (decoded as Map?)?.cast<String, Object?>() ?? {};

    final error = decoded is Map ? decoded.cast<String, Object?>() : const <String, Object?>{};
    throw ApiException(
      response.statusCode,
      (error['errorCode'] ?? 'HTTP_${response.statusCode}') as String,
      (error['message'] ?? error['title'] ?? 'Request failed (${response.statusCode}).') as String,
    );
  }

  Future<ProjectRef> createProject(String name) async {
    final project = await _send('POST', '/api/v1/projects', {'name': name});
    final projectId = project['id']! as String;
    final variantId = ((project['variants']! as List).first as Map)['id']! as String;
    final floor = await _send('POST', '/api/v1/projects/$projectId/floors', {'floorNumber': 1});
    return ProjectRef(projectId, variantId, floor['id']! as String);
  }

  Future<List<PushResult>> push(List<OutboxOperation> operations, {String? deviceId}) async {
    final response = await _send('POST', '/api/v1/sync/push', {'deviceId': deviceId, 'operations': [for (final o in operations) o.toServer()]});
    return [for (final r in (response['results']! as List).cast<Map<String, Object?>>()) PushResult.fromJson(r)];
  }

  Future<PullResult> pull(String variantId, int version) async {
    final response = await _send('POST', '/api/v1/sync/pull', {
      'variants': [
        {'variantId': variantId, 'version': version}
      ]
    });
    final changes = (response['variants']! as List).cast<Map<String, Object?>>().single;
    return PullResult(
      found: changes['found']! as bool,
      currentVersion: changes['currentVersion']! as int,
      snapshotRequired: changes['snapshotRequired']! as bool,
      operations: [
        for (final o in (changes['operations']! as List).cast<Map<String, Object?>>())
          ServerOperation(o['commandType']! as String, (o['payload']! as Map).cast<String, Object?>(), o['resultVersion']! as int),
      ],
    );
  }

  Future<int> resolveKeepServer(String variantId, {String? deviceId}) async {
    final response = await _send('POST', '/api/v1/sync/resolve-conflict', {'variantId': variantId, 'resolution': 'keep_server', 'deviceId': deviceId});
    return response['currentVersion']! as int;
  }

  Future<List<PushResult>> resolveKeepMine(String variantId, List<OutboxOperation> operations, {String? deviceId}) async {
    final response = await _send('POST', '/api/v1/sync/resolve-conflict', {
      'variantId': variantId,
      'resolution': 'keep_mine',
      'deviceId': deviceId,
      'operations': [for (final o in operations) o.toServer()],
    });
    return [for (final r in (response['results']! as List).cast<Map<String, Object?>>()) PushResult.fromJson(r)];
  }

  /// The measurement history of a project's variant.
  Future<List<Map<String, Object?>>> measurements(String projectId, String variantId) async {
    final path = '/api/v1/projects/$projectId/measurements?variantId=$variantId';
    final headers = {'Accept': 'application/json'};
    final bearer = await token?.call();
    if (bearer != null && bearer.isNotEmpty) headers['Authorization'] = 'Bearer $bearer';
    final uri = Uri.parse('${baseUrl.replaceAll(RegExp(r'/+$'), '')}$path');

    http.Response response;
    try {
      response = await client.get(uri, headers: headers).timeout(timeout);
    } on http.ClientException catch (e) {
      throw OfflineException(e.message);
    } on TimeoutException {
      throw const OfflineException('The server did not answer in time.');
    }
    if (response.statusCode != 200) throw ApiException(response.statusCode, 'HTTP_${response.statusCode}', 'Could not load measurements.');
    return (jsonDecode(response.body) as List).cast<Map<String, Object?>>();
  }

  /// The full geometry of a variant: `{ version, nodes, walls, ... }`.
  Future<Map<String, Object?>> geometry(String variantId) => _send('GET', '/api/v1/variants/$variantId/geometry');
}
