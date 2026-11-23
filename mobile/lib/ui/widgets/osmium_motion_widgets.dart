import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_miuix/miuix.dart';

import '../../core/theme/osmium_motion.dart';

/// Slide-in/out reveal that animates both size and opacity while keeping the
/// child mounted, so its controllers and focus stay intact across collapses.
class OsmiumAnimatedReveal extends StatefulWidget {
  const OsmiumAnimatedReveal({
    super.key,
    required this.visible,
    required this.child,
    this.duration = OsmiumMotion.small,
  });

  final bool visible;
  final Widget child;
  final Duration duration;

  @override
  State<OsmiumAnimatedReveal> createState() => _OsmiumAnimatedRevealState();
}

class _OsmiumAnimatedRevealState extends State<OsmiumAnimatedReveal>
    with SingleTickerProviderStateMixin {
  late final AnimationController _controller = AnimationController(
    vsync: this,
    duration: widget.duration,
    value: widget.visible ? 1 : 0,
  );

  late final CurvedAnimation _curve = CurvedAnimation(
    parent: _controller,
    curve: MiuixMotion.standardDecelerate,
    reverseCurve: MiuixMotion.standardAccelerate,
  );

  bool _reduced = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _reduced = OsmiumMotion.reduced(context);
  }

  @override
  void didUpdateWidget(OsmiumAnimatedReveal oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.visible == widget.visible) return;
    if (_reduced) {
      _controller.value = widget.visible ? 1 : 0;
    } else if (widget.visible) {
      _controller.forward();
    } else {
      _controller.reverse();
    }
  }

  @override
  void dispose() {
    _curve.dispose();
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: _curve,
      builder: (context, child) {
        final value = _curve.value;
        return ClipRect(
          child: Align(
            alignment: Alignment.topCenter,
            heightFactor: value,
            child: Opacity(
              opacity: value.clamp(0.0, 1.0),
              child: IgnorePointer(
                ignoring: !widget.visible,
                child: ExcludeFocus(excluding: !widget.visible, child: child!),
              ),
            ),
          ),
        );
      },
      child: widget.child,
    );
  }
}

/// Collapses its child to zero height and back when [collapsed] changes.
///
/// Unlike [OsmiumAnimatedReveal] this is driven from the outside so a caller
/// can play the collapse first and drop the row afterwards.
class OsmiumCollapse extends StatefulWidget {
  const OsmiumCollapse({
    super.key,
    required this.collapsed,
    required this.child,
    this.onCollapsed,
    this.duration = const Duration(milliseconds: 220),
  });

  final bool collapsed;
  final Widget child;
  final VoidCallback? onCollapsed;
  final Duration duration;

  @override
  State<OsmiumCollapse> createState() => _OsmiumCollapseState();
}

class _OsmiumCollapseState extends State<OsmiumCollapse>
    with SingleTickerProviderStateMixin {
  late final AnimationController _controller = AnimationController(
    vsync: this,
    duration: widget.duration,
    value: widget.collapsed ? 0 : 1,
  );

  late final CurvedAnimation _curve = CurvedAnimation(
    parent: _controller,
    curve: MiuixMotion.standardDecelerate,
    reverseCurve: MiuixMotion.standardAccelerate,
  );

  bool _reduced = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _reduced = OsmiumMotion.reduced(context);
  }

  @override
  void initState() {
    super.initState();
    _controller.addStatusListener((status) {
      if (status == AnimationStatus.dismissed) widget.onCollapsed?.call();
    });
  }

  @override
  void didUpdateWidget(OsmiumCollapse oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.collapsed == widget.collapsed) return;
    if (_reduced) {
      _controller.value = widget.collapsed ? 0 : 1;
      if (widget.collapsed) widget.onCollapsed?.call();
    } else if (widget.collapsed) {
      _controller.reverse();
    } else {
      _controller.forward();
    }
  }

  @override
  void dispose() {
    _curve.dispose();
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: _curve,
      builder: (context, child) => ClipRect(
        child: Align(
          alignment: Alignment.topCenter,
          heightFactor: _curve.value,
          child: Opacity(opacity: _curve.value.clamp(0.0, 1.0), child: child),
        ),
      ),
      child: widget.child,
    );
  }
}

