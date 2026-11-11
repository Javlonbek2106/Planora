import 'dart:math' as math;

import 'vec2.dart';

/// Creates ids for new nodes and walls. Tests inject a counter; the app uses random UUIDs, because ids are
/// created offline and must never collide with ids made on another device.
typedef IdGenerator = String Function();

String randomUuid() {
  final random = math.Random.secure();
  final bytes = List<int>.generate(16, (_) => random.nextInt(256));
  bytes[6] = (bytes[6] & 0x0f) | 0x40; // version 4
  bytes[8] = (bytes[8] & 0x3f) | 0x80; // RFC 4122 variant
  final hex = bytes.map((b) => b.toRadixString(16).padLeft(2, '0')).join();
  return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-${hex.substring(12, 16)}-${hex.substring(16, 20)}-${hex.substring(20)}';
}

class PlanNode {
  const PlanNode(this.id, this.x, this.y);

  final String id;
  final double x;
  final double y;

  Vec2 get position => Vec2(x, y);

  PlanNode moved(double x, double y) => PlanNode(id, x, y);
}

class PlanWall {
  const PlanWall({
    required this.id,
    required this.startNodeId,
    required this.endNodeId,
    this.thicknessMm = 200,
    this.heightMm = 2700,
  });

  final String id;
  final String startNodeId;
  final String endNodeId;
  final double thicknessMm;
  final double heightMm;

  String otherNode(String nodeId) => nodeId == startNodeId ? endNodeId : startNodeId;

  bool touches(String nodeId) => nodeId == startNodeId || nodeId == endNodeId;
}

enum OpeningKind { door, window }

enum RoomAreaType {
  enclosed,

  /// Balcony, loggia, terrace: counted separately from the enclosed area.
  openArea;

  /// The name the server uses in JSON.
  String get wire => this == enclosed ? 'enclosed' : 'open_area';

  static RoomAreaType fromWire(String value) => value == 'open_area' ? openArea : enclosed;
}

/// A door or window in a wall, positioned by [offsetMm] from the wall's start.
class PlanOpening {
  const PlanOpening({
    required this.id,
    required this.wallId,
    required this.kind,
    required this.offsetMm,
    required this.widthMm,
    required this.heightMm,
    this.sillMm = 0,
  });

  final String id;
  final String wallId;
  final OpeningKind kind;
  final double offsetMm;
  final double widthMm;
  final double heightMm;
  final double sillMm;

  PlanOpening copyWith({String? wallId, double? offsetMm}) => PlanOpening(
        id: id,
        wallId: wallId ?? this.wallId,
        kind: kind,
        offsetMm: offsetMm ?? this.offsetMm,
        widthMm: widthMm,
        heightMm: heightMm,
        sillMm: sillMm,
      );
}

/// How a measured length was obtained. Only exact sources exist here: AR/LiDAR and AI results are estimates and
/// the server refuses them as lengths.
enum MeasurementSource {
  manual,
  laserManual,
  laserBluetooth;

  /// The name the server uses in JSON.
  String get wire => switch (this) { manual => 'manual', laserManual => 'laser_manual', laserBluetooth => 'laser_bluetooth' };

  static MeasurementSource fromWire(String value) => switch (value) {
        'laser_manual' => laserManual,
        'laser_bluetooth' => laserBluetooth,
        _ => manual,
      };

  String get label => switch (this) { manual => 'Tape / manual', laserManual => 'Laser (typed in)', laserBluetooth => 'Laser (Bluetooth)' };
}

enum MeasurementStatus {
  unverified,
  verified,

  /// Replaced by a newer measurement of the same wall. Kept: history is never deleted.
  superseded,
  rejected;

  static MeasurementStatus fromWire(String value) => MeasurementStatus.values.firstWhere((s) => s.name == value, orElse: () => unverified);
}

