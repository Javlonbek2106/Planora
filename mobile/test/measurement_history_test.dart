import 'dart:convert';

import 'package:building_draft_app/geometry/commands.dart';
import 'package:building_draft_app/geometry/model.dart';
import 'package:building_draft_app/geometry/vec2.dart';
import 'package:building_draft_app/state/plan_controller.dart';
import 'package:building_draft_app/state/viewport.dart';
import 'package:building_draft_app/sync/server_commands.dart';
import 'package:flutter_test/flutter_test.dart';

PlanModel rectangle() {
  var model = PlanModel(floorId: 'f');
  const corners = [Vec2(0, 0), Vec2(4000, 0), Vec2(4000, 3000), Vec2(0, 3000)];
  for (var i = 0; i < 4; i++) {
    final next = (i + 1) % 4;
    model = CreateWall(wallId: 'w$i', startNodeId: 'n$i', start: corners[i], endNodeId: 'n$next', end: corners[next]).apply(model);
  }
  return model;
}

final taken = DateTime.utc(2026, 11, 10, 9, 30);

ApplyMeasurement measure(String id, {String wall = 'w0', double value = 3800, bool verified = false, String? notes, MeasurementSource source = MeasurementSource.laserManual}) =>
    ApplyMeasurement(wallId: wall, valueMm: value, measurementId: id, source: source, verified: verified, notes: notes, capturedAt: taken);