/// Fades and lifts a row into place once, after an optional stagger delay.
///
/// Mounting is the trigger, so untouched rows never replay on rebuilds.
class OsmiumEntrance extends StatefulWidget {
  const OsmiumEntrance({
    super.key,
    required this.child,
    this.index = 0,
    this.delayStep = const Duration(milliseconds: 40),
    this.maxDelay = const Duration(milliseconds: 160),
    this.offset = 10,
  });

  final Widget child;
  final int index;
  final Duration delayStep;
  final Duration maxDelay;
  final double offset;

  @override
  State<OsmiumEntrance> createState() => _OsmiumEntranceState();
}

class _OsmiumEntranceState extends State<OsmiumEntrance>
    with SingleTickerProviderStateMixin {
  static const Duration _rise = Duration(milliseconds: 300);

  Duration get _delay {
    final raw = widget.delayStep * widget.index;
    return raw > widget.maxDelay ? widget.maxDelay : raw;
  }

  // The stagger lives inside one controller (an Interval) instead of a timer,
  // so no pending timers survive a frame and the row can be disposed freely.
  late final AnimationController _controller = AnimationController(
    vsync: this,
    duration: _rise + _delay,
  );

  late final CurvedAnimation _curve = CurvedAnimation(
    parent: _controller,
    curve: Interval(
      _delay.inMicroseconds / _controller.duration!.inMicroseconds,
      1,
      curve: MiuixMotion.standardDecelerate,
    ),
  );

  bool _started = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (_started) return;
    _started = true;
    if (OsmiumMotion.reduced(context)) {
      _controller.value = 1;
    } else {
      _controller.forward();
    }
  }

  @override
  void dispose() {
    _curve.dispose();
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: _curve,
      builder: (context, child) {
        final value = _curve.value;
        return Opacity(
          opacity: value,
          child: Transform.translate(
            offset: Offset(0, (1 - value) * widget.offset),
            child: child,
          ),
        );
      },
      child: widget.child,
    );
  }
}

/// Short horizontal spring shake, used for invalid input.
class OsmiumShake extends StatefulWidget {
  const OsmiumShake({super.key, required this.child, this.amplitude = 9});

  final Widget child;
  final double amplitude;

  @override
  State<OsmiumShake> createState() => OsmiumShakeState();
}

class OsmiumShakeState extends State<OsmiumShake>
    with SingleTickerProviderStateMixin {
  late final AnimationController _controller = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 360),
  );

  void shake() {
    if (OsmiumMotion.reduced(context)) return;
    _controller.forward(from: 0);
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: _controller,
      builder: (context, child) {
        final t = _controller.value;
        final dx = math.sin(t * math.pi * 5) * (1 - t) * widget.amplitude;
        return Transform.translate(offset: Offset(dx, 0), child: child);
      },
      child: widget.child,
    );
  }
}

/// Icon that briefly morphs from a copy glyph to a check on success.
class OsmiumCopyMorph extends StatelessWidget {
  const OsmiumCopyMorph({
    super.key,
    required this.copied,
    required this.color,
    this.size = 18,
  });

  final bool copied;
  final Color color;
  final double size;

  @override
  Widget build(BuildContext context) {
    final reduced = OsmiumMotion.reduced(context);
    return AnimatedSwitcher(
      duration: reduced ? Duration.zero : const Duration(milliseconds: 220),
      switchInCurve: MiuixMotion.standardDecelerate,
      switchOutCurve: MiuixMotion.standardAccelerate,
      transitionBuilder: (child, animation) => ScaleTransition(
        scale: Tween<double>(begin: 0.6, end: 1).animate(animation),
        child: FadeTransition(opacity: animation, child: child),
      ),
      child: Icon(
        copied ? Icons.check_rounded : Icons.copy_rounded,
        key: ValueKey(copied),
        size: size,
        color: color,
        semanticLabel: copied ? '已复制' : '复制',
      ),
    );
  }
}
