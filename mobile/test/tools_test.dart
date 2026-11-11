import 'package:building_draft_app/geometry/commands.dart';
import 'package:building_draft_app/geometry/model.dart';
import 'package:building_draft_app/geometry/rooms.dart';
import 'package:building_draft_app/geometry/validation.dart';
import 'package:building_draft_app/geometry/vec2.dart';
import 'package:building_draft_app/state/plan_controller.dart';
import 'package:building_draft_app/state/viewport.dart';
import 'package:flutter_test/flutter_test.dart';

PlanController controllerWithRectangle({double thickness = 200}) {
  var model = PlanModel(floorId: 'f');
  const corners = [Vec2(0, 0), Vec2(4000, 0), Vec2(4000, 3000), Vec2(0, 3000)];
  for (var i = 0; i < 4; i++) {
    final next = (i + 1) % 4;
    model = CreateWall(
      wallId: 'w$i',
      startNodeId: 'n$i',
      start: corners[i],
      endNodeId: 'n$next',
      end: corners[next],
      thicknessMm: thickness,
    ).apply(model);
  }
  var n = 0;
  return PlanController(model: model, ids: () => 'id-${++n}')..viewport = const Viewport(pxPerMm: 0.1);
}

void tap(PlanController c, Vec2 p) {
  c.pointerDown(p);
  c.pointerUp(p);
}

void drag(PlanController c, Vec2 from, Vec2 to) {
  c.pointerDown(from);
  c.pointerMove(Vec2((from.x + to.x) / 2, (from.y + to.y) / 2));
  c.pointerMove(to);
  c.pointerUp(to);
}

