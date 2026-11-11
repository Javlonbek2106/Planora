import 'package:flutter/material.dart';

import '../app/app_controller.dart';
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