/// One entry of a wall's measurement history: the value never changes, only its status does.
class PlanMeasurement {
  const PlanMeasurement({
    required this.id,
    required this.wallId,
    required this.valueMm,
    required this.inner,
    required this.source,
    required this.status,
    required this.capturedAt,
    this.deviceName,
    this.notes,
  });

  final String id;
  final String wallId;
  final double valueMm;

  /// Between the wall faces (true) or along the centerline (false).
  final bool inner;
  final MeasurementSource source;
  final MeasurementStatus status;
  final DateTime capturedAt;
  final String? deviceName;
  final String? notes;

  PlanMeasurement withStatus(MeasurementStatus next) => PlanMeasurement(
        id: id,
        wallId: wallId,
        valueMm: valueMm,
        inner: inner,
        source: source,
        status: next,
        capturedAt: capturedAt,
        deviceName: deviceName,
        notes: notes,
      );
}

class PlanRoom {
  const PlanRoom({
    required this.id,
    required this.name,
    required this.wallIds,
    this.roomType = 'other',
    this.areaType = RoomAreaType.enclosed,
  });

  final String id;
  final String name;
  final String roomType;
  final RoomAreaType areaType;
  final List<String> wallIds;
}

/// One floor's walls, corners, rooms and openings. Immutable: every change makes a new model, which is what makes
/// undo a plain "go back to the previous model" and keeps the canvas free of half-applied states.
class PlanModel {
  PlanModel({
    required this.floorId,
    Map<String, PlanNode> nodes = const {},
    Map<String, PlanWall> walls = const {},
    Map<String, PlanRoom> rooms = const {},
    Map<String, PlanOpening> openings = const {},
    Map<String, double> fixedLengths = const {},
    Map<String, PlanMeasurement> measurements = const {},
  })  : nodes = Map.unmodifiable(nodes),
        walls = Map.unmodifiable(walls),
        rooms = Map.unmodifiable(rooms),
        openings = Map.unmodifiable(openings),
        fixedLengths = Map.unmodifiable(fixedLengths),
        measurements = Map.unmodifiable(measurements);

  final String floorId;
  final Map<String, PlanNode> nodes;
  final Map<String, PlanWall> walls;
  final Map<String, PlanRoom> rooms;
  final Map<String, PlanOpening> openings;

  /// Walls whose centerline length was fixed by a measurement (wall id -> mm). The server keeps these as
  /// constraints, so an edit that would change such a length has to be refused here as well.
  final Map<String, double> fixedLengths;

  /// The history of measurements taken on this plan's walls (newest information last in insertion order).
  final Map<String, PlanMeasurement> measurements;

  /// The measurements of one wall, newest first.
  List<PlanMeasurement> measurementsOf(String wallId) =>
      measurements.values.where((m) => m.wallId == wallId).toList()..sort((a, b) => b.capturedAt.compareTo(a.capturedAt));

  Vec2 pos(String nodeId) => nodes[nodeId]!.position;

  double length(PlanWall wall) => pos(wall.startNodeId).distanceTo(pos(wall.endNodeId));

  Iterable<PlanWall> wallsAt(String nodeId) => walls.values.where((w) => w.touches(nodeId));

  Iterable<PlanOpening> openingsOn(String wallId) => openings.values.where((o) => o.wallId == wallId);

  PlanModel copyWith({
    Map<String, PlanNode>? nodes,
    Map<String, PlanWall>? walls,
    Map<String, PlanRoom>? rooms,
    Map<String, PlanOpening>? openings,
    Map<String, double>? fixedLengths,
    Map<String, PlanMeasurement>? measurements,
    String? floorId,
  }) =>
      PlanModel(
        floorId: floorId ?? this.floorId,
        nodes: nodes ?? this.nodes,
        walls: walls ?? this.walls,
        rooms: rooms ?? this.rooms,
        openings: openings ?? this.openings,
        fixedLengths: fixedLengths ?? this.fixedLengths,
        measurements: measurements ?? this.measurements,
      );

