import 'package:flutter/material.dart';

import '../geometry/model.dart';
import '../state/plan_controller.dart';

/// Room types offered when naming a room (PRD 10.1). Balconies and the like are open areas: they are counted
/// separately from the enclosed area and never added to it.
const roomTypes = <({String value, String label, bool open})>[
  (value: 'living_room', label: 'Living room', open: false),
  (value: 'bedroom', label: 'Bedroom', open: false),
  (value: 'childrens_room', label: "Children's room", open: false),
  (value: 'kitchen', label: 'Kitchen', open: false),
  (value: 'bathroom', label: 'Bathroom', open: false),
  (value: 'toilet', label: 'Toilet', open: false),
  (value: 'corridor', label: 'Corridor', open: false),
  (value: 'dressing_room', label: 'Dressing room', open: false),
  (value: 'storage', label: 'Storage', open: false),
  (value: 'study', label: 'Study', open: false),
  (value: 'balcony', label: 'Balcony', open: true),
  (value: 'loggia', label: 'Loggia', open: true),
  (value: 'terrace', label: 'Terrace', open: true),
  (value: 'other', label: 'Other', open: false),
];

/// Names the closed area the user tapped with the room tool and makes it a room.
Future<void> showRoomDialog(BuildContext context, PlanController c) async {
  final proposal = c.roomProposal;
  if (proposal == null) return;

  var type = roomTypes.first;
  final name = TextEditingController(text: type.label);

  await showDialog<void>(
    context: context,
    barrierDismissible: false,
    builder: (dialogContext) => StatefulBuilder(
      builder: (context, setState) => AlertDialog(
        key: const ValueKey('room-dialog'),
        title: const Text('New room'),
        content: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 340),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextField(key: const ValueKey('room-name'), controller: name, autofocus: true, decoration: const InputDecoration(labelText: 'Name')),
              const SizedBox(height: 12),
              DropdownButtonFormField<String>(
                key: const ValueKey('room-type'),
                isExpanded: true,
                initialValue: type.value,
                decoration: const InputDecoration(labelText: 'Type'),
                items: [for (final t in roomTypes) DropdownMenuItem(value: t.value, child: Text(t.open ? '${t.label} (open area)' : t.label))],
                onChanged: (value) => setState(() {
                  final previous = type;
                  type = roomTypes.firstWhere((t) => t.value == value);
                  if (name.text.trim().isEmpty || name.text == previous.label) name.text = type.label;
                }),
              ),
              if (type.open)
                const Padding(
                  padding: EdgeInsets.only(top: 12),
                  child: Text('Counted separately from the enclosed area.'),
                ),
            ],
          ),
        ),
        actions: [
          TextButton(
            key: const ValueKey('room-cancel'),
            onPressed: () {
              c.cancelRoomProposal();
              Navigator.pop(dialogContext);
            },
            child: const Text('Cancel'),
          ),
          FilledButton(
            key: const ValueKey('room-create'),
            onPressed: () {
              if (c.createRoom(name.text, roomType: type.value, areaType: type.open ? RoomAreaType.openArea : RoomAreaType.enclosed)) {
                Navigator.pop(dialogContext);
              }
            },
            child: const Text('Create room'),
          ),
        ],
      ),
    ),
  );
}

/// Edits the size and position of the selected door or window. The dialog stays open with the reason when the new
/// values do not fit (too wide for the wall, overlapping another opening).
Future<void> showOpeningDialog(BuildContext context, PlanController c) async {
  final opening = c.selectedOpeningId == null ? null : c.model.openings[c.selectedOpeningId];
  if (opening == null) return;

  final isWindow = opening.kind == OpeningKind.window;
  String text(double v) => v.round().toString();
  final width = TextEditingController(text: text(opening.widthMm));
  final height = TextEditingController(text: text(opening.heightMm));
  final sill = TextEditingController(text: text(opening.sillMm));
  final offset = TextEditingController(text: text(opening.offsetMm));
  String? problem;

  double? number(TextEditingController field) => double.tryParse(field.text.replaceAll(',', '.'));

  Widget field(String key, TextEditingController controller, String label, StateSetter setState) => TextField(
        key: ValueKey(key),
        controller: controller,
        keyboardType: const TextInputType.numberWithOptions(decimal: true),
        decoration: InputDecoration(labelText: label, suffixText: 'mm'),
        onChanged: (_) => setState(() => problem = null),
      );

  await showDialog<void>(
    context: context,
    builder: (dialogContext) => StatefulBuilder(
      builder: (context, setState) {
        final w = number(width), h = number(height), s = number(sill), o = number(offset);
        final valid = w != null && h != null && s != null && o != null && w > 0 && h > 0 && s >= 0 && o >= 0;
        return AlertDialog(
          key: const ValueKey('opening-dialog'),
          title: Text(isWindow ? 'Edit window' : 'Edit door'),
          content: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 340),
            child: SingleChildScrollView(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  field('opening-width', width, 'Width', setState),
                  field('opening-height', height, 'Height', setState),
                  if (isWindow) field('opening-sill', sill, 'Sill height (from the floor)', setState),
                  field('opening-offset', offset, 'Distance from the wall start', setState),
                  if (problem != null)
                    Padding(
                      padding: const EdgeInsets.only(top: 12),
                      child: Text(problem!, key: const ValueKey('opening-error'), style: TextStyle(color: Theme.of(context).colorScheme.error)),
                    ),
                ],
              ),
            ),
          ),
          actions: [
            TextButton(onPressed: () => Navigator.pop(dialogContext), child: const Text('Cancel')),
            FilledButton(
              key: const ValueKey('opening-apply'),
              onPressed: !valid
                  ? null
                  : () {
                      if (c.resizeOpening(widthMm: w, heightMm: h, sillMm: isWindow ? s : opening.sillMm, offsetMm: o)) {
                        Navigator.pop(dialogContext);
                      } else {
                        setState(() => problem = c.lastError);
                      }
                    },
              child: const Text('Apply'),
            ),
          ],
        );
      },
    ),
  );
}

