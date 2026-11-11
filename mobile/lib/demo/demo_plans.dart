import '../geometry/model.dart';

/// Ready-made plans for trying the editor and for performance checks.
class DemoPlans {
  const DemoPlans._();

  /// A small flat: a living room, a bedroom and a kitchen, with two doors, two windows and one measured wall.
  static PlanModel apartment({String floorId = 'floor-1'}) {
    const corners = <String, (double, double)>{
      'a': (0, 0), 'b': (5200, 0), 'c': (8200, 0), 'd': (8200, 4200), 'e': (5200, 4200), 'f': (0, 4200),
      'g': (5200, 2600), 'h': (8200, 2600),
    };
    const walls = [
      ('a', 'b'), ('b', 'c'), ('c', 'h'), ('h', 'd'), ('d', 'e'), ('e', 'f'), ('f', 'a'),
      ('b', 'g'), ('g', 'e'), ('g', 'h'),
    ];
    return PlanModel(
      floorId: floorId,
      nodes: {for (final e in corners.entries) e.key: PlanNode(e.key, e.value.$1, e.value.$2)},
      walls: {
        for (final (i, w) in walls.indexed)
          'w$i': PlanWall(id: 'w$i', startNodeId: w.$1, endNodeId: w.$2, thicknessMm: i < 7 ? 250 : 120),
      },
      rooms: const {
        'r-living': PlanRoom(id: 'r-living', name: 'Living room', roomType: 'living_room', wallIds: ['w0', 'w7', 'w8', 'w5', 'w6']),
        'r-bedroom': PlanRoom(id: 'r-bedroom', name: 'Bedroom', roomType: 'bedroom', wallIds: ['w1', 'w2', 'w9', 'w7']),
        'r-kitchen': PlanRoom(id: 'r-kitchen', name: 'Kitchen', roomType: 'kitchen', wallIds: ['w9', 'w3', 'w4', 'w8']),
      },
      openings: const {
        'o-door-bedroom': PlanOpening(id: 'o-door-bedroom', wallId: 'w7', kind: OpeningKind.door, offsetMm: 900, widthMm: 900, heightMm: 2100),
        'o-door-kitchen': PlanOpening(id: 'o-door-kitchen', wallId: 'w8', kind: OpeningKind.door, offsetMm: 350, widthMm: 900, heightMm: 2100),
        'o-window-living': PlanOpening(id: 'o-window-living', wallId: 'w6', kind: OpeningKind.window, offsetMm: 1500, widthMm: 1200, heightMm: 1200, sillMm: 900),
        'o-window-bedroom': PlanOpening(id: 'o-window-bedroom', wallId: 'w1', kind: OpeningKind.window, offsetMm: 900, widthMm: 1200, heightMm: 1200, sillMm: 900),
      },
      fixedLengths: const {'w0': 5200},
    );
  }

  /// A lattice of [columns] x [rows] rooms (cell 3 m): 2 * columns * rows + columns + rows walls. 10 x 10 gives
  /// 220, a little above the 100-200 wall target of the PRD's geometry stability test.
  static PlanModel lattice({int columns = 10, int rows = 10, double cellMm = 3000, String floorId = 'floor-1'}) {
    String id(int x, int y) => 'n$x-$y';
    final nodes = <String, PlanNode>{
      for (var y = 0; y <= rows; y++)
        for (var x = 0; x <= columns; x++) id(x, y): PlanNode(id(x, y), x * cellMm, y * cellMm),
    };
    final walls = <String, PlanWall>{};
    void add(String a, String b) => walls['w${walls.length}'] = PlanWall(id: 'w${walls.length}', startNodeId: a, endNodeId: b);
    for (var y = 0; y <= rows; y++) {
      for (var x = 0; x < columns; x++) {
        add(id(x, y), id(x + 1, y));
      }
    }
    for (var x = 0; x <= columns; x++) {
      for (var y = 0; y < rows; y++) {
        add(id(x, y), id(x, y + 1));
      }
    }
    return PlanModel(floorId: floorId, nodes: nodes, walls: walls);
  }
}
