import 'package:flutter/material.dart';

import '../app/app_controller.dart';
import '../sync/api_client.dart';
import '../sync/models.dart';
import '../sync/plan_session.dart';

String describeStatus(SyncStatus status) => switch (status) {
      SyncStatus.upToDate => 'Up to date',
      SyncStatus.pending => 'Waiting to sync',
      SyncStatus.syncing => 'Syncing...',
      SyncStatus.offline => 'Offline (changes are saved on this device)',
      SyncStatus.conflict => 'Conflict: choose which version to keep',
      SyncStatus.failed => 'The server refused a change',
    };

/// The cloud button in the app bar: its icon shows the sync state and a badge counts unsent edits.
class SyncButton extends StatelessWidget {
  const SyncButton({super.key, required this.app, required this.onPressed});

  final AppController app;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final session = app.session;
    if (session == null) {
      return IconButton(key: const ValueKey('action-sync'), tooltip: 'Connect to a server to save and sync', icon: const Icon(Icons.cloud_off_outlined), onPressed: onPressed);
    }

    final status = session.status;
    final icon = switch (status) {
      SyncStatus.upToDate => Icon(Icons.cloud_done_outlined, color: scheme.primary),
      SyncStatus.pending => const Icon(Icons.cloud_upload_outlined),
      SyncStatus.syncing => const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2)),
      SyncStatus.offline => const Icon(Icons.cloud_off_outlined),
      SyncStatus.conflict => Icon(Icons.warning_amber_rounded, color: scheme.error),
      SyncStatus.failed => Icon(Icons.error_outline, color: scheme.error),
    };

    return IconButton(
      key: const ValueKey('action-sync'),
      tooltip: describeStatus(status),
      onPressed: status == SyncStatus.syncing ? null : onPressed,
      icon: session.pendingCount > 0 ? Badge(key: const ValueKey('sync-badge'), label: Text('${session.pendingCount}'), child: icon) : icon,
    );
  }
}

Future<void> showConnectDialog(BuildContext context, AppController app) {
  final url = TextEditingController(text: app.baseUrl ?? 'http://localhost:5276');
  final token = TextEditingController();
  final name = TextEditingController(text: 'My apartment');

  return showDialog<void>(
    context: context,
    builder: (dialogContext) => ListenableBuilder(
      listenable: app,
      builder: (context, _) => AlertDialog(
        title: const Text('Connect to a server'),
        content: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 380),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              const Text('The plan on screen is saved on this device and sent to a new project on the server. After that it can be edited offline.'),
              const SizedBox(height: 12),
              TextField(key: const ValueKey('connect-url'), controller: url, decoration: const InputDecoration(labelText: 'Server address')),
              TextField(key: const ValueKey('connect-token'), controller: token, obscureText: true, decoration: const InputDecoration(labelText: 'Access token (if the server needs one)')),
              TextField(key: const ValueKey('connect-name'), controller: name, decoration: const InputDecoration(labelText: 'Project name')),
              if (app.connectionError != null)
                Padding(
                  padding: const EdgeInsets.only(top: 12),
                  child: Text(app.connectionError!, key: const ValueKey('connect-error'), style: TextStyle(color: Theme.of(context).colorScheme.error)),
                ),
            ],
          ),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(dialogContext), child: const Text('Cancel')),
          FilledButton(
            key: const ValueKey('connect-submit'),
            onPressed: app.connecting
                ? null
                : () async {
                    final ok = await app.connect(url: url.text.trim(), token: token.text.trim().isEmpty ? null : token.text.trim(), projectName: name.text.trim());
                    if (ok && dialogContext.mounted) Navigator.pop(dialogContext);
                  },
            child: Text(app.connecting ? 'Connecting...' : 'Connect'),
          ),
        ],
      ),
    ),
  );
}

Future<void> showProjectsDialog(BuildContext context, AppController app) =>
    showDialog<void>(context: context, builder: (_) => _ProjectsDialog(app: app));

/// Lists the user's projects on a server and opens one on this device.
class _ProjectsDialog extends StatefulWidget {
  const _ProjectsDialog({required this.app});

  final AppController app;

  @override
  State<_ProjectsDialog> createState() => _ProjectsDialogState();
}

class _ProjectsDialogState extends State<_ProjectsDialog> {
  late final url = TextEditingController(text: widget.app.baseUrl ?? 'http://localhost:5276');
  final token = TextEditingController();
  List<RemoteProject>? projects;

  AppController get app => widget.app;

  String? get _token => token.text.trim().isEmpty ? null : token.text.trim();

