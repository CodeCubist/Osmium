import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../core/theme/osmium_motion.dart';

/// HyperOS-style push: the incoming page slides in from the leading edge while
/// the page underneath shifts a quarter of the width and dims.
///
/// Wrapped in [OsmiumBackGestureDetector], so a predictive back swipe drives
/// the same animation with the finger and springs back when cancelled.
class OsmiumPageTransitionsBuilder extends PageTransitionsBuilder {
  const OsmiumPageTransitionsBuilder();

  @override
  Duration get transitionDuration => OsmiumMotion.pagePush;

  @override
  Duration get reverseTransitionDuration => OsmiumMotion.pagePop;

  @override
  Widget buildTransitions<T>(
    PageRoute<T> route,
    BuildContext context,
    Animation<double> animation,
    Animation<double> secondaryAnimation,
    Widget child,
  ) {
    return OsmiumBackGestureDetector(
      route: route,
      child: _OsmiumPushTransition(
        route: route,
        animation: animation,
        secondaryAnimation: secondaryAnimation,
        child: child,
      ),
    );
  }
}

/// Registers as a [WidgetsBindingObserver] and forwards Android predictive back
/// events into the route's own controller, exactly like the framework's
/// private detector. Without this a custom [PageTransitionsBuilder] never
/// receives gesture progress.
class OsmiumBackGestureDetector extends StatefulWidget {
  const OsmiumBackGestureDetector({
    super.key,
    required this.route,
    required this.child,
  });

  final PageRoute<dynamic> route;
  final Widget child;

  @override
  State<OsmiumBackGestureDetector> createState() =>
      _OsmiumBackGestureDetectorState();
}

class _OsmiumBackGestureDetectorState extends State<OsmiumBackGestureDetector>
    with WidgetsBindingObserver {
  bool get _enabled => widget.route.isCurrent && widget.route.popGestureEnabled;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  @override
  bool handleStartBackGesture(PredictiveBackEvent backEvent) {
    if (backEvent.isButtonEvent || !_enabled) return false;
    widget.route.handleStartBackGesture(progress: 1 - backEvent.progress);
    return true;
  }

  @override
  void handleUpdateBackGestureProgress(PredictiveBackEvent backEvent) {
    widget.route.handleUpdateBackGestureProgress(
      progress: 1 - backEvent.progress,
    );
  }

  @override
  void handleCancelBackGesture() => widget.route.handleCancelBackGesture();

  @override
  void handleCommitBackGesture() => widget.route.handleCommitBackGesture();

  @override
  Widget build(BuildContext context) => widget.child;
}

class _OsmiumPushTransition extends StatelessWidget {
  const _OsmiumPushTransition({
    required this.route,
    required this.animation,
    required this.secondaryAnimation,
    required this.child,
  });

  final PageRoute<dynamic> route;
  final Animation<double> animation;
  final Animation<double> secondaryAnimation;
  final Widget child;

  /// How far the page underneath travels, as a fraction of the width.
  static const double _parallax = 0.28;

  /// Maximum dim applied to the page underneath.
  static const double _dim = 0.16;

  @override
  Widget build(BuildContext context) {
    if (OsmiumMotion.reduced(context)) return child;

    final bool rtl = Directionality.of(context) == TextDirection.rtl;
    final double direction = rtl ? -1 : 1;

    return AnimatedBuilder(
      animation: Listenable.merge([animation, secondaryAnimation]),
      child: child,
      builder: (context, child) {
        final bool gesture =
            route.popGestureInProgress ||
            (route.navigator?.userGestureInProgress ?? false);
        final double rawEnter = animation.value.clamp(0.0, 1.0);
        final double rawCover = secondaryAnimation.value.clamp(0.0, 1.0);
        final double enter = gesture
            ? rawEnter
            : OsmiumMotion.pushCurve.transform(rawEnter);
        final double cover = gesture
            ? rawCover
            : OsmiumMotion.pushCurve.transform(rawCover);

        final double dx = (1 - enter) * direction;
        final double underDx = -cover * _parallax * direction;

        return FractionalTranslation(
          translation: Offset(dx + underDx, 0),
          child: Stack(
            fit: StackFit.expand,
            children: [
              child!,
              if (cover > 0)
                IgnorePointer(
                  child: ColoredBox(
                    color: Colors.black.withValues(alpha: _dim * cover),
                  ),
                ),
            ],
          ),
        );
      },
    );
  }
}
