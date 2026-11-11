import 'dart:math' as math;

/// A point or vector in plan coordinates (millimetres, Y axis pointing up).
class Vec2 {
  const Vec2(this.x, this.y);

  final double x;
  final double y;

  Vec2 operator +(Vec2 o) => Vec2(x + o.x, y + o.y);
  Vec2 operator -(Vec2 o) => Vec2(x - o.x, y - o.y);
  Vec2 operator *(double k) => Vec2(x * k, y * k);
  Vec2 operator /(double k) => Vec2(x / k, y / k);

  double get length => math.sqrt(x * x + y * y);

  double distanceTo(Vec2 o) => (this - o).length;

  static double dot(Vec2 a, Vec2 b) => a.x * b.x + a.y * b.y;

  static double cross(Vec2 a, Vec2 b) => a.x * b.y - a.y * b.x;

  @override
  bool operator ==(Object other) => other is Vec2 && other.x == x && other.y == y;

  @override
  int get hashCode => Object.hash(x, y);

  @override
  String toString() => 'Vec2($x, $y)';
}

/// The point of segment [a]-[b] closest to [p], and its position along the segment (0 at [a], 1 at [b]).
({Vec2 point, double t}) closestOnSegment(Vec2 a, Vec2 b, Vec2 p) {
  final ab = b - a;
  final lengthSquared = Vec2.dot(ab, ab);
  if (lengthSquared == 0) return (point: a, t: 0);
  final t = (Vec2.dot(p - a, ab) / lengthSquared).clamp(0.0, 1.0);
  return (point: a + ab * t, t: t);
}

/// Rounds like the server does (0.001 mm), so values compare equal after a round trip.
double roundMm(double value) => (value * 1000).roundToDouble() / 1000;
