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
      connectionError = _describe(e);
    } on ApiException catch (e) {
      connectionError = _describe(e);
    } finally {
      connecting = false;
      notifyListeners();
    }
    return false;
  }

  /// The signed-in user's projects on [url]. Throws nothing: a failure is reported through [connectionError].
  Future<List<RemoteProject>?> listProjects({required String url, String? token}) async {
    connecting = true;
    connectionError = null;
    notifyListeners();
    try {
      _token = token;
      return await _api(url).projects();
    } on OfflineException catch (e) {
      connectionError = _describe(e);
    } on ApiException catch (e) {
      connectionError = _describe(e);
    } finally {
      connecting = false;
      notifyListeners();
    }
    return null;
  }

  /// Opens a server project on this device. A plan that is already stored here is opened as it is (its unsent
  /// edits are kept and the next sync reconciles them); otherwise it is downloaded first.
  Future<bool> openProject({required String url, String? token, required RemoteProject project, required RemoteVariant variant}) async {
    final floorId = project.floorId;
    if (floorId == null) {
      connectionError = 'This project has no floor yet, so there is no plan to open.';
      notifyListeners();
      return false;
    }

    connecting = true;
    connectionError = null;
    notifyListeners();
    try {
      _token = token;
      final service = SyncService(api: _api(url), store: store, deviceId: await _deviceId());
      if (await store.loadDocument(variant.id) == null) {
        await service.download(projectId: project.id, variantId: variant.id, floorId: floorId);
      }

      final opened = await PlanSession.open(controller: editor, store: store, service: service, variantId: variant.id);
      baseUrl = url;
      await store.setSetting(_baseUrlKey, url);
      await store.setSetting(_variantKey, variant.id);
      _attach(opened);
      return true;
    } on OfflineException catch (e) {
      connectionError = _describe(e);
    } on ApiException catch (e) {
      connectionError = _describe(e);
    } finally {
      connecting = false;
      notifyListeners();
    }
    return false;
  }

  String _describe(Exception error) => switch (error) {
        OfflineException(:final reason) => 'Cannot reach the server: $reason',
        ApiException(status: 401) => 'The server rejected the token (401).',
        ApiException(:final code, :final message) => '$code: $message',
        _ => 'Unexpected error: $error',
      };

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