/// Enters a measured length for the selected wall (a laser value or a tape measure reading).
Future<void> showMeasureDialog(BuildContext context, PlanController c) async {
  final wall = c.selectedWallId == null ? null : c.model.walls[c.selectedWallId];
  if (wall == null) return;

  final centerline = c.model.length(wall);
  var inner = true;
  var source = MeasurementSource.laserManual;
  var verified = false;
  final notes = TextEditingController();
  final value = TextEditingController(text: (centerline - wall.thicknessMm).round().toString());

  await showDialog<void>(
    context: context,
    builder: (dialogContext) => StatefulBuilder(
      builder: (context, setState) {
        final entered = double.tryParse(value.text.replaceAll(',', '.'));
        final result = entered == null ? null : (inner ? entered + wall.thicknessMm : entered);
        return AlertDialog(
          key: const ValueKey('measure-dialog'),
          title: const Text('Measure wall'),
          content: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 340),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('Now: ${(centerline - wall.thicknessMm).round()} mm between the wall faces, ${centerline.round()} mm along the centerline.'),
                const SizedBox(height: 12),
                TextField(
                  key: const ValueKey('measure-value'),
                  controller: value,
                  autofocus: true,
                  keyboardType: const TextInputType.numberWithOptions(decimal: true),
                  decoration: const InputDecoration(labelText: 'Measured length', suffixText: 'mm'),
                  onChanged: (_) => setState(() {}),
                ),
                const SizedBox(height: 12),
                SegmentedButton<bool>(
                  key: const ValueKey('measure-reference'),
                  segments: const [
                    ButtonSegment(value: true, label: Text('Between faces')),
                    ButtonSegment(value: false, label: Text('Centerline')),
                  ],
                  selected: {inner},
                  onSelectionChanged: (s) => setState(() => inner = s.first),
                ),
                const SizedBox(height: 12),
                DropdownButtonFormField<MeasurementSource>(
                  key: const ValueKey('measure-source'),
                  isExpanded: true,
                  initialValue: source,
                  decoration: const InputDecoration(labelText: 'How was it measured?'),
                  items: [for (final s in MeasurementSource.values) DropdownMenuItem(value: s, child: Text(s.label))],
                  onChanged: (s) => setState(() => source = s ?? source),
                ),
                TextField(key: const ValueKey('measure-notes'), controller: notes, decoration: const InputDecoration(labelText: 'Note (optional)')),
                CheckboxListTile(
                  key: const ValueKey('measure-verified'),
                  contentPadding: EdgeInsets.zero,
                  controlAffinity: ListTileControlAffinity.leading,
                  value: verified,
                  onChanged: (v) => setState(() => verified = v ?? false),
                  title: const Text('I checked this value (mark as verified)'),
                ),
                Text(
                  result == null || result <= 0
                      ? 'Enter a length in millimetres.'
                      : 'The wall is set to ${result.round()} mm along its centerline; its far corner moves and the length is then fixed.',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ],
            ),
          ),
          actions: [
            TextButton(onPressed: () => Navigator.pop(dialogContext), child: const Text('Cancel')),
            FilledButton(
              key: const ValueKey('measure-apply'),
              onPressed: entered == null || entered <= 0
                  ? null
                  : () {
                      c.applyMeasurement(entered, inner: inner, source: source, verified: verified, notes: notes.text);
                      Navigator.pop(dialogContext);
                    },
              child: const Text('Apply'),
            ),
          ],
        );
      },
    ),
  );
}
