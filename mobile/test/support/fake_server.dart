import 'dart:convert';

import 'package:building_draft_app/geometry/commands.dart';
import 'package:building_draft_app/geometry/model.dart';
import 'package:building_draft_app/geometry/validation.dart';
import 'package:building_draft_app/sync/server_commands.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

/// A stand-in for the backend's sync endpoints that follows the rules the real server was verified to follow:
/// ordered application per variant, expectedVersion checks, idempotent operation ids, the first conflict or failure
/// blocking the rest, all-or-nothing keep_mine. (The live test checks the real server itself.)
class FakeServer {
  FakeServer() : model = PlanModel(floorId: floorId);

  static const floorId = 'f0000000-0000-4000-8000-000000000001';
  static const variantId = 'a0000000-0000-4000-8000-000000000001';
  static const projectId = 'b0000000-0000-4000-8000-000000000001';

  int version = 1;
  PlanModel model;
  bool locked = false; // the Original Layout was approved
  bool offline = false;
  bool snapshotOnlyPull = false;
  int requests = 0;
  final Map<String, int> _applied = {};
  final List<({String type, Map<String, Object?> payload, int version})> log = [];

  late final http.Client client = MockClient(_handle);

  /// An edit made by another device, straight on the server.
  void otherDeviceEdits(PlanCommand command) {
    for (final c in command.toServer(floorId)) {
      model = decodeServerCommand(c['commandType']! as String, (c['payload']! as Map).cast())!.apply(model);
      version++;
      log.add((type: c['commandType']! as String, payload: (c['payload']! as Map).cast(), version: version));
    }
  }

  /// An edit the editor cannot replay (for example a room made on another device).
  void otherDeviceRunsUnknownCommand() {
    version++;
    log.add((type: 'create_room', payload: const {}, version: version));
  }

  Future<http.Response> _handle(http.Request request) async {
    requests++;
    if (offline) throw http.ClientException('Network is unreachable');

    final path = request.url.path;
    final body = request.body.isEmpty ? <String, Object?>{} : (jsonDecode(request.body) as Map).cast<String, Object?>();
    Object result;
    switch ((request.method, path)) {
      case ('POST', '/api/v1/projects'):
        result = {'id': projectId, 'variants': [{'id': variantId}]};
      case ('POST', '/api/v1/projects/$projectId/floors'):
        result = {'id': floorId};
      case ('POST', '/api/v1/sync/push'):
        result = {'results': _push((body['operations']! as List).cast<Map<String, Object?>>(), rebase: false, atomic: false)};
      case ('POST', '/api/v1/sync/pull'):
        result = _pull(body);
      case ('POST', '/api/v1/sync/resolve-conflict'):
        if (body['resolution'] == 'keep_server') {
          result = {'resolution': 'keep_server', 'currentVersion': version, 'results': <Object>[]};
        } else {
          final results = _push((body['operations']! as List).cast<Map<String, Object?>>(), rebase: true, atomic: true);
          result = {'resolution': 'keep_mine', 'currentVersion': version, 'results': results};
        }
      case ('GET', '/api/v1/variants/$variantId/geometry'):
        result = {
          'version': version,
          'nodes': [for (final n in model.nodes.values) {'id': n.id, 'floorId': floorId, 'x': n.x, 'y': n.y}],
          'walls': [
            for (final w in model.walls.values)
              {'id': w.id, 'startNodeId': w.startNodeId, 'endNodeId': w.endNodeId, 'thicknessMm': w.thicknessMm, 'heightMm': w.heightMm}
          ],
        };
      default:
        return http.Response(jsonEncode({'errorCode': 'NOT_FOUND', 'message': 'No route $path'}), 404);
    }
    return http.Response(jsonEncode(result), path.endsWith('/projects') ? 201 : 200, headers: {'content-type': 'application/json'});
  }

  List<Map<String, Object?>> _push(List<Map<String, Object?>> operations, {required bool rebase, required bool atomic}) {
    final results = <Map<String, Object?>>[];
    final snapshot = (model: model, version: version, applied: {..._applied}, logLength: log.length);
    String? blocked;

    for (final op in operations) {
      final id = op['operationId']! as String;
      if (blocked != null) {
        results.add({'operationId': id, 'status': blocked, 'currentVersion': version, 'errorCode': 'BLOCKED_BY_PREVIOUS_OPERATION', 'message': 'A previous operation was not applied.'});
        continue;
      }
      if (_applied.containsKey(id)) {
        results.add({'operationId': id, 'status': 'acknowledged', 'version': _applied[id]});
        continue;
      }
      if (locked) {
        blocked = 'failed';
        results.add({'operationId': id, 'status': 'failed', 'errorCode': 'ORIGINAL_LAYOUT_PROTECTED', 'message': 'The Original Layout is approved and cannot be changed.'});
        continue;
      }
      final expected = rebase ? version : op['expectedVersion']! as int;
      if (expected != version) {
        blocked = 'conflict';
        results.add({'operationId': id, 'status': 'conflict', 'currentVersion': version, 'errorCode': 'VERSION_CONFLICT', 'message': 'Expected version $expected, but the server is at $version.'});
        continue;
      }
      final command = decodeServerCommand(op['commandType']! as String, (op['payload']! as Map).cast());
      PlanModel? next;
      try {
        next = command?.apply(model);
      } on PlanException {
        next = null;
      }
      if (next == null || validatePlan(next).isNotEmpty) {
        blocked = 'failed';
        results.add({'operationId': id, 'status': 'failed', 'errorCode': 'GEOMETRY_VALIDATION_FAILED', 'message': 'The resulting geometry is not valid.'});
        continue;
      }
      model = next;
      version++;
      _applied[id] = version;
      log.add((type: op['commandType']! as String, payload: (op['payload']! as Map).cast(), version: version));
      results.add({'operationId': id, 'status': 'acknowledged', 'version': version});
    }

    if (atomic && results.any((r) => r['status'] != 'acknowledged')) {
      model = snapshot.model;
      version = snapshot.version;
      _applied
        ..clear()
        ..addAll(snapshot.applied);
      log.removeRange(snapshot.logLength, log.length);
      return [
        for (final r in results)
          r['status'] == 'acknowledged' ? {'operationId': r['operationId'], 'status': 'failed', 'errorCode': 'ROLLED_BACK', 'message': 'Another operation in the batch failed, so nothing was applied.'} : r,
      ];
    }
    return results;
  }

  Map<String, Object?> _pull(Map<String, Object?> body) {
    final cursor = (body['variants']! as List).cast<Map<String, Object?>>().single;
    final since = cursor['version']! as int;
    if (since >= version) return {'variants': [{'variantId': variantId, 'found': true, 'currentVersion': version, 'snapshotRequired': false, 'operations': <Object>[]}]};

    final missed = log.where((e) => e.version > since).toList();
    final replayable = !snapshotOnlyPull && missed.length == version - since && missed.every((e) => e.type != 'create_room');
    return {
      'variants': [
        {
          'variantId': variantId,
          'found': true,
          'currentVersion': version,
          'snapshotRequired': !replayable,
          'operations': replayable ? [for (final e in missed) {'operationId': 'x', 'commandType': e.type, 'payload': e.payload, 'resultVersion': e.version}] : <Object>[],
        }
      ]
    };
  }
}
