import 'package:flutter/foundation.dart';
import 'package:http/http.dart' as http;

import '../state/plan_controller.dart';
import '../sync/api_client.dart';
import '../sync/local_store.dart';
import '../sync/models.dart';
import '../sync/plan_session.dart';
import '../sync/sync_service.dart';

/// App-level state: the local database, the connection to the server and the open plan's sync session.
/// The editor works without any of this (a plan on screen, nothing saved); connecting adds saving and sync.
class AppController extends ChangeNotifier {
  AppController({required this.store, required this.editor, http.Client Function()? clientFactory})
      : _clientFactory = clientFactory ?? http.Client.new;

  final LocalStore store;
  final PlanController editor;
  final http.Client Function() _clientFactory;

  PlanSession? session;
  String? baseUrl;
  bool connecting = false;
  String? connectionError;

  // The bearer token is kept in memory only: a database file is not a safe place for a credential.
  String? _token;

  static const _baseUrlKey = 'server.baseUrl';
  static const _variantKey = 'plan.variantId';
  static const _deviceKey = 'device.id';

  bool get connected => session != null;

  SyncStatus get status => session?.status ?? SyncStatus.upToDate;

  Future<String> _deviceId() async {
    var id = await store.getSetting(_deviceKey);
    if (id == null) {
      id = 'device-${DateTime.now().microsecondsSinceEpoch.toRadixString(36)}';
      await store.setSetting(_deviceKey, id);
    }
    return id;
  }

  ApiClient _api(String url) => ApiClient(client: _clientFactory(), baseUrl: url, token: () async => _token);

  /// Reopens the plan that was open last time (its unsent edits are still in the outbox).
  Future<void> restore() async {
    final url = await store.getSetting(_baseUrlKey);
    final variantId = await store.getSetting(_variantKey);
    if (url == null || variantId == null || await store.loadDocument(variantId) == null) return;

    baseUrl = url;
    final service = SyncService(api: _api(url), store: store, deviceId: await _deviceId());
    _attach(await PlanSession.open(controller: editor, store: store, service: service, variantId: variantId));
  }

  /// Creates a project on the server for the plan on screen and starts syncing it.
  Future<bool> connect({required String url, String? token, required String projectName}) async {
    connecting = true;
    connectionError = null;
    notifyListeners();
    try {
      _token = token;
      final api = _api(url);
      final service = SyncService(api: api, store: store, deviceId: await _deviceId());
      final opened = await PlanSession.connect(controller: editor, store: store, service: service, api: api, projectName: projectName);

      baseUrl = url;
      await store.setSetting(_baseUrlKey, url);
      await store.setSetting(_variantKey, opened.variantId);
      _attach(opened);
      return true;
    } on OfflineException catch (e) {
      connectionError = 'Cannot reach the server: ${e.reason}';
    } on ApiException catch (e) {
      connectionError = e.status == 401 ? 'The server rejected the token (401).' : '${e.code}: ${e.message}';
    } finally {
      connecting = false;
      notifyListeners();
    }
    return false;
  }

  void setToken(String? token) => _token = token;

  void _attach(PlanSession next) {
    session?.removeListener(notifyListeners);
    session?.dispose();
    session = next..addListener(notifyListeners);
    notifyListeners();
  }

  @override
  void dispose() {
    session?.removeListener(notifyListeners);
    session?.dispose();
    super.dispose();
  }
}
