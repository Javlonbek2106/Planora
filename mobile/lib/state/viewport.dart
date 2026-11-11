import 'dart:ui';

import '../geometry/vec2.dart';

/// Maps plan millimetres (Y up) to screen pixels (Y down) and back.
class Viewport {
  const Viewport({this.origin = Offset.zero, this.pxPerMm = 0.1});

  /// Screen position of the plan origin.
  final Offset origin;
  final double pxPerMm;

  static const minScale = 0.005;
  static const maxScale = 2.0;

  Offset toScreen(Vec2 p) => Offset(origin.dx + p.x * pxPerMm, origin.dy - p.y * pxPerMm);

  Vec2 toPlan(Offset s) => Vec2((s.dx - origin.dx) / pxPerMm, (origin.dy - s.dy) / pxPerMm);

  Viewport panBy(Offset delta) => Viewport(origin: origin + delta, pxPerMm: pxPerMm);

  /// Zooms by [factor] keeping the plan point under [focal] where it is.
  Viewport zoomAt(Offset focal, double factor) {
    final next = (pxPerMm * factor).clamp(minScale, maxScale);
    final k = next / pxPerMm;
    return Viewport(origin: focal - (focal - origin) * k, pxPerMm: next);
  }

  /// Fits [min]..[max] (plan coordinates) into [size] with [paddingPx] around.
  static Viewport fit(Vec2 min, Vec2 max, Size size, {double paddingPx = 48}) {
    final width = (max.x - min.x).clamp(1.0, double.infinity);
    final height = (max.y - min.y).clamp(1.0, double.infinity);
    final scale = ((size.width - 2 * paddingPx) / width).clamp(0.0, (size.height - 2 * paddingPx) / height).clamp(minScale, maxScale);
    final center = (min + max) / 2;
    return Viewport(origin: Offset(size.width / 2 - center.x * scale, size.height / 2 + center.y * scale), pxPerMm: scale);
  }
}
