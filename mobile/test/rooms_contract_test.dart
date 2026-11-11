import 'dart:convert';
import 'dart:io';

import 'package:building_draft_app/geometry/commands.dart';
import 'package:building_draft_app/geometry/model.dart';
import 'package:building_draft_app/geometry/rooms.dart';
import 'package:building_draft_app/geometry/validation.dart';
import 'package:building_draft_app/geometry/vec2.dart';
import 'package:flutter_test/flutter_test.dart';

const floor = 'f0000000-0000-4000-8000-000000000001';

String id(int n) => '00000000-0000-4000-8000-${n.toString().padLeft(12, '0')}';

/// A session that exercises rooms, openings, splitting, deleting a room and a measurement that moves a corner.
/// The commands are the golden file the C# test replays; `expected` is what THIS implementation computed, and the
/// C# test must reach the same numbers with its own geometry code (tests/.../ClientRoomsContractTests.cs).
({List<PlanCommand> commands, PlanModel model}) session() {
  final n = [for (var i = 0; i <= 6; i++) id(100 + i)]; // corners 1..6
  final w = [for (var i = 0; i <= 7; i++) id(200 + i)]; // walls 1..7
  final commands = <PlanCommand>[
    CreateWall(wallId: w[1], startNodeId: n[1], start: const Vec2(0, 0), endNodeId: n[2], end: const Vec2(4000, 0)),
    CreateWall(wallId: w[2], startNodeId: n[2], start: const Vec2(4000, 0), endNodeId: n[3], end: const Vec2(4000, 3000)),
    CreateWall(wallId: w[3], startNodeId: n[3], start: const Vec2(4000, 3000), endNodeId: n[4], end: const Vec2(0, 3000)),
    CreateWall(wallId: w[4], startNodeId: n[4], start: const Vec2(0, 3000), endNodeId: n[1], end: const Vec2(0, 0)),
    SplitWall(wallId: w[1], newWallId: w[5], newNodeId: n[5], atMm: 2000),
    SplitWall(wallId: w[3], newWallId: w[6], newNodeId: n[6], atMm: 2000),
    CreateWall(wallId: w[7], startNodeId: n[5], start: const Vec2(2000, 0), endNodeId: n[6], end: const Vec2(2000, 3000)),
    CreateRoom(roomId: id(300), name: 'Left', wallIds: [w[1], w[7], w[6], w[4]], roomType: 'living_room'),
    CreateRoom(roomId: id(301), name: 'Right', wallIds: [w[5], w[2], w[3], w[7]]),
    AttachOpening(openingId: id(400), wallId: w[4], kind: OpeningKind.door, offsetMm: 500, widthMm: 900, heightMm: 2100),
    AttachOpening(openingId: id(401), wallId: w[2], kind: OpeningKind.window, offsetMm: 1000, widthMm: 1200, heightMm: 1200, sillMm: 900),
    ApplyMeasurement(wallId: w[4], valueMm: 3000), // inner 3000 -> centerline 3200: the corner n1 moves to (0, -200)
    DeleteRoom(id(301)),
    CreateRoom(roomId: id(302), name: 'Balcony', wallIds: [w[5], w[2], w[3], w[7]], areaType: RoomAreaType.openArea),
  ];

  var model = PlanModel(floorId: floor);
  for (final c in commands) {
    model = c.apply(model);
    final issues = validatePlan(model);
    expect(issues, isEmpty, reason: '${c.label}: ${issues.map((i) => i.message)}');
  }
  return (commands: commands, model: model);
}

Map<String, Object?> expected(PlanModel m) => {
      'wallCount': m.walls.length,
      'nodes': {for (final nd in m.nodes.values) nd.id: [nd.x, nd.y]},
      'rooms': {
        for (final r in m.rooms.values)
          r.id: {'name': r.name, 'areaType': r.areaType.wire, 'areaM2': roomMetrics(m, r)!.areaSqM}
      },
      'openings': {for (final o in m.openings.values) o.id: {'wallId': o.wallId, 'offsetMm': o.offsetMm}},
      'fixedLengths': m.fixedLengths,
    };

void main() {
  test('the rooms session matches the golden file (regenerate with --dart-define=UPDATE_GOLDEN=true)', () {
    final s = session();
    final golden = File('../contracts/client_rooms_session.json');
    final actual = const JsonEncoder.withIndent('  ').convert({
      'commands': [for (final c in s.commands) ...c.toServer(floor)],
      'expected': expected(s.model),
    });

    if (const bool.fromEnvironment('UPDATE_GOLDEN')) {
      golden.createSync(recursive: true);
      golden.writeAsStringSync('$actual\n');
    }

    expect(golden.existsSync(), isTrue, reason: 'run once with --dart-define=UPDATE_GOLDEN=true');
    expect(jsonDecode(golden.readAsStringSync()), jsonDecode(actual));
  });

  test('the session ends with the plan the test author expects', () {
    final m = session().model;

    expect(m.walls, hasLength(7));
    expect(m.rooms.values.map((r) => r.name), unorderedEquals(['Left', 'Balcony']));
    expect(m.pos(id(101)), const Vec2(0, -200), reason: 'the measured wall pushed its end corner');
    expect(m.fixedLengths[id(204)], 3200);
    expect(m.openings, hasLength(2));
    expect(roomMetrics(m, m.rooms[id(302)]!)!.areaSqM, closeTo(1.8 * 2.8, 1e-9)); // 1800 x 2800 inner
  });
}
