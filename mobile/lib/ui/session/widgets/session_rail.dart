import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/physics.dart';

import '../../../core/theme/osmium_motion.dart';
import '../../../core/theme/osmium_theme.dart';

/// One button on the [SessionRail].
class SessionRailAction {
  const SessionRailAction({
    required this.icon,
    required this.label,
    required this.onTap,
    this.active = false,
  });

  final IconData icon;
  final String label;
  final VoidCallback onTap;
  final bool active;
}

/// Frosted action rail docked to a screen edge during a session.
///
/// Dragged, it follows the finger and springs back onto the nearer edge,
/// keeping its height across rotations. It folds down to a slim tab when the
/// picture needs the room.
class SessionRail extends StatefulWidget {
  const SessionRail({super.key, required this.actions, this.visible = true});

  final List<SessionRailAction> actions;

  /// False while the panel or a keyboard is up: the rail shrinks into its
  /// edge instead of disappearing on a single frame.
  final bool visible;

  @override
  State<SessionRail> createState() => _SessionRailState();
}

class _SessionRailState extends State<SessionRail>
    with TickerProviderStateMixin {
  static const double _width = 54;
  static const double _itemHeight = 48;
  static const double _foldHeight = 26;
  static const double _margin = 8;
  // Leaves the bottom corner to the OSD capsule.
  static const double _bottomClear = 56;

  // Shared across sessions.
  static bool _restRight = true;
  static double _restFraction = 0.5;
  static bool _folded = false;

  /// Box position while a finger or the snap spring owns the rail.
  Offset? _live;
  Size? _lastScreen;
  bool _dragging = false;
  bool _idle = false;
  Timer? _idleTimer;

  late final AnimationController _snapX;
  late final AnimationController _snapY;
  late final AnimationController _reveal;

  static final _spring = SpringDescription.withDampingRatio(
    mass: 1.0,
    stiffness: 380.0,
    ratio: 0.85,
  );

  // Top inset plus the glass border on both edges.
  double get _railHeight =>
      widget.actions.length * _itemHeight + _foldHeight + 8;

  @override
  void initState() {
    super.initState();
    _reveal = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 220),
      reverseDuration: const Duration(milliseconds: 180),
      value: widget.visible ? 1 : 0,
    );
    _snapX = AnimationController.unbounded(vsync: this)..addListener(_onSnap);
    _snapY = AnimationController.unbounded(vsync: this)..addListener(_onSnap);
    _scheduleIdle();
  }

  @override
  void didUpdateWidget(covariant SessionRail oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.visible == oldWidget.visible) return;
    if (OsmiumMotion.reduced(context)) {
      _reveal.value = widget.visible ? 1 : 0;
    } else if (widget.visible) {
      _reveal.forward();
    } else {
      _reveal.reverse();
    }
    if (widget.visible) _scheduleIdle();
  }

  @override
  void dispose() {
    _idleTimer?.cancel();
    _snapX.dispose();
    _snapY.dispose();
    _reveal.dispose();
    super.dispose();
  }

  void _onSnap() {
    if (_dragging) return;
    setState(() {
      _live = _snapX.isAnimating || _snapY.isAnimating
          ? Offset(_snapX.value, _snapY.value)
          : null;
    });
  }

  void _scheduleIdle() {
    _idleTimer?.cancel();
    if (_idle) setState(() => _idle = false);
    _idleTimer = Timer(const Duration(seconds: 3), () {
      if (mounted && !_dragging) setState(() => _idle = true);
    });
  }

  double _boxX(Size screen, EdgeInsets pad, bool right) =>
      right ? screen.width - pad.right - _margin - _width : pad.left + _margin;

  (double, double) _boxYRange(Size screen, EdgeInsets pad) {
    final minY = pad.top + 16;
    final maxY = screen.height - pad.bottom - _railHeight - _bottomClear;
    return (minY, maxY < minY ? minY : maxY);
  }

  Offset _restBox(Size screen, EdgeInsets pad) {
    final (minY, maxY) = _boxYRange(screen, pad);
    return Offset(
      _boxX(screen, pad, _restRight),
      minY + (maxY - minY) * _restFraction,
    );
  }

  void _snapToEdge(Size screen, EdgeInsets pad, Offset velocity) {
    final from = _live!;
    final (minY, maxY) = _boxYRange(screen, pad);
    _restRight = (from.dx + _width / 2) >= screen.width / 2;
    final targetX = _boxX(screen, pad, _restRight);
    final targetY = (from.dy + velocity.dy * 0.08).clamp(minY, maxY);
    _restFraction = maxY > minY ? (targetY - minY) / (maxY - minY) : 0.5;

    if (OsmiumMotion.reduced(context)) {
      setState(() => _live = null);
      return;
    }
    _snapX.animateWith(
      SpringSimulation(_spring, from.dx, targetX, velocity.dx),
    );
    _snapY.animateWith(
      SpringSimulation(_spring, from.dy, targetY, velocity.dy),
    );
  }

  void _setFolded(bool folded) {
    OsmiumHaptics.selection();
    setState(() => _folded = folded);
    _scheduleIdle();
  }

  @override
  Widget build(BuildContext context) {
    final screen = MediaQuery.sizeOf(context);
    final pad = MediaQuery.paddingOf(context);

    if (_lastScreen != null && _lastScreen != screen && !_dragging) {
      // Rotated or resized mid-snap: settle on the rest spot at once.
      _snapX.stop();
      _snapY.stop();
      _live = null;
    }
    _lastScreen = screen;
    final box = _live ?? _restBox(screen, pad);
    final right = _live == null
        ? _restRight
        : box.dx + _width / 2 >= screen.width / 2;
    final edge = right ? Alignment.centerRight : Alignment.centerLeft;

    return Positioned(
      key: const ValueKey('session-rail'),
      left: box.dx,
      top: box.dy,
      width: _width,
      height: _railHeight,
      child: AnimatedBuilder(
        animation: _reveal,
        builder: (context, child) {
          final t = Curves.easeOutCubic.transform(
            _reveal.value.clamp(0.0, 1.0),
          );
          return IgnorePointer(
            ignoring: t < 0.05,
            child: Opacity(
              opacity: t,
              child: Transform.scale(
                scale: 0.82 + 0.18 * t,
                alignment: edge,
                child: child,
              ),
            ),
          );
        },
        child: GestureDetector(
          onPanStart: (_) {
            _snapX.stop();
            _snapY.stop();
            _dragging = true;
            _live = box;
            _scheduleIdle();
          },
          onPanUpdate: (d) {
            final from = _live ?? box;
            setState(() {
              _live = Offset(
                (from.dx + d.delta.dx).clamp(0.0, screen.width - _width),
                (from.dy + d.delta.dy).clamp(
                  pad.top + 8,
                  screen.height - pad.bottom - _railHeight - 8,
                ),
              );
            });
          },
          onPanEnd: (d) {
            _dragging = false;
            _snapToEdge(screen, pad, d.velocity.pixelsPerSecond);
            _scheduleIdle();
          },
          child: AnimatedOpacity(
            duration: const Duration(milliseconds: 240),
            opacity: _dragging ? 1 : (_idle ? 0.56 : 1),
            child: Align(
              alignment: edge,
              child: AnimatedSwitcher(
                duration: OsmiumMotion.durationOr(context, OsmiumMotion.small),
                switchInCurve: Curves.easeOutCubic,
                switchOutCurve: Curves.easeInCubic,
                layoutBuilder: (current, previous) => Stack(
                  alignment: edge,
                  children: [...previous, ?current],
                ),
                transitionBuilder: (child, animation) => FadeTransition(
                  opacity: animation,
                  child: ScaleTransition(
                    scale: Tween<double>(
                      begin: 0.86,
                      end: 1,
                    ).animate(animation),
                    alignment: edge,
                    child: child,
                  ),
                ),
                child: _folded
                    ? _FoldedTab(
                        key: const ValueKey('folded'),
                        right: right,
                        onTap: () => _setFolded(false),
                      )
                    : _RailBody(
                        key: const ValueKey('rail'),
                        actions: widget.actions,
                        right: right,
                        onAction: (action) {
                          _scheduleIdle();
                          OsmiumHaptics.selection();
                          action.onTap();
                        },
                        onFold: () => _setFolded(true),
                      ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _RailGlass extends StatelessWidget {
  const _RailGlass({
    required this.width,
    required this.radius,
    required this.child,
    this.height,
  });

  final double width;
  final double? height;
  final double radius;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    final shape = BorderRadius.circular(radius);
    return DecoratedBox(
      decoration: BoxDecoration(
        borderRadius: shape,
        boxShadow: const [
          BoxShadow(
            color: Color(0x47000000),
            blurRadius: 18,
            offset: Offset(0, 6),
          ),
        ],
      ),
      child: ClipRRect(
        borderRadius: shape,
        child: BackdropFilter(
          filter: OsmiumGlass.filter(20),
          child: Container(
            width: width,
            height: height,
            decoration: BoxDecoration(
              gradient: const LinearGradient(
                begin: Alignment.topLeft,
                end: Alignment.bottomRight,
                colors: [Color(0x80192130), OsmiumColors.sessionGlassFill],
              ),
              borderRadius: shape,
              border: Border.all(
                color: OsmiumColors.sessionGlassBorder,
                width: 0.8,
              ),
            ),
            child: child,
          ),
        ),
      ),
    );
  }
}

class _RailBody extends StatelessWidget {
  const _RailBody({
    super.key,
    required this.actions,
    required this.right,
    required this.onAction,
    required this.onFold,
  });

  final List<SessionRailAction> actions;
  final bool right;
  final ValueChanged<SessionRailAction> onAction;
  final VoidCallback onFold;

  @override
  Widget build(BuildContext context) {
    return _RailGlass(
      width: _SessionRailState._width,
      radius: 16,
      child: Padding(
        padding: const EdgeInsets.only(top: 6),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            for (final action in actions)
              _RailButton(action: action, onTap: () => onAction(action)),
            GestureDetector(
              behavior: HitTestBehavior.opaque,
              onTap: onFold,
              child: SizedBox(
                width: double.infinity,
                height: _SessionRailState._foldHeight,
                child: Icon(
                  right
                      ? Icons.keyboard_arrow_right_rounded
                      : Icons.keyboard_arrow_left_rounded,
                  size: 18,
                  color: Colors.white.withValues(alpha: 0.42),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _RailButton extends StatefulWidget {
  const _RailButton({required this.action, required this.onTap});

  final SessionRailAction action;
  final VoidCallback onTap;

  @override
  State<_RailButton> createState() => _RailButtonState();
}

class _RailButtonState extends State<_RailButton> {
  bool _pressed = false;

  void _press(bool down) {
    if (_pressed != down) setState(() => _pressed = down);
  }

  @override
  Widget build(BuildContext context) {
    final action = widget.action;
    final fg = action.active
        ? OsmiumColors.accentOnGlass
        : Colors.white.withValues(alpha: 0.9);
    return GestureDetector(
      behavior: HitTestBehavior.opaque,
      onTapDown: (_) => _press(true),
      onTapUp: (_) => _press(false),
      onTapCancel: () => _press(false),
      onTap: widget.onTap,
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 120),
        height: _SessionRailState._itemHeight,
        margin: const EdgeInsets.symmetric(horizontal: 5),
        decoration: BoxDecoration(
          color: _pressed
              ? Colors.white.withValues(alpha: 0.12)
              : action.active
              ? OsmiumColors.accentOnGlass.withValues(alpha: 0.14)
              : Colors.transparent,
          borderRadius: BorderRadius.circular(11),
        ),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(action.icon, size: 20, color: fg),
            const SizedBox(height: 3),
            Text(
              action.label,
              maxLines: 1,
              style: TextStyle(
                fontFamily: OsmiumTheme.fontFamily,
                fontSize: 10,
                fontWeight: FontWeight.w500,
                height: 1.2,
                color: action.active
                    ? OsmiumColors.accentOnGlass
                    : Colors.white.withValues(alpha: 0.72),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _FoldedTab extends StatelessWidget {
  const _FoldedTab({super.key, required this.right, required this.onTap});

  final bool right;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      behavior: HitTestBehavior.opaque,
      onTap: onTap,
      child: Padding(
        // A wider hit area than the sliver that shows.
        padding: EdgeInsets.only(left: right ? 14 : 0, right: right ? 0 : 14),
        child: Transform.translate(
          offset: Offset(right ? 4 : -4, 0),
          child: _RailGlass(
            width: 16,
            height: 60,
            radius: 8,
            child: Icon(
              right
                  ? Icons.keyboard_arrow_left_rounded
                  : Icons.keyboard_arrow_right_rounded,
              size: 16,
              color: Colors.white.withValues(alpha: 0.7),
            ),
          ),
        ),
      ),
    );
  }
}
