import 'package:flutter/material.dart';
import 'package:flutter_miuix/miuix.dart';

import '../../core/theme/osmium_motion.dart';

/// Keeps every tab mounted (state preserved) but only paints the active one.
///
/// Switching runs a short fade-through: the outgoing tab fades fast in place,
/// the incoming tab fades in while drifting a few pixels from the side it sits
/// on in the dock. Only those two subtrees are rebuilt per frame; the third
/// stays offstage with its tickers muted.
class OsmiumTabSwitcher extends StatefulWidget {
  const OsmiumTabSwitcher({
    super.key,
    required this.index,
    required this.children,
    this.duration = OsmiumMotion.tabSwitch,
  });

  final int index;
  final List<Widget> children;
  final Duration duration;

  @override
  State<OsmiumTabSwitcher> createState() => _OsmiumTabSwitcherState();
}

class _OsmiumTabSwitcherState extends State<OsmiumTabSwitcher>
    with SingleTickerProviderStateMixin {
  late final AnimationController _controller = AnimationController(
    vsync: this,
    duration: widget.duration,
    value: 1,
  )..addStatusListener(_onStatus);

  /// How far the incoming tab drifts. A hint of direction, not a page slide.
  static const double _drift = 20;

  late final CurvedAnimation _incomingCurve = CurvedAnimation(
    parent: _controller,
    curve: MiuixMotion.standardDecelerate,
  );

  // The incoming fade starts a beat late so the two tabs barely overlap.
  late final CurvedAnimation _incomingFade = CurvedAnimation(
    parent: _controller,
    curve: const Interval(0.2, 1, curve: MiuixMotion.standardDecelerate),
  );

  late final CurvedAnimation _outgoingCurve = CurvedAnimation(
    parent: _controller,
    curve: const Interval(0, 0.35, curve: MiuixMotion.standardAccelerate),
  );

  late int _current = widget.index;
  int? _previous;
  // 1 when moving to a tab on the right, -1 when moving left.
  double _direction = 1;
  bool _reduced = false;

  void _onStatus(AnimationStatus status) {
    if (status != AnimationStatus.completed || _previous == null) return;
    _previous = null;
    if (mounted) setState(() {});
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _reduced = OsmiumMotion.reduced(context);
  }

  @override
  void didUpdateWidget(OsmiumTabSwitcher oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.index == _current) return;
    final int outgoing = _current;
    _current = widget.index;
    _direction = _current > outgoing ? 1 : -1;
    if (_reduced) {
      _previous = null;
      _controller.value = 1;
    } else {
      _previous = outgoing;
      _controller.forward(from: 0);
    }
  }

  @override
  void dispose() {
    _incomingCurve.dispose();
    _incomingFade.dispose();
    _outgoingCurve.dispose();
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final bool animating = _previous != null;
    final int? previous = _previous;

    return Stack(
      fit: StackFit.expand,
      children: [
        for (int i = 0; i < widget.children.length; i++)
          _tab(i, animating, previous),
      ],
    );
  }

  Widget _tab(int index, bool animating, int? previous) {
    final bool active = index == _current;
    final bool leaving = animating && index == previous;
    final bool visible = active || leaving;

    // The shape of this subtree must never change between states: swapping the
    // AnimatedBuilder in or out reparents the tab and would re-create its state
    // on every switch. So every tab is always wrapped the same way and only the
    // animated values differ.
    return Positioned.fill(
      child: Offstage(
        offstage: !visible,
        child: TickerMode(
          enabled: active,
          child: RepaintBoundary(
            child: AnimatedBuilder(
              animation: _controller,
              child: widget.children[index],
              builder: (context, child) {
                double opacity = active ? 1.0 : 0.0;
                double dx = 0;
                if (active && animating) {
                  opacity = _incomingFade.value;
                  dx = (1 - _incomingCurve.value) * _drift * _direction;
                } else if (leaving) {
                  opacity = 1 - _outgoingCurve.value;
                }
                return Opacity(
                  opacity: opacity.clamp(0.0, 1.0),
                  child: Transform.translate(
                    offset: Offset(dx, 0),
                    child: child,
                  ),
                );
              },
            ),
          ),
        ),
      ),
    );
  }
}