void main() {
  group('recording a measurement', () {
    test('keeps the value, source, notes and time, unverified unless the user confirmed it', () {
      final m = measure('m1', notes: 'kitchen').apply(rectangle()).measurements['m1']!;

      expect((m.valueMm, m.source, m.status, m.notes, m.capturedAt, m.inner), (3800.0, MeasurementSource.laserManual, MeasurementStatus.unverified, 'kitchen', taken, true));
    });

    test('an explicit confirmation makes it verified', () {
      expect(measure('m1', verified: true).apply(rectangle()).measurements['m1']!.status, MeasurementStatus.verified);
    });

    test('a newer measurement of the same wall supersedes the older one; other walls are untouched; nothing is deleted', () {
      var m = measure('a', wall: 'w0', value: 3800).apply(rectangle());
      m = measure('other', wall: 'w1', value: 2800).apply(m);

      m = measure('b', wall: 'w0', value: 3850).apply(m);

      expect(m.measurements, hasLength(3));
      expect(m.measurements['a']!.status, MeasurementStatus.superseded);
      expect(m.measurements['b']!.status, MeasurementStatus.unverified);
      expect(m.measurements['other']!.status, MeasurementStatus.unverified);
      expect(m.measurements['a']!.valueMm, 3800, reason: 'the value of history never changes');
    });

    test('a rejected measurement stays rejected when a newer one arrives', () {
      var m = measure('a').apply(rectangle());
      m = m.copyWith(measurements: {'a': m.measurements['a']!.withStatus(MeasurementStatus.rejected)});

      m = measure('b', value: 3850).apply(m);

      expect(m.measurements['a']!.status, MeasurementStatus.rejected);
    });

    test('recording the same id twice changes nothing (like the server)', () {
      final once = measure('a').apply(rectangle());

      final twice = measure('a', value: 3900).apply(once);

      expect(twice.measurements, hasLength(1));
      expect(twice.measurements['a']!.valueMm, 3800);
    });

    test('without an id the geometry changes but no history entry is made', () {
      final m = const ApplyMeasurement(wallId: 'w0', valueMm: 3800).apply(rectangle());

      expect(m.measurements, isEmpty);
      expect(m.fixedLengths['w0'], 4000);
    });

    test('the history is kept when the wall is deleted or split, as on the server', () {
      final measured = measure('a').apply(rectangle());

      final split = const SplitWall(wallId: 'w0', newWallId: 'w9', newNodeId: 'n9', atMm: 2000).apply(measured);

      expect(split.fixedLengths, isEmpty);
      expect(split.measurements['a'], isNotNull);
    });
  });

  group('what is sent to the server', () {
    test('the measurement travels inside apply_measurement with its metadata', () {
      final payload = (measure('m1', verified: true, notes: '  tape  ').toServer('f').single['payload']! as Map).cast<String, Object?>();

      expect(measure('m1').toServer('f').single['commandType'], 'apply_measurement');
      expect(payload['measurementId'], 'm1');
      expect(payload['source'], 'laser_manual');
      expect(payload['verified'], true);
      expect(payload['notes'], 'tape');
      expect(payload['reference'], 'inner');
      expect(payload['capturedAt'], '2026-11-10T09:30:00.000Z');
      expect(payload['valueMm'], 3800);
    });

    test('without an id the payload is the old, plain one', () {
      final payload = (const ApplyMeasurement(wallId: 'w0', valueMm: 3800).toServer('f').single['payload']! as Map).keys;

      expect(payload, unorderedEquals(['wallId', 'valueMm', 'reference']));
    });

    test('a measurement made on another device is decoded with its metadata and gives the same history', () {
      final sent = measure('m1', verified: true, notes: 'north wall', source: MeasurementSource.laserBluetooth);
      final wire = sent.toServer('f').single;

      final decoded = decodeServerCommand(wire['commandType']! as String, (wire['payload']! as Map).cast<String, Object?>())!;

      final a = sent.apply(rectangle()).measurements['m1']!;
      final b = decoded.apply(rectangle()).measurements['m1']!;
      expect((b.valueMm, b.source, b.status, b.notes, b.capturedAt), (a.valueMm, a.source, a.status, a.notes, a.capturedAt));
    });

    test('a plan drawn before connecting uploads the latest recorded measurement of a wall, with its history entry', () {
      var m = measure('old', value: 3700).apply(rectangle());
      m = measure('new', value: 3800, notes: 'recheck').apply(m);

      final upload = commandsForModel(m).whereType<ApplyMeasurement>().single;

      expect(upload.measurementId, 'new');
      expect(upload.notes, 'recheck');
      expect(upload.valueMm, 3800);
    });

    test('a measured wall with no history entry (older plans) is uploaded as a plain centerline length', () {
      final m = const ApplyMeasurement(wallId: 'w0', valueMm: 4000, inner: false).apply(rectangle());

      final upload = commandsForModel(m).whereType<ApplyMeasurement>().single;

      expect(upload.measurementId, isNull);
      expect(upload.inner, isFalse);
    });
  });

  group('from the server', () {
    test('the history response becomes plan measurements and estimates are left out', () {
      final history = measurementsFromServer([
        {'id': 'a', 'targetObjectId': 'w0', 'valueMm': 3800, 'reference': 'inner', 'source': 'laser_manual', 'status': 'superseded', 'isEstimate': false, 'capturedAt': '2026-11-10T09:30:00+00:00', 'deviceName': 'GLM', 'notes': null},
        {'id': 'b', 'targetObjectId': 'w0', 'valueMm': 3850, 'reference': 'centerline', 'source': 'manual', 'status': 'verified', 'isEstimate': false, 'capturedAt': '2026-11-10T10:00:00+00:00', 'deviceName': null, 'notes': 'tape'},
        {'id': 'ar', 'targetObjectId': 'w0', 'valueMm': 3900, 'reference': 'inner', 'source': 'ar_lidar', 'status': 'unverified', 'isEstimate': true, 'capturedAt': '2026-11-10T11:00:00+00:00'},
      ]);

      expect(history.keys, unorderedEquals(['a', 'b']));
      expect(history['a']!.status, MeasurementStatus.superseded);
      expect(history['a']!.deviceName, 'GLM');
      expect((history['b']!.inner, history['b']!.source, history['b']!.status), (false, MeasurementSource.manual, MeasurementStatus.verified));
    });
  });

  group('storage', () {
    test('the history survives a JSON round trip', () {
      final m = measure('a', notes: 'x', verified: true).apply(rectangle());

      final copy = PlanModel.fromJson(jsonDecode(jsonEncode(m.toJson())) as Map<String, Object?>);

      expect(jsonEncode(copy.toJson()), jsonEncode(m.toJson()));
      expect(copy.measurements['a']!.capturedAt, taken);
    });

    test('plans saved before the history existed still load', () {
      final old = rectangle().toJson()..remove('measurements');

      expect(PlanModel.fromJson(old).measurements, isEmpty);
    });

    test('the sources are written the way the server spells them', () {
      expect([for (final s in MeasurementSource.values) s.wire], ['manual', 'laser_manual', 'laser_bluetooth']);
      expect(MeasurementSource.fromWire('laser_bluetooth'), MeasurementSource.laserBluetooth);
    });
  });

  group('through the editor', () {
    PlanController editor() {
      var n = 0;
      return PlanController(model: rectangle(), ids: () => 'id-${++n}')..viewport = const Viewport(pxPerMm: 0.1);
    }

    test('measuring from the editor records who/what, as a laser value by default, and one undo removes both the length and the entry', () {
      final c = editor();
      c.pointerDown(const Vec2(2000, 0));
      c.pointerUp(const Vec2(2000, 0));

      expect(c.applyMeasurement(3800, notes: 'checked twice'), isTrue);

      final recorded = c.model.measurements.values.single;
      expect((recorded.source, recorded.status, recorded.notes), (MeasurementSource.laserManual, MeasurementStatus.unverified, 'checked twice'));
      expect((c.serverCommands.last['payload']! as Map)['measurementId'], recorded.id);

      c.undo();
      expect(c.model.measurements, isEmpty);
      expect(c.model.fixedLengths, isEmpty);
    });

    test('measuring a wall twice leaves two entries, the first superseded', () {
      final c = editor();
      c.pointerDown(const Vec2(2000, 0));
      c.pointerUp(const Vec2(2000, 0));
      c.applyMeasurement(3800);
      c.pointerDown(const Vec2(2000, 0));
      c.pointerUp(const Vec2(2000, 0));

      c.applyMeasurement(3800, verified: true);

      final history = c.model.measurementsOf('w0');
      expect(history, hasLength(2));
      expect(history.map((m) => m.status), unorderedEquals([MeasurementStatus.superseded, MeasurementStatus.verified]));
    });
  });
}
