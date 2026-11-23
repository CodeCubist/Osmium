import 'package:flutter/material.dart';
import 'package:flutter_miuix/miuix.dart';

import '../../core/theme/osmium_motion.dart';
import 'osmium_page_transitions.dart';

/// Opens a full-screen route out of a source surface: a black rounded rectangle
/// grows from [sourceKey] (or [sourceRect]) to fill the screen, then the page
/// fades in over it. Popping reverses it and collapses back onto the source, or
/// falls back to a fade/scale when the source is gone.
class OsmiumContainerRoute<T> extends PageRoute<T> {
  OsmiumContainerRoute({
    required this.pageBuilder,
    this.sourceKey,
    this.sourceRect,
    this.sourceCornerRadius = 24,
    super.settings,
  });

  final WidgetBuilder pageBuilder;

  /// The widget the container grows from. While it stays mounted the exit
  /// animation tracks its current position.
  final GlobalKey? sourceKey;

  /// Fallback bounds captured at tap time.
  final Rect? sourceRect;

  final double sourceCornerRadius;

  @override
  bool get opaque => true;

  @override
  Color? get barrierColor => null;

  @override
  String? get barrierLabel => null;

  @override
  bool get maintainState => true;

  @override
  Duration get transitionDuration => OsmiumMotion.containerTransform;

  @override
  Duration get reverseTransitionDuration => OsmiumMotion.containerTransformBack;

  @override
  Widget buildPage(
    BuildContext context,
    Animation<double> animation,
    Animation<double> secondaryAnimation,
  ) {
    return pageBuilder(context);
  }

  @override
  Widget buildTransitions(
    BuildContext context,
    Animation<double> animation,
    Animation<double> secondaryAnimation,
    Widget child,
  ) {
    return OsmiumBackGestureDetector(
      route: this,
      child: _ContainerTransform(
        route: this,
        animation: animation,
        sourceKey: sourceKey,
        sourceRect: sourceRect,
        sourceCornerRadius: sourceCornerRadius,
        child: child,
      ),
    );
  }
}

class _ContainerTransform extends StatelessWidget {
  const _ContainerTransform({
    required this.route,
    required this.animation,
    required this.sourceKey,
    required this.sourceRect,
    required this.sourceCornerRadius,
    required this.child,
  });

  final PageRoute<dynamic> route;
  final Animation<double> animation;
  final GlobalKey? sourceKey;
  final Rect? sourceRect;
  final double sourceCornerRadius;
  final Widget child;

  static const Color _containerColor = Colors.black;

  /// The container accelerates out of the card and decelerates into place, so
  /// the growth is still visibly in flight a third of the way through rather
  /// than snapping to full screen on the spring's front-loaded displacement.
  static const Curve _growCurve = Curves.easeInOutCubic;

  /// Growth at which the page behind the container starts fading in.
  static const double _fadeStart = 0.5;

  Rect? _resolveSource(BuildContext context) {
    final BuildContext? sourceContext = sourceKey?.currentContext;
    if (sourceContext != null && sourceContext.mounted) {
      final RenderObject? object = sourceContext.findRenderObject();
      if (object is RenderBox && object.hasSize && !object.size.isEmpty) {
        return object.localToGlobal(Offset.zero) & object.size;
      }
    }
    return sourceRect;
  }

  @override
  Widget build(BuildContext context) {
    if (OsmiumMotion.reduced(context)) return child;

    final Size screen = MediaQuery.sizeOf(context);
    final Rect target = Offset.zero & screen;

    return AnimatedBuilder(
      animation: animation,
      child: child,
      builder: (context, child) {
        final Rect? source = _resolveSource(context);
        if (source == null) {
          final curved = CurvedAnimation(
            parent: animation,
            curve: MiuixMotion.standardDecelerate,
            reverseCurve: MiuixMotion.standardAccelerate,
          );
          return FadeTransition(
            opacity: curved,
            child: ScaleTransition(
              scale: Tween<double>(begin: 0.94, end: 1).animate(curved),
              child: child,
            ),
          );
        }

        final bool gesture = route.popGestureInProgress;
        final double raw = animation.value.clamp(0.0, 1.0);
        final double grow = (gesture ? raw : _growCurve.transform(raw)).clamp(
          0.0,
          1.0,
        );
        final double fade = ((grow - _fadeStart) / (1 - _fadeStart)).clamp(
          0.0,
          1.0,
        );

        final Rect rect = Rect.lerp(source, target, grow)!;
        final double radius = sourceCornerRadius * (1 - grow);

        return Stack(
          fit: StackFit.expand,
          children: [
            // Slight scrim so the source screen recedes while the card grows.
            IgnorePointer(
              child: ColoredBox(
                color: Colors.black.withValues(alpha: 0.22 * grow),
              ),
            ),
            Positioned.fromRect(
              rect: rect,
              child: ClipRRect(
                borderRadius: BorderRadius.circular(radius),
                child: const ColoredBox(color: _containerColor),
              ),
            ),
            Opacity(opacity: fade, child: child),
          ],
        );
      },
    );
  }
}
