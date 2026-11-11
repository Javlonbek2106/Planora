import 'dart:convert';
import 'dart:math';

import 'package:building_draft_app/demo/demo_plans.dart';
import 'package:building_draft_app/geometry/commands.dart';
import 'package:building_draft_app/geometry/hit_test.dart';
import 'package:building_draft_app/geometry/model.dart';
import 'package:building_draft_app/geometry/snap.dart';
import 'package:building_draft_app/geometry/validation.dart';
import 'package:building_draft_app/geometry/vec2.dart';
import 'package:flutter_test/flutter_test.dart';

PlanModel rectangle({double width = 4000, double depth = 3000}) {
  var model = PlanModel(floorId: 'f');
  final corners = [Vec2(0, 0), Vec2(width, 0), Vec2(width, depth), Vec2(0, depth)];
  for (var i = 0; i < 4; i++) {
    final next = (i + 1) % 4;
    model = CreateWall(
      wallId: 'w$i',
      startNodeId: 'n$i',
      start: corners[i],
      endNodeId: 'n$next',
      end: corners[next],
    ).apply(model);
  }
  return model;
}

List<String> codes(PlanModel m) => validatePlan(m).map((i) => i.code).toList();

void main() {
  group('commands', () {
    test('a rectangle of four walls is valid and shares its corners', () {
      final model = rectangle();

      expect(model.walls, hasLength(4));
      expect(model.nodes, hasLength(4));
      expect(validatePlan(model), isEmpty);
    });

    test('create rejects zero-length and duplicate ids', () {
      final model = rectangle();

      expect(() => const CreateWall(wallId: 'x', startNodeId: 'a', start: Vec2(0, 0), endNodeId: 'a', end: Vec2(0, 0)).apply(model),
          throwsA(isA<PlanException>().having((e) => e.code, 'code', 'WALL_ZERO_LENGTH')));
      expect(() => const CreateWall(wallId: 'w0', startNodeId: 'a', start: Vec2(0, 0), endNodeId: 'b', end: Vec2(1, 0)).apply(model),
          throwsA(isA<PlanException>().having((e) => e.code, 'code', 'WALL_EXISTS')));
    });

    test('move node changes only that corner', () {
      final moved = const MoveNode('n2', Vec2(4500, 3500)).apply(rectangle());

      expect(moved.pos('n2'), const Vec2(4500, 3500));
      expect(moved.pos('n1'), const Vec2(4000, 0));
      expect(() => const MoveNode('nope', Vec2(0, 0)).apply(rectangle()), throwsA(isA<PlanException>()));
    });

    test('delete removes the wall and corners nothing else uses', () {
      final model = const DeleteWall('w0').apply(rectangle());

      expect(model.walls.keys, isNot(contains('w0')));
      expect(model.nodes.keys, containsAll(['n0', 'n1'])); // still used by w3 and w1
      final lone = const DeleteWall('x').apply(const CreateWall(wallId: 'x', startNodeId: 'a', start: Vec2(0, 0), endNodeId: 'b', end: Vec2(1000, 0)).apply(PlanModel(floorId: 'f')));
      expect(lone.nodes, isEmpty);
    });

    test('split cuts a wall in two at the given distance', () {
      final model = const SplitWall(wallId: 'w0', newWallId: 'w4', newNodeId: 'n4', atMm: 1500).apply(rectangle());

      expect(model.pos('n4'), const Vec2(1500, 0));
      expect(model.walls['w0']!.endNodeId, 'n4');
      expect(model.walls['w4']!.startNodeId, 'n4');
      expect(model.walls['w4']!.endNodeId, 'n1');
      expect(validatePlan(model), isEmpty);
      expect(() => const SplitWall(wallId: 'w0', newWallId: 'w5', newNodeId: 'n5', atMm: 0.5).apply(rectangle()), throwsA(isA<PlanException>()));
    });

    test('a model survives a JSON round trip unchanged', () {
      final model = rectangle();

      final copy = PlanModel.fromJson(jsonDecode(jsonEncode(model.toJson())) as Map<String, Object?>);

      expect(jsonEncode(copy.toJson()), jsonEncode(model.toJson()));
    });

    test('server payloads use the backend command names and field names', () {
      final commands = const CreateWall(wallId: 'w', startNodeId: 'a', start: Vec2(0, 0), endNodeId: 'b', end: Vec2(1000, 0)).toServer('floor');

      expect(commands.single['commandType'], 'create_wall');
      expect((commands.single['payload']! as Map).keys,
          containsAll(['wallId', 'floorId', 'startNodeId', 'startXMm', 'startYMm', 'endNodeId', 'endXMm', 'endYMm']));
    });
  });

  group('validation (the same rules as the server)', () {
    test('crossing walls are rejected', () {
      final model = const CreateWall(wallId: 'x', startNodeId: 'a', start: Vec2(-500, 1500), endNodeId: 'b', end: Vec2(4500, 1500)).apply(rectangle());

      expect(codes(model), contains('WALL_INTERSECTION'));
    });

    test('a wall that ends on another wall without splitting it is rejected', () {
      final model = const CreateWall(wallId: 'x', startNodeId: 'a', start: Vec2(2000, 0), endNodeId: 'b', end: Vec2(2000, -1000)).apply(rectangle());

      expect(codes(model), contains('WALL_INTERSECTION'));
    });

    test('the same T-junction is fine once the wall was split', () {
      var model = const SplitWall(wallId: 'w0', newWallId: 'w4', newNodeId: 'n4', atMm: 2000).apply(rectangle());
      model = const CreateWall(wallId: 'x', startNodeId: 'n4', start: Vec2(2000, 0), endNodeId: 'b', end: Vec2(2000, -1000)).apply(model);

      expect(validatePlan(model), isEmpty);
    });

    test('duplicate and overlapping walls are rejected', () {
      expect(codes(const CreateWall(wallId: 'x', startNodeId: 'n1', start: Vec2(4000, 0), endNodeId: 'n0', end: Vec2(0, 0)).apply(rectangle())),
          contains('WALL_DUPLICATE'));
      expect(codes(const CreateWall(wallId: 'x', startNodeId: 'n0', start: Vec2(0, 0), endNodeId: 'c', end: Vec2(2000, 0)).apply(rectangle())),
          contains('WALL_OVERLAP'));
    });

    test('a corner dragged across the opposite wall folds the plan over itself', () {
      final model = const MoveNode('n2', Vec2(4000, -1000)).apply(rectangle());

      expect(codes(model), contains('WALL_INTERSECTION'));
    });
  });

  group('snapping', () {
    const engine = SnapEngine();

    test('a corner wins over everything else', () {
      final result = engine.snap(const Vec2(4050, 30), model: rectangle(), pxPerMm: 0.1);

      expect(result.kind, SnapKind.node);
      expect(result.nodeId, 'n1');
      expect(result.point, const Vec2(4000, 0));
    });

    test('a point on a wall snaps onto it as a T-junction candidate', () {
      final result = engine.snap(const Vec2(2000, 60), model: rectangle(), pxPerMm: 0.1);

      expect(result.kind, SnapKind.wall);
      expect(result.wallId, 'w0');
      expect(result.t, closeTo(0.5, 0.001));
      expect(result.point.y, 0);
    });

    test('excluded corners and their walls are ignored (dragging a corner must not snap to itself)', () {
      final result = engine.snap(const Vec2(10, 10), model: rectangle(), pxPerMm: 0.1, excludeNodes: {'n0'});

      expect(result.nodeId, isNull);
      expect(result.kind, isNot(SnapKind.node));
    });

    test('in line with the anchor wins over the grid', () {
      final horizontal = engine.snap(const Vec2(6010, 40), model: PlanModel(floorId: 'f'), pxPerMm: 0.1, anchor: const Vec2(0, 0));
      final vertical = engine.snap(const Vec2(30, 5040), model: PlanModel(floorId: 'f'), pxPerMm: 0.1, anchor: const Vec2(0, 0));

      expect(horizontal.kind, SnapKind.axis);
      expect(horizontal.point, const Vec2(6000, 0));
      expect(vertical.point, const Vec2(0, 5000));
    });

    test('otherwise it snaps to the grid, and the grid coarsens when zoomed out', () {
      final fine = engine.snap(const Vec2(1234, 5678), model: PlanModel(floorId: 'f'), pxPerMm: 0.1);
      final coarse = engine.snap(const Vec2(1234, 5678), model: PlanModel(floorId: 'f'), pxPerMm: 0.02);

      expect(fine.point, const Vec2(1200, 5700));
      expect(engine.effectiveGridMm(0.02), 800); // 100 -> 200 -> 400 -> 800 until lines are >= 10 px apart
      expect(coarse.point, const Vec2(1600, 5600));
    });

    test('with the grid off the raw point is kept', () {
      final result = engine.snap(const Vec2(1234.5, 5678.5), model: PlanModel(floorId: 'f'), pxPerMm: 0.1, useGrid: false);

      expect(result.kind, SnapKind.none);
      expect(result.point, const Vec2(1234.5, 5678.5));
    });
  });

  group('hit testing', () {
    test('a corner is hit before a wall', () {
      expect(hitTest(rectangle(), const Vec2(20, 20), 0.1), isA<NodeHit>().having((h) => h.nodeId, 'nodeId', 'n0'));
    });

    test('a wall is hit along its body, a thin one still within the finger tolerance', () {
      final hit = hitTest(rectangle(), const Vec2(2000, 90), 0.1);

      expect(hit, isA<WallHit>().having((h) => h.wallId, 'wallId', 'w0'));
      expect(hitTest(rectangle(), const Vec2(2000, 1500), 0.1), isNull);
    });
  });

  group('incremental validation', () {
    test('checking only the edited walls gives the same verdict as checking everything', () {
      final random = Random(7);
      var model = DemoPlans.lattice(columns: 6, rows: 6);
      var valid = 0;
      var invalid = 0;

      for (var i = 0; i < 300; i++) {
        final ids = model.nodes.keys.toList();
        final move = MoveNode(ids[random.nextInt(ids.length)], Vec2(random.nextDouble() * 20000 - 1000, random.nextDouble() * 20000 - 1000));
        final next = move.apply(model);

        final full = validatePlan(next).isEmpty;
        final incremental = validatePlan(next, only: changedWalls(model, next)).isEmpty;

        expect(incremental, full, reason: 'edit $i disagreed');
        if (full) {
          model = next;
          valid++;
        } else {
          invalid++;
        }
      }
      expect(valid, greaterThan(20));
      expect(invalid, greaterThan(20));
    });

    test('changed walls are the new ones and every wall on a moved corner', () {
      final before = rectangle();

      final moved = const MoveNode('n2', Vec2(4500, 3500)).apply(before);
      final split = const SplitWall(wallId: 'w0', newWallId: 'w4', newNodeId: 'n4', atMm: 1000).apply(before);

      expect(changedWalls(before, moved), {'w1', 'w2'});
      expect(changedWalls(before, split), {'w0', 'w4'});
      expect(changedWalls(before, before), isEmpty);
    });
  });
}
