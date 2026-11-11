import 'dart:ui' as ui;

import 'package:building_draft_app/demo/demo_plans.dart';
import 'package:building_draft_app/geometry/commands.dart';
import 'package:building_draft_app/geometry/hit_test.dart';
import 'package:building_draft_app/geometry/snap.dart';
import 'package:building_draft_app/geometry/validation.dart';
import 'package:building_draft_app/geometry/vec2.dart';
import 'package:building_draft_app/state/plan_controller.dart';
import 'package:building_draft_app/ui/plan_painter.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

// PRD 11.4 sets "around 60 FPS" as the starting target, i.e. about 16 ms per frame. These numbers are measured in
// the test VM (debug, JIT), which is slower than a release build on a phone, and the painter only records
// drawing commands (it does not rasterize). They are a coarse regression guard and an early read on the
// risk, not a substitute for measuring on real devices.
const frameBudgetMs = 16.0;

double averageMs(int iterations, void Function() work, {int warmup = 20}) {
  for (var i = 0; i < warmup; i++) {
    work();
  }
  final watch = Stopwatch()..start();
  for (var i = 0; i < iterations; i++) {
    work();
  }
  return watch.elapsedMicroseconds / 1000 / iterations;
}

void main() {
  final plan = DemoPlans.lattice(); // 220 walls, 121 corners
  late PlanController c;

  setUp(() {
    c = PlanController(model: plan)..fitToContent(const Size(1280, 800));
  });

  test('recording a frame of 220 walls fits the frame budget', () {
    final painter = PlanPainter(c, ColorScheme.fromSeed(seedColor: Colors.blue));

    final ms = averageMs(200, () {
      final recorder = ui.PictureRecorder();
      painter.paint(Canvas(recorder), const Size(1280, 800));
      recorder.endRecording().dispose();
    });

    // ignore: avoid_print
    print('paint (record) 220 walls: ${ms.toStringAsFixed(2)} ms/frame');
    expect(ms, lessThan(frameBudgetMs));
  });

  test('zoomed in, culling keeps the frame cheap', () {
    c.setViewport(c.viewport.zoomAt(const Offset(640, 400), 8)); // most of the lattice is off screen
    final painter = PlanPainter(c, ColorScheme.fromSeed(seedColor: Colors.blue));

    final ms = averageMs(200, () {
      final recorder = ui.PictureRecorder();
      painter.paint(Canvas(recorder), const Size(1280, 800));
      recorder.endRecording().dispose();
    });

    // ignore: avoid_print
    print('paint (record) zoomed in: ${ms.toStringAsFixed(2)} ms/frame');
    expect(ms, lessThan(frameBudgetMs));
  });

  test('hit testing and snapping stay far below a frame', () {
    const engine = SnapEngine();
    var i = 0;
    Vec2 point() => Vec2((i++ * 137.0) % 30000, (i * 71.0) % 30000);

    final hit = averageMs(2000, () => hitTest(plan, point(), 0.04));
    final snap = averageMs(2000, () => engine.snap(point(), model: plan, pxPerMm: 0.04, anchor: const Vec2(0, 0)));

    // ignore: avoid_print
    print('hit test: ${hit.toStringAsFixed(3)} ms, snap: ${snap.toStringAsFixed(3)} ms (220 walls)');
    expect(hit, lessThan(1.0));
    expect(snap, lessThan(1.0));
  });

  test('validating the whole plan after an edit fits a frame at 220 walls', () {
    final ms = averageMs(50, () => validatePlan(plan), warmup: 5);

    // ignore: avoid_print
    print('validate 220 walls: ${ms.toStringAsFixed(2)} ms (all wall pairs)');
    expect(ms, lessThan(frameBudgetMs));
  });

  test('executing a command (apply, validate, history) at 220 walls', () {
    var n = 0;
    final ms = averageMs(30, () {
      c.execute(CreateWall(
        wallId: 'extra-${n++}',
        startNodeId: 'free-a-$n',
        start: Vec2(40000.0 + n * 10, 0),
        endNodeId: 'free-b-$n',
        end: Vec2(40000.0 + n * 10, 1000),
      ));
    }, warmup: 3);

    // ignore: avoid_print
    print('execute command: ${ms.toStringAsFixed(2)} ms (220+ walls)');
    expect(ms, lessThan(frameBudgetMs * 2));
  });

  test('at 511 walls an edit still fits a frame because only the edited walls are re-checked', () {
    final big = DemoPlans.lattice(columns: 16, rows: 15); // 2*16*15 + 16 + 15 = 511 walls
    final controller = PlanController(model: big);
    var n = 0;

    final full = averageMs(5, () => validatePlan(big), warmup: 1);
    final edit = averageMs(30, () {
      controller.execute(CreateWall(
        wallId: 'extra-${n++}',
        startNodeId: 'free-a-$n',
        start: Vec2(80000.0 + n * 10, 0),
        endNodeId: 'free-b-$n',
        end: Vec2(80000.0 + n * 10, 1000),
      ));
    }, warmup: 3);

    // ignore: avoid_print
    print('${big.walls.length} walls: full validation ${full.toStringAsFixed(1)} ms, one edit (incremental) ${edit.toStringAsFixed(2)} ms');
    expect(validatePlan(big), isEmpty);
    expect(edit, lessThan(frameBudgetMs));
  });
}