  // Opening a project replaces the plan on screen. A plan that is already saved is safe (it is stored on the
  // device); a drawing that was never connected would be lost, so the user is told.
  bool get _replacesUnsavedDrawing => !app.connected && app.editor.model.walls.isNotEmpty;

  Future<void> _load() async {
    final loaded = await app.listProjects(url: url.text.trim(), token: _token);
    if (mounted) setState(() => projects = loaded);
  }

  Future<void> _open(RemoteProject project) async {
    final variant = project.variants.length == 1 ? project.variants.single : await _chooseVariant(project);
    if (variant == null) return;
    final ok = await app.openProject(url: url.text.trim(), token: _token, project: project, variant: variant);
    if (ok && mounted) Navigator.pop(context);
  }

  Future<RemoteVariant?> _chooseVariant(RemoteProject project) => showDialog<RemoteVariant>(
        context: context,
        builder: (context) => SimpleDialog(
          key: const ValueKey('variant-picker'),
          title: const Text('Which variant?'),
          children: [
            for (final v in project.variants)
              SimpleDialogOption(
                key: ValueKey('variant-${v.id}'),
                onPressed: () => Navigator.pop(context, v),
                child: Text('${v.name}  (v${v.currentVersion}${v.isLocked ? ', locked' : ''})'),
              ),
          ],
        ),
      );

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: app,
      builder: (context, _) => AlertDialog(
        title: const Text('Open a project'),
        content: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 380),
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                TextField(key: const ValueKey('projects-url'), controller: url, decoration: const InputDecoration(labelText: 'Server address')),
                TextField(key: const ValueKey('projects-token'), controller: token, obscureText: true, decoration: const InputDecoration(labelText: 'Access token (if the server needs one)')),
                if (_replacesUnsavedDrawing)
                  Padding(
                    padding: const EdgeInsets.only(top: 12),
                    child: Text('The drawing on screen is not saved anywhere. Opening a project replaces it.', key: const ValueKey('projects-warning'), style: TextStyle(color: Theme.of(context).colorScheme.error)),
                  ),
                if (app.connectionError != null)
                  Padding(
                    padding: const EdgeInsets.only(top: 12),
                    child: Text(app.connectionError!, key: const ValueKey('projects-error'), style: TextStyle(color: Theme.of(context).colorScheme.error)),
                  ),
                if (projects != null && projects!.isEmpty) const Padding(padding: EdgeInsets.only(top: 12), child: Text('No projects on this server yet.', key: ValueKey('projects-empty'))),
                for (final p in projects ?? const <RemoteProject>[])
                  ListTile(
                    key: ValueKey('project-${p.id}'),
                    contentPadding: EdgeInsets.zero,
                    title: Text(p.name),
                    subtitle: Text([if (p.address != null) p.address!, '${p.variants.length} variant(s)', if (p.floorId == null) 'no floor'].join(' · ')),
                    trailing: const Icon(Icons.chevron_right),
                    enabled: !app.connecting && p.floorId != null,
                    onTap: () => _open(p),
                  ),
              ],
            ),
          ),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context), child: const Text('Close')),
          FilledButton(key: const ValueKey('projects-load'), onPressed: app.connecting ? null : _load, child: Text(app.connecting ? 'Working...' : (projects == null ? 'Load projects' : 'Reload'))),
        ],
      ),
    );
  }
}

/// Shown when the server's plan changed while this device had unsent edits. The user decides; nothing is merged
/// behind their back.
Future<void> showConflictDialog(BuildContext context, PlanSession session) {
  return showDialog<void>(
    context: context,
    builder: (dialogContext) => AlertDialog(
      key: const ValueKey('conflict-dialog'),
      title: const Text('This plan was changed elsewhere'),
      content: Text(
        'The version on the server is newer than the one you started from, and you have ${session.pendingCount} unsent '
        'change(s).\n\n'
        'Keep server version: your unsent changes are discarded.\n'
        'Keep my changes: they are applied on top of the server version, all or nothing.',
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(dialogContext), child: const Text('Decide later')),
        TextButton(
          key: const ValueKey('conflict-keep-server'),
          onPressed: () {
            Navigator.pop(dialogContext);
            session.keepServer();
          },
          child: const Text('Keep server version'),
        ),
        FilledButton(
          key: const ValueKey('conflict-keep-mine'),
          onPressed: () {
            Navigator.pop(dialogContext);
            session.keepMine();
          },
          child: const Text('Keep my changes'),
        ),
      ],
    ),
  );
}
