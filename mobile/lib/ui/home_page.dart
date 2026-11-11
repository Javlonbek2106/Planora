import 'package:flutter/material.dart';

import '../app/app_controller.dart';
import '../demo/demo_plans.dart';
import '../geometry/model.dart';
import '../state/plan_controller.dart';
import '../sync/models.dart';
import '../sync/plan_session.dart';
import 'plan_canvas.dart';
import 'sync_widgets.dart';

/// The 2D Constructor screen. Wide screens (tablet) get a tool rail on the left and a properties panel on the
/// right; narrow screens (phone) get the canvas with a toolbar at the bottom.
class HomePage extends StatefulWidget {
  const HomePage({super.key, this.controller, this.app});

  final PlanController? controller;

  /// Storage and sync; without it the editor works on its own and nothing is saved.
  final AppController? app;

  @override
  State<HomePage> createState() => _HomePageState();
}

class _HomePageState extends State<HomePage> {
  late final PlanController c = widget.controller ?? widget.app?.editor ?? PlanController();

  static const wideBreakpoint = 720.0;

  @override
  void dispose() {
    if (widget.controller == null && widget.app == null) c.dispose();
    super.dispose();
  }

  void _load(Size size, PlanModel plan) {
    c.load(plan);
    c.viewportTouched = false;
    c.fitToContent(size);
  }

  Future<void> _syncPressed() async {
    final app = widget.app;
    if (app == null) return;
    if (!app.connected) return showConnectDialog(context, app);

    final session = app.session!;
    if (session.status != SyncStatus.conflict) await session.syncNow();
    if (!mounted) return;
    if (session.status == SyncStatus.conflict) return showConflictDialog(context, session);

    final message = session.message;
    if (message != null && session.status != SyncStatus.upToDate) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(message)));
    }
  }

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(builder: (context, constraints) {
      final wide = constraints.maxWidth >= wideBreakpoint;
      return ListenableBuilder(
        listenable: Listenable.merge([c, if (widget.app != null) widget.app!]),
        builder: (context, _) => Scaffold(
          appBar: AppBar(
            title: const Text('Building Draft'),
            actions: [
              if (widget.app != null) SyncButton(app: widget.app!, onPressed: _syncPressed),
              IconButton(key: const ValueKey('action-undo'), tooltip: 'Undo', icon: const Icon(Icons.undo), onPressed: c.canUndo ? c.undo : null),
              IconButton(key: const ValueKey('action-redo'), tooltip: 'Redo', icon: const Icon(Icons.redo), onPressed: c.canRedo ? c.redo : null),
              IconButton(
                key: const ValueKey('action-grid'),
                tooltip: c.snapToGrid ? 'Grid snapping on' : 'Grid snapping off',
                icon: Icon(c.snapToGrid ? Icons.grid_on : Icons.grid_off),
                onPressed: c.toggleGrid,
              ),
              IconButton(
                key: const ValueKey('action-fit'),
                tooltip: 'Fit plan to screen',
                icon: const Icon(Icons.fit_screen),
                onPressed: () {
                  c.viewportTouched = false;
                  c.fitToContent(constraints.biggest);
                },
              ),
              PopupMenuButton<String>(
                key: const ValueKey('action-demo'),
                tooltip: 'Demo plans',
                enabled: !(widget.app?.connected ?? false), // loading a demo would replace a plan that is being synced
                onSelected: (value) => _load(constraints.biggest, value == 'apartment' ? DemoPlans.apartment() : DemoPlans.lattice()),
                itemBuilder: (_) => const [
                  PopupMenuItem(value: 'apartment', child: Text('Demo: one-bedroom flat')),
                  PopupMenuItem(value: 'lattice', child: Text('Stress: 220 walls')),
                ],
              ),
            ],
          ),
          body: wide ? _wideBody() : _narrowBody(),
        ),
      );
    });
  }

  Widget _canvas() => Stack(
        children: [
          Positioned.fill(child: PlanCanvas(controller: c)),
          Positioned(left: 12, top: 12, right: 12, child: _status()),
        ],
      );

  Widget _status() {
    final scheme = Theme.of(context).colorScheme;
    if (c.lastError != null) {
      return Material(
        key: const ValueKey('error-banner'),
        color: scheme.errorContainer,
        borderRadius: BorderRadius.circular(8),
        child: ListTile(
          dense: true,
          leading: Icon(Icons.error_outline, color: scheme.onErrorContainer),
          title: Text(c.lastError!, style: TextStyle(color: scheme.onErrorContainer)),
          trailing: IconButton(icon: const Icon(Icons.close), onPressed: c.clearError),
        ),
      );
    }
    final hint = switch (c.tool) {
      PlanTool.wall => c.draftStart == null ? 'Wall: tap the first corner, or drag to draw one wall' : 'Tap the next corner (tap the start again to close)',
      PlanTool.select => 'Select: tap a wall or corner, drag a corner to move it, drag empty space to pan',
    };
    return Text(hint, key: const ValueKey('hint'), style: TextStyle(color: scheme.onSurfaceVariant));
  }

  Widget _tools({required Axis axis}) {
    final buttons = [
      _ToolButton(
        buttonKey: const ValueKey('tool-select'),
        icon: Icons.near_me,
        label: 'Select',
        selected: c.tool == PlanTool.select,
        onPressed: () => c.selectTool(PlanTool.select),
      ),
      _ToolButton(
        buttonKey: const ValueKey('tool-wall'),
        icon: Icons.horizontal_rule,
        label: 'Wall',
        selected: c.tool == PlanTool.wall,
        onPressed: () => c.selectTool(PlanTool.wall),
      ),
      _ToolButton(
        buttonKey: const ValueKey('action-delete'),
        icon: Icons.delete_outline,
        label: 'Delete',
        selected: false,
        onPressed: c.selectedWallId != null || c.selectedNodeId != null ? c.deleteSelection : null,
      ),
    ];
    return axis == Axis.vertical
        ? Column(mainAxisSize: MainAxisSize.min, children: buttons)
        : Row(mainAxisAlignment: MainAxisAlignment.spaceEvenly, children: buttons);
  }

  Widget _wideBody() => Row(
        key: const ValueKey('layout-wide'),
        children: [
          SizedBox(width: 88, child: Padding(padding: const EdgeInsets.symmetric(vertical: 12), child: Align(alignment: Alignment.topCenter, child: _tools(axis: Axis.vertical)))),
          const VerticalDivider(width: 1),
          Expanded(child: _canvas()),
          const VerticalDivider(width: 1),
          SizedBox(width: 280, child: PropertiesPanel(controller: c, session: widget.app?.session)),
        ],
      );

  Widget _narrowBody() => Column(
        key: const ValueKey('layout-narrow'),
        children: [
          Expanded(child: _canvas()),
          const Divider(height: 1),
          SafeArea(top: false, child: Padding(padding: const EdgeInsets.symmetric(vertical: 4), child: _tools(axis: Axis.horizontal))),
        ],
      );
}