void main() {
  group('room tool', () {
    test('tapping inside a closed loop proposes that area; naming it makes a room', () {
      final c = controllerWithRectangle()..selectTool(PlanTool.room);

      tap(c, const Vec2(2000, 1500));
      expect(c.roomProposal, isNotNull);
      expect(c.model.rooms, isEmpty);

      expect(c.createRoom('Living room', roomType: 'living_room'), isTrue);

      expect(c.model.rooms.values.single.name, 'Living room');
      expect(c.roomProposal, isNull);
      expect(c.serverCommands.last['commandType'], 'create_room');
      expect(roomMetrics(c.model, c.model.rooms.values.single)!.areaSqM, closeTo(10.64, 1e-9));
    });

    test('one undo takes the room back', () {
      final c = controllerWithRectangle()..selectTool(PlanTool.room);
      tap(c, const Vec2(2000, 1500));
      c.createRoom('Living room');

      c.undo();

      expect(c.model.rooms, isEmpty);
    });

    test('tapping outside any loop says so and proposes nothing', () {
      final c = controllerWithRectangle()..selectTool(PlanTool.room);

      tap(c, const Vec2(9000, 9000));

      expect(c.roomProposal, isNull);
      expect(c.lastError, contains('closed'));
    });

    test('a second tap on the same area selects the existing room instead of making a duplicate', () {
      final c = controllerWithRectangle()..selectTool(PlanTool.room);
      tap(c, const Vec2(2000, 1500));
      c.createRoom('Living room');

      tap(c, const Vec2(2000, 1500));

      expect(c.roomProposal, isNull);
      expect(c.selectedRoomId, c.model.rooms.keys.single);
      expect(c.lastError, contains('already a room'));
    });

    test('an empty name is refused and the proposal stays so the user can fix it', () {
      final c = controllerWithRectangle()..selectTool(PlanTool.room);
      tap(c, const Vec2(2000, 1500));

      expect(c.createRoom('   '), isFalse);
      expect(c.roomProposal, isNotNull);
      expect(c.createRoom('Kitchen'), isTrue);
    });

    test('a balcony is an open area', () {
      final c = controllerWithRectangle()..selectTool(PlanTool.room);
      tap(c, const Vec2(2000, 1500));

      c.createRoom('Balcony', roomType: 'balcony', areaType: RoomAreaType.openArea);

      expect(c.model.rooms.values.single.areaType, RoomAreaType.openArea);
      expect((c.serverCommands.last['payload']! as Map)['areaType'], 'open_area');
    });

    test('a wall that bounds a room cannot be deleted, and the reason is shown', () {
      final c = controllerWithRectangle()..selectTool(PlanTool.room);
      tap(c, const Vec2(2000, 1500));
      c.createRoom('Living room');
      c.selectTool(PlanTool.select);

      tap(c, const Vec2(2000, 0));
      c.deleteSelection();

      expect(c.model.walls, hasLength(4));
      expect(c.lastError, contains('rooms'));
    });

    test('a wall drawn to end on a room wall splits it and the room stays a valid loop', () {
      final c = controllerWithRectangle()..selectTool(PlanTool.room);
      tap(c, const Vec2(2000, 1500));
      c.createRoom('Living room');

      c.selectTool(PlanTool.wall);
      drag(c, const Vec2(2000, 0), const Vec2(2000, 3000));

      expect(c.lastError, isNull);
      expect(c.model.rooms.values.single.wallIds, hasLength(6));
      expect(validatePlan(c.model), isEmpty);
    });
  });

  group('door and window tools', () {
    test('hovering a wall shows where the door would go, centred on the pointer', () {
      final c = controllerWithRectangle()..selectTool(PlanTool.door);

      c.hoverAt(const Vec2(2000, 20));

      expect(c.openingPreview!.wallId, 'w0');
      expect(c.openingPreview!.offsetMm, 1550); // 2000 - 450
      expect(c.openingPreview!.widthMm, 900);
    });

    test('hovering empty space shows nothing', () {
      final c = controllerWithRectangle()..selectTool(PlanTool.door);

      c.hoverAt(const Vec2(2000, 1500));

      expect(c.openingPreview, isNull);
    });

    test('tapping a wall places a door of the default size and queues attach_opening', () {
      final c = controllerWithRectangle()..selectTool(PlanTool.door);

      tap(c, const Vec2(2000, 0));

      final door = c.model.openings.values.single;
      expect((door.kind, door.widthMm, door.heightMm, door.sillMm, door.offsetMm), (OpeningKind.door, 900, 2100, 0, 1550));
      expect(c.serverCommands.last['commandType'], 'attach_opening');
    });

    test('a window gets its own size and a sill', () {
      final c = controllerWithRectangle()..selectTool(PlanTool.window);

      tap(c, const Vec2(2000, 3000));

      final window = c.model.openings.values.single;
      expect((window.kind, window.widthMm, window.heightMm, window.sillMm), (OpeningKind.window, 1200, 1200, 900));
    });

    test('near a wall end the opening is pushed back inside the wall', () {
      final c = controllerWithRectangle()..selectTool(PlanTool.door);

      tap(c, const Vec2(100, 0));

      expect(c.model.openings.values.single.offsetMm, 0);
    });

    test('a second door that would overlap the first is refused with the reason', () {
      final c = controllerWithRectangle()..selectTool(PlanTool.door);
      tap(c, const Vec2(2000, 0));

      tap(c, const Vec2(2200, 0));

      expect(c.model.openings, hasLength(1));
      expect(c.lastError, contains('overlap'));
    });

    test('a wall too short for the opening, and a tap away from walls, give clear messages', () {
      final c = controllerWithRectangle();
      c.execute(const CreateWall(wallId: 'stub', startNodeId: 'a', start: Vec2(6000, 0), endNodeId: 'b', end: Vec2(6500, 0)));
      c.selectTool(PlanTool.door);

      tap(c, const Vec2(6250, 0));
      expect(c.lastError, contains('too short'));

      tap(c, const Vec2(2000, 1500));
      expect(c.lastError, contains('Tap a wall'));
    });

    test('one undo removes the opening', () {
      final c = controllerWithRectangle()..selectTool(PlanTool.door);
      tap(c, const Vec2(2000, 0));

      c.undo();

      expect(c.model.openings, isEmpty);
    });
  });

  group('selecting and moving openings', () {
    PlanController withDoor() {
      final c = controllerWithRectangle()..selectTool(PlanTool.door);
      tap(c, const Vec2(1000, 0)); // offset 550..1450
      c.selectTool(PlanTool.select);
      return c;
    }

    test('tapping a door selects the door, not the wall under it', () {
      final c = withDoor();

      tap(c, const Vec2(1000, 0));

      expect(c.selectedOpeningId, isNotNull);
      expect(c.selectedWallId, isNull);
    });

    test('tapping the wall beside the door selects the wall', () {
      final c = withDoor();

      tap(c, const Vec2(3000, 0));

      expect(c.selectedWallId, 'w0');
      expect(c.selectedOpeningId, isNull);
    });

    test('dragging a door along its wall moves it in 50 mm steps, as one undoable step', () {
      final c = withDoor();
      final before = c.serverCommands.length;

      drag(c, const Vec2(1000, 0), const Vec2(2013, 0));

      expect(c.model.openings.values.single.offsetMm, 1550); // grabbed at its middle: 2013 - 450 -> 1563 -> 1550
      expect(c.serverCommands.length - before, 2, reason: 'remove_opening + attach_opening');
      c.undo();
      expect(c.model.openings.values.single.offsetMm, 550);
    });

    test('a door cannot be dragged past the end of its wall', () {
      final c = withDoor();

      drag(c, const Vec2(1000, 0), const Vec2(9000, 0));

      expect(c.model.openings.values.single.offsetMm, 3100); // 4000 - 900
    });

    test('Delete removes the selected opening', () {
      final c = withDoor();
      tap(c, const Vec2(1000, 0));

      c.deleteSelection();

      expect(c.model.openings, isEmpty);
    });

    test('tapping the inside of a room selects the room, and Delete removes it', () {
      final c = controllerWithRectangle()..selectTool(PlanTool.room);
      tap(c, const Vec2(2000, 1500));
      c.createRoom('Living room');
      c.selectTool(PlanTool.select);

      tap(c, const Vec2(2000, 1500));
      expect(c.selectedRoomId, isNotNull);

      c.deleteSelection();
      expect(c.model.rooms, isEmpty);
      expect(c.model.walls, hasLength(4), reason: 'only the room is removed');
    });
  });

  group('measuring', () {
    test('a measured wall is resized, fixed, and queued as apply_measurement', () {
      final c = controllerWithRectangle();
      tap(c, const Vec2(2000, 0));

      expect(c.applyMeasurement(3900), isTrue); // inner 3900 -> centerline 4100

      expect(c.model.pos('n1'), const Vec2(4100, 0));
      expect(c.model.fixedLengths['w0'], 4100);
      expect(c.serverCommands.last['commandType'], 'apply_measurement');
      expect((c.serverCommands.last['payload']! as Map)['reference'], 'inner');
    });

    test('the corners of a measured wall cannot be dragged afterwards', () {
      final c = controllerWithRectangle();
      tap(c, const Vec2(2000, 0));
      c.applyMeasurement(3800); // 4000 centerline: n1 stays
      final before = c.model.pos('n1');

      drag(c, const Vec2(4000, 0), const Vec2(4500, 0));

      expect(c.lastError, contains('fixed'));
      expect(c.model.pos('n1'), before);
    });

    test('measuring needs a selected wall', () {
      final c = controllerWithRectangle();

      expect(c.applyMeasurement(3000), isFalse);
      expect(c.lastError, contains('Select a wall'));
    });

    test('a measurement that would break the plan is refused', () {
      final c = controllerWithRectangle();
      tap(c, const Vec2(2000, 0));

      // 6 m on the bottom wall drags its end corner across the right wall's far side.
      c.execute(const AttachOpening(openingId: 'o', wallId: 'w0', kind: OpeningKind.door, offsetMm: 3000, widthMm: 900, heightMm: 2100));
      final accepted = c.applyMeasurement(1000, inner: false);

      expect(accepted, isFalse);
      expect(c.lastError, contains('door'));
      expect(c.model.fixedLengths, isEmpty);
    });
  });
}