  Map<String, Object?> toJson() => {
        'floorId': floorId,
        'nodes': [for (final n in nodes.values) {'id': n.id, 'x': n.x, 'y': n.y}],
        'walls': [
          for (final w in walls.values)
            {
              'id': w.id,
              'startNodeId': w.startNodeId,
              'endNodeId': w.endNodeId,
              'thicknessMm': w.thicknessMm,
              'heightMm': w.heightMm,
            }
        ],
        'rooms': [
          for (final r in rooms.values)
            {'id': r.id, 'name': r.name, 'roomType': r.roomType, 'areaType': r.areaType.wire, 'wallIds': r.wallIds}
        ],
        'openings': [
          for (final o in openings.values)
            {
              'id': o.id,
              'wallId': o.wallId,
              'kind': o.kind.name,
              'offsetMm': o.offsetMm,
              'widthMm': o.widthMm,
              'heightMm': o.heightMm,
              'sillMm': o.sillMm,
            }
        ],
        'fixedLengths': fixedLengths,
        'measurements': [
          for (final m in measurements.values)
            {
              'id': m.id,
              'wallId': m.wallId,
              'valueMm': m.valueMm,
              'inner': m.inner,
              'source': m.source.wire,
              'status': m.status.name,
              'capturedAt': m.capturedAt.toUtc().toIso8601String(),
              'deviceName': m.deviceName,
              'notes': m.notes,
            }
        ],
      };

  /// Reads a stored plan; plans saved before rooms and openings existed simply have none.
  factory PlanModel.fromJson(Map<String, Object?> json) {
    List<Map<String, Object?>> list(String key) => ((json[key] ?? const []) as List).cast<Map<String, Object?>>();

    return PlanModel(
      floorId: json['floorId']! as String,
      nodes: {
        for (final n in list('nodes')) n['id']! as String: PlanNode(n['id']! as String, (n['x']! as num).toDouble(), (n['y']! as num).toDouble())
      },
      walls: {
        for (final w in list('walls'))
          w['id']! as String: PlanWall(
            id: w['id']! as String,
            startNodeId: w['startNodeId']! as String,
            endNodeId: w['endNodeId']! as String,
            thicknessMm: (w['thicknessMm']! as num).toDouble(),
            heightMm: (w['heightMm']! as num).toDouble(),
          )
      },
      rooms: {
        for (final r in list('rooms'))
          r['id']! as String: PlanRoom(
            id: r['id']! as String,
            name: r['name']! as String,
            roomType: r['roomType']! as String,
            areaType: RoomAreaType.fromWire(r['areaType']! as String),
            wallIds: (r['wallIds']! as List).cast<String>(),
          )
      },
      openings: {
        for (final o in list('openings'))
          o['id']! as String: PlanOpening(
            id: o['id']! as String,
            wallId: o['wallId']! as String,
            kind: OpeningKind.values.byName(o['kind']! as String),
            offsetMm: (o['offsetMm']! as num).toDouble(),
            widthMm: (o['widthMm']! as num).toDouble(),
            heightMm: (o['heightMm']! as num).toDouble(),
            sillMm: (o['sillMm']! as num).toDouble(),
          )
      },
      fixedLengths: {for (final e in ((json['fixedLengths'] ?? const {}) as Map).entries) e.key as String: (e.value as num).toDouble()},
      measurements: {
        for (final m in list('measurements'))
          m['id']! as String: PlanMeasurement(
            id: m['id']! as String,
            wallId: m['wallId']! as String,
            valueMm: (m['valueMm']! as num).toDouble(),
            inner: m['inner']! as bool,
            source: MeasurementSource.fromWire(m['source']! as String),
            status: MeasurementStatus.fromWire(m['status']! as String),
            capturedAt: DateTime.parse(m['capturedAt']! as String),
            deviceName: m['deviceName'] as String?,
            notes: m['notes'] as String?,
          )
      },
    );
  }
}