class _ToolButton extends StatelessWidget {
  const _ToolButton({required this.buttonKey, required this.icon, required this.label, required this.selected, required this.onPressed});

  final Key buttonKey;
  final IconData icon;
  final String label;
  final bool selected;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) {
    return Tooltip(
      message: label,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 2),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            IconButton.filledTonal(key: buttonKey, isSelected: selected, selectedIcon: Icon(icon), icon: Icon(icon), onPressed: onPressed),
            Text(label, style: Theme.of(context).textTheme.labelSmall),
          ],
        ),
      ),
    );
  }
}

/// Details of the selection (tablet layout), plus how much has not been synced yet.
class PropertiesPanel extends StatelessWidget {
  const PropertiesPanel({super.key, required this.controller, this.session});

  final PlanController controller;
  final PlanSession? session;

  @override
  Widget build(BuildContext context) {
    final c = controller;
    final m = c.model;
    final wall = c.selectedWallId == null ? null : m.walls[c.selectedWallId];
    final node = c.selectedNodeId == null ? null : m.nodes[c.selectedNodeId];
    final style = Theme.of(context).textTheme;

    String mm(double v) => '${v.toStringAsFixed(0)} mm';

    return ListView(
      key: const ValueKey('properties-panel'),
      padding: const EdgeInsets.all(16),
      children: [
        Text('Properties', style: style.titleMedium),
        const SizedBox(height: 12),
        if (wall != null) ...[
          Text('Wall', style: style.labelLarge),
          Text('Length: ${mm(m.length(wall))}', key: const ValueKey('prop-length')),
          Text('Thickness: ${mm(wall.thicknessMm)}'),
          Text('Height: ${mm(wall.heightMm)}'),
          Text('From (${m.nodes[wall.startNodeId]!.x.toStringAsFixed(0)}, ${m.nodes[wall.startNodeId]!.y.toStringAsFixed(0)})'),
          Text('To (${m.nodes[wall.endNodeId]!.x.toStringAsFixed(0)}, ${m.nodes[wall.endNodeId]!.y.toStringAsFixed(0)})'),
        ] else if (node != null) ...[
          Text('Corner', style: style.labelLarge),
          Text('X: ${mm(node.x)}'),
          Text('Y: ${mm(node.y)}'),
          Text('Walls: ${m.wallsAt(node.id).length}'),
        ] else
          const Text('Nothing selected'),
        const Divider(height: 32),
        Text('Plan', style: style.labelLarge),
        Text('${m.walls.length} walls, ${m.nodes.length} corners', key: const ValueKey('prop-summary')),
        if (session == null)
          Text('Not synced yet: ${c.serverCommands.length} command(s)', key: const ValueKey('prop-pending'))
        else ...[
          Text('Sync: ${describeStatus(session!.status)}', key: const ValueKey('prop-sync')),
          Text('Unsent changes: ${session!.pendingCount}', key: const ValueKey('prop-pending')),
          if (session!.message != null) Text(session!.message!, style: TextStyle(color: Theme.of(context).colorScheme.onSurfaceVariant)),
        ],
      ],
    );
  }
}
