import 'dart:convert';

import 'package:building_draft_app/geometry/commands.dart';
import 'package:building_draft_app/geometry/model.dart';
import 'package:building_draft_app/geometry/rooms.dart';
import 'package:building_draft_app/geometry/validation.dart';
import 'package:building_draft_app/geometry/vec2.dart';
import 'package:flutter_test/flutter_test.dart';

/// A closed loop of walls through [corners], walls named w0.. and corners n0..
PlanModel loop(List<Vec2> corners, {double thickness = 200, PlanModel? onto}) {
  var model = onto ?? PlanModel(floorId: 'f');
  for (var i = 0; i < corners.length; i++) {
    final next = (i + 1) % corners.length;
    model = CreateWall(
      wallId: 'w$i',
      startNodeId: 'n$i',
      start: corners[i],
      endNodeId: 'n$next',
      end: corners[next],
      thicknessMm: thickness,
    ).apply(model);
  }
  return model;
}

PlanModel rectangle() => loop(const [Vec2(0, 0), Vec2(4000, 0), Vec2(4000, 3000), Vec2(0, 3000)]);

PlanModel lShape() => loop(const [Vec2(0, 0), Vec2(4000, 0), Vec2(4000, 2000), Vec2(2000, 2000), Vec2(2000, 4000), Vec2(0, 4000)]);

PlanModel withRoom(PlanModel m, {String name = 'Living'}) =>
    CreateRoom(roomId: 'r1', name: name, wallIds: m.walls.keys.toList()).apply(m);

List<String> codes(PlanModel m) => validatePlan(m).map((i) => i.code).toList();

void main() {
  group('room geometry (the same numbers as the server)', () {
    test('a 4 x 3 m room with 200 mm walls has 10.64 m2 of inner area', () {
      final m = withRoom(rectangle());

      final metrics = roomMetrics(m, m.rooms['r1']!)!;

      expect(metrics.areaSqM, closeTo(10.64, 1e-9));
      expect(metrics.innerPerimeterMm, closeTo(2 * (3800 + 2800), 1e-6));
    });

    test('a non-convex L room has 10.44 m2, as the server reports', () {
      final m = withRoom(lShape());

      expect(roomMetrics(m, m.rooms['r1']!)!.areaSqM, closeTo(10.44, 1e-9));
    });

    test('thicker walls leave less inner area', () {
      final thin = withRoom(loop(const [Vec2(0, 0), Vec2(4000, 0), Vec2(4000, 3000), Vec2(0, 3000)], thickness: 100));
      final thick = withRoom(loop(const [Vec2(0, 0), Vec2(4000, 0), Vec2(4000, 3000), Vec2(0, 3000)], thickness: 300));

      expect(roomMetrics(thin, thin.rooms['r1']!)!.areaSqM, closeTo(3.9 * 2.9, 1e-9));
      expect(roomMetrics(thick, thick.rooms['r1']!)!.areaSqM, closeTo(3.7 * 2.7, 1e-9));
    });

    test('the walls of a room are stored as one ordered loop whatever order they were given in', () {
      final m = rectangle();

      final room = CreateRoom(roomId: 'r1', name: 'Living', wallIds: const ['w2', 'w0', 'w3', 'w1']).apply(m).rooms['r1']!;

      final loopWalls = roomLoop(m, room)!;
      for (var i = 0; i < 4; i++) {
        expect(loopWalls.walls[i].endNodeId == loopWalls.walls[(i + 1) % 4].startNodeId || loopWalls.walls[i].endNodeId == loopWalls.walls[(i + 1) % 4].endNodeId, isTrue);
      }
    });

    test('what is not a closed loop is not a room', () {
      final m = rectangle();

      expect(roomLoop(m, const PlanRoom(id: 'r', name: 'x', wallIds: ['w0', 'w1', 'w2'])), isNull);
      expect(roomLoop(m, const PlanRoom(id: 'r', name: 'x', wallIds: ['w0', 'w1', 'nope'])), isNull);
      expect(roomLoop(m, const PlanRoom(id: 'r', name: 'x', wallIds: ['w0', 'w0', 'w1', 'w2'])), isNull);
      expect(codes(CreateRoom(roomId: 'r', name: 'Broken', wallIds: const ['w0', 'w1', 'w2']).apply(m)), contains('ROOM_NOT_CLOSED'));
    });

    test('a room needs a name', () {
      expect(() => const CreateRoom(roomId: 'r', name: '  ', wallIds: ['w0', 'w1', 'w2', 'w3']).apply(PlanModel(floorId: 'f')), throwsA(isA<PlanException>()));
    });
  });

  group('finding the closed area under a tap', () {
    test('a rectangle encloses exactly one area, and the outside is not one', () {
      final faces = findFaces(rectangle());

      expect(faces, hasLength(1));
      expect(faces.single.areaSqMm, closeTo(12e6, 1));
      expect(faceAt(rectangle(), const Vec2(2000, 1500)), isNotNull);
      expect(faceAt(rectangle(), const Vec2(9000, 9000)), isNull);
    });

    test('a wall across the rectangle makes two areas, and a tap picks the one it is in', () {
      var m = rectangle();
      m = const SplitWall(wallId: 'w0', newWallId: 'w4', newNodeId: 'n4', atMm: 2000).apply(m);
      m = const SplitWall(wallId: 'w2', newWallId: 'w5', newNodeId: 'n5', atMm: 2000).apply(m);
      m = const CreateWall(wallId: 'mid', startNodeId: 'n4', start: Vec2(2000, 0), endNodeId: 'n5', end: Vec2(2000, 3000)).apply(m);

      expect(findFaces(m), hasLength(2));
      final left = faceAt(m, const Vec2(1000, 1500))!;
      final right = faceAt(m, const Vec2(3000, 1500))!;
      expect(left.areaSqMm, closeTo(6e6, 1));
      expect(right.areaSqMm, closeTo(6e6, 1));
      expect(left.wallIds, contains('mid'));
      expect(right.wallIds, contains('mid'));
      expect(left.wallIds.toSet(), isNot(right.wallIds.toSet()));
    });

    test('a room made from a found area is a valid room', () {
      final m = rectangle();
      final face = faceAt(m, const Vec2(2000, 1500))!;

      final withRoomFromFace = CreateRoom(roomId: 'r', name: 'Found', wallIds: face.wallIds).apply(m);

      expect(validatePlan(withRoomFromFace), isEmpty);
      expect(roomMetrics(withRoomFromFace, withRoomFromFace.rooms['r']!)!.areaSqM, closeTo(10.64, 1e-9));
    });

    test('a non-convex area is found, and a tap in its notch is outside it', () {
      final m = lShape();

      expect(faceAt(m, const Vec2(1000, 3000)), isNotNull);
      expect(faceAt(m, const Vec2(3000, 3000)), isNull);
    });

    test('a wall sticking into an area makes it not a plain room', () {
      var m = rectangle();
      m = const CreateWall(wallId: 'dangling', startNodeId: 'x', start: Vec2(1000, 1000), endNodeId: 'n0', end: Vec2(0, 0)).apply(m);

      expect(faceAt(m, const Vec2(3000, 2000)), isNull);
    });
  });

  group('cascades follow the server', () {
    test('a wall that bounds a room cannot be deleted until the room is', () {
      final m = withRoom(rectangle());

      expect(() => const DeleteWall('w0').apply(m), throwsA(isA<PlanException>().having((e) => e.code, 'code', 'WALL_USED_BY_ROOM')));
      final freed = const DeleteWall('w0').apply(const DeleteRoom('r1').apply(m));
      expect(freed.walls, hasLength(3));
    });

    test('deleting a wall also removes its openings and its fixed length', () {
      var m = rectangle();
      m = const AttachOpening(openingId: 'o1', wallId: 'w0', kind: OpeningKind.door, offsetMm: 500, widthMm: 900, heightMm: 2100).apply(m);
      m = const ApplyMeasurement(wallId: 'w0', valueMm: 3800).apply(m);

      m = const DeleteWall('w0').apply(m);

      expect(m.openings, isEmpty);
      expect(m.fixedLengths, isEmpty);
    });

    test('splitting a wall moves the openings behind the cut, adds the new wall to its rooms and drops the fixed length', () {
      var m = withRoom(rectangle());
      m = const AttachOpening(openingId: 'door', wallId: 'w0', kind: OpeningKind.door, offsetMm: 100, widthMm: 800, heightMm: 2100).apply(m);
      m = const AttachOpening(openingId: 'window', wallId: 'w0', kind: OpeningKind.window, offsetMm: 3000, widthMm: 600, heightMm: 1200, sillMm: 900).apply(m);
      m = const ApplyMeasurement(wallId: 'w0', valueMm: 3800).apply(m);

      m = const SplitWall(wallId: 'w0', newWallId: 'w4', newNodeId: 'n4', atMm: 2000).apply(m);

      expect(m.openings['door']!.wallId, 'w0');
      expect(m.openings['window']!.wallId, 'w4');
      expect(m.openings['window']!.offsetMm, 1000);
      expect(m.rooms['r1']!.wallIds, contains('w4'));
      expect(m.fixedLengths, isEmpty);
      expect(validatePlan(m), isEmpty);
    });

    test('a cut through an opening is refused', () {
      final m = const AttachOpening(openingId: 'o1', wallId: 'w0', kind: OpeningKind.door, offsetMm: 1500, widthMm: 900, heightMm: 2100).apply(rectangle());

      expect(() => const SplitWall(wallId: 'w0', newWallId: 'w4', newNodeId: 'n4', atMm: 2000).apply(m),
          throwsA(isA<PlanException>().having((e) => e.code, 'code', 'OPENING_STRADDLES_SPLIT')));
    });
  });

  group('openings', () {
    PlanModel door({double offset = 500, double width = 900, double height = 2100, double sill = 0, String id = 'o1', String wall = 'w0', PlanModel? on}) =>
        AttachOpening(openingId: id, wallId: wall, kind: OpeningKind.door, offsetMm: offset, widthMm: width, heightMm: height, sillMm: sill).apply(on ?? rectangle());

    test('a door inside the wall is valid', () {
      expect(validatePlan(door()), isEmpty);
    });

    test('it must fit the wall length and height', () {
      expect(codes(door(offset: 3500)), contains('OPENING_OUT_OF_BOUNDS'));
      expect(codes(door(offset: -10)), contains('OPENING_OUT_OF_BOUNDS'));
      expect(codes(door(height: 2800)), contains('OPENING_OUT_OF_BOUNDS'));
      expect(codes(door(width: 0)), contains('OPENING_INVALID_SIZE'));
    });

    test('openings on one wall may not overlap', () {
      final m = door(offset: 500);

      expect(codes(door(offset: 1000, id: 'o2', on: m)), contains('OPENING_OVERLAP'));
      expect(validatePlan(door(offset: 1400, id: 'o2', on: m)), isEmpty);
    });

    test('shortening a wall so an opening no longer fits is refused', () {
      final m = door(offset: 2800);

      expect(codes(const MoveNode('n1', Vec2(3000, 0)).apply(m)), contains('OPENING_OUT_OF_BOUNDS'));
    });
  });

  group('measurements', () {
    test('an inner measurement becomes a centerline length with the wall thickness added', () {
      final m = const ApplyMeasurement(wallId: 'w0', valueMm: 4250).apply(rectangle());

      expect(m.pos('n1'), const Vec2(4450, 0));
      expect(m.pos('n0'), const Vec2(0, 0), reason: 'the start stays');
      expect(m.fixedLengths['w0'], 4450);
    });

    test('a centerline measurement is used as it is', () {
      final m = const ApplyMeasurement(wallId: 'w0', valueMm: 4250, inner: false).apply(rectangle());

      expect(m.pos('n1'), const Vec2(4250, 0));
      expect(m.fixedLengths['w0'], 4250);
    });

    test('measuring works along any direction', () {
      final m = const ApplyMeasurement(wallId: 'w1', valueMm: 3300, inner: false).apply(rectangle());

      expect(m.pos('n2'), const Vec2(4000, 3300));
    });

    test('a measured wall keeps its length: dragging its corners is refused with the reason', () {
      final m = const ApplyMeasurement(wallId: 'w0', valueMm: 3800).apply(rectangle());

      final dragged = const MoveNode('n1', Vec2(4500, 0)).apply(m);

      expect(validatePlan(dragged).map((i) => i.code), contains('LENGTH_LOCKED'));
      expect(validatePlan(dragged).first.message, contains('4.00 m'));
    });

    test('a corner can still be moved if the measured wall keeps its length', () {
      final m = const ApplyMeasurement(wallId: 'w0', valueMm: 3800).apply(rectangle());

      // Sliding both ends of the measured wall by the same amount keeps its length.
      final slid = const MoveNode('n1', Vec2(4000, 500)).apply(const MoveNode('n0', Vec2(0, 500)).apply(m));

      expect(validatePlan(slid).where((i) => i.code == 'LENGTH_LOCKED'), isEmpty);
    });

    test('nonsense is refused', () {
      expect(() => const ApplyMeasurement(wallId: 'w0', valueMm: 0).apply(rectangle()), throwsA(isA<PlanException>()));
      expect(() => const ApplyMeasurement(wallId: 'nope', valueMm: 10).apply(rectangle()), throwsA(isA<PlanException>()));
    });
  });

  group('storage', () {
    test('rooms, openings and fixed lengths survive a JSON round trip', () {
      var m = withRoom(rectangle());
      m = const AttachOpening(openingId: 'o1', wallId: 'w0', kind: OpeningKind.window, offsetMm: 500, widthMm: 900, heightMm: 1200, sillMm: 900).apply(m);
      m = const ApplyMeasurement(wallId: 'w1', valueMm: 2800).apply(m);

      final copy = PlanModel.fromJson(jsonDecode(jsonEncode(m.toJson())) as Map<String, Object?>);

      expect(jsonEncode(copy.toJson()), jsonEncode(m.toJson()));
      expect(copy.rooms['r1']!.areaType, RoomAreaType.enclosed);
      expect(copy.openings['o1']!.kind, OpeningKind.window);
    });

    test('a plan saved by an older version (no rooms, openings or fixed lengths) still loads', () {
      final old = {
        'floorId': 'f',
        'nodes': [
          {'id': 'a', 'x': 0, 'y': 0},
          {'id': 'b', 'x': 1000, 'y': 0}
        ],
        'walls': [
          {'id': 'w', 'startNodeId': 'a', 'endNodeId': 'b', 'thicknessMm': 200, 'heightMm': 2700}
        ],
      };

      final m = PlanModel.fromJson(old);

      expect(m.walls, hasLength(1));
      expect(m.rooms, isEmpty);
      expect(m.openings, isEmpty);
      expect(m.fixedLengths, isEmpty);
    });

    test('the open-area type is written the way the server spells it', () {
      expect(RoomAreaType.openArea.wire, 'open_area');
      expect(RoomAreaType.fromWire('open_area'), RoomAreaType.openArea);
      final command = const CreateRoom(roomId: 'r', name: 'Balcony', wallIds: ['a'], areaType: RoomAreaType.openArea).toServer('f').single;
      expect((command['payload']! as Map)['areaType'], 'open_area');
    });
  });
}
