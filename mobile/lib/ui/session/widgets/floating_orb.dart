import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/physics.dart';

import '../../../core/models/osmium_settings.dart';
import '../../../core/theme/osmium_motion.dart';
import '../../../core/theme/osmium_theme.dart';

/// Inertia edge-snapping translucent floating orb (UU Remote + HyperOS Folme spring).
/// - Tap: opens the 4-section glass control panel.
/// - Long press: quick-toggles Direct Touch <-> Precision Mouse mode.
class FloatingOrb extends StatefulWidget {
  const FloatingOrb({
    super.key,
    required this.controlMode,
    required this.onOpenPanel,
    required this.onToggleControlMode,
    this.visible = true,
  });

  final ControlMode controlMode;

  /// False while the control panel is open: the orb shrinks into the panel's
  /// origin instead of disappearing on a single frame.
  final bool visible;
  final VoidCallback onOpenPanel;
  final VoidCallback onToggleControlMode;

  @override
  State<FloatingOrb> createState() => _FloatingOrbState();
}

class _FloatingOrbState extends State<FloatingOrb>
    with TickerProviderStateMixin {
  static const double _size = 46.0;
  static const double _margin = 10.0;

  // Where the orb rests: an edge plus a fraction of the usable height, so a
  // rotation keeps it in the same visual spot. Shared across sessions.
  static bool _restRight = true;
  static double _restFraction = 0.5;

  /// Absolute position while a finger or the snap spring owns the orb.
  Offset? _live;
  Size? _lastScreen;
  bool _dragging = false;
  bool _idle = false;
  Timer? _idleTimer;

  late final AnimationController _snapX;
  late final AnimationController _snapY;
  late final AnimationController _reveal;

  static final _folmeSpring = SpringDescription.withDampingRatio(
    mass: 1.0,
    stiffness: 380.0,
    ratio: 0.85,
  );

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
  void didUpdateWidget(covariant FloatingOrb oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.visible == oldWidget.visible) return;
    if (widget.visible) {
      if (OsmiumMotion.reduced(context)) {
        _reveal.value = 1;
      } else {
        _reveal.forward();
      }
    } else {
      if (OsmiumMotion.reduced(context)) {
        _reveal.value = 0;
      } else {
        _reveal.reverse();
      }
    }
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
      if (mounted && !_dragging) {
        setState(() => _idle = true);
      }
    });
  }

  double _restX(Size screen, EdgeInsets pad, bool right) =>
      right ? screen.width - pad.right - _size - _margin : pad.left + _margin;

  (double, double) _restYRange(Size screen, EdgeInsets pad) {
    final minY = pad.top + 24;
    final maxY = screen.height - pad.bottom - _size - 24;
    return (minY, maxY < minY ? minY : maxY);
  }

  Offset _restPosition(Size screen, EdgeInsets pad) {
    final (minY, maxY) = _restYRange(screen, pad);
    return Offset(
      _restX(screen, pad, _restRight),
      minY + (maxY - minY) * _restFraction,
    );
  }

  void _snapToEdge(Size screen, EdgeInsets pad, Offset velocity) {
    final from = _live!;
    final (minY, maxY) = _restYRange(screen, pad);
    _restRight = (from.dx + _size / 2) >= screen.width / 2;
    final targetX = _restX(screen, pad, _restRight);
    final targetY = (from.dy + velocity.dy * 0.08).clamp(minY, maxY);
    _restFraction = maxY > minY ? (targetY - minY) / (maxY - minY) : 0.5;

    if (OsmiumMotion.reduced(context)) {
      setState(() => _live = null);
      return;
    }
    _snapX.animateWith(
      SpringSimulation(_folmeSpring, from.dx, targetX, velocity.dx),
    );
    _snapY.animateWith(
      SpringSimulation(_folmeSpring, from.dy, targetY, velocity.dy),
    );
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
    final pos = _live ?? _restPosition(screen, pad);

    final isPrecision = widget.controlMode == ControlMode.precisionMouse;
    final isLandscape = screen.width > screen.height;
    final origin = isLandscape ? Alignment.centerRight : Alignment.bottomRight;

    return Positioned(
      left: pos.dx,
      top: pos.dy,
      child: RepaintBoundary(
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
                  scale: 0.55 + 0.45 * t,
                  alignment: origin,
                  child: child,
                ),
              ),
            );
          },
          child: GestureDetector(
            onTap: () {
              _scheduleIdle();
              widget.onOpenPanel();
            },
            onLongPress: () {
              _scheduleIdle();
              OsmiumHaptics.selection();
              widget.onToggleControlMode();
            },
            onPanStart: (_) {
              _snapX.stop();
              _snapY.stop();
              _dragging = true;
              _live = pos;
              _scheduleIdle();
            },
            onPanUpdate: (d) {
              final from = _live ?? pos;
              setState(() {
                _live = Offset(
                  (from.dx + d.delta.dx).clamp(0.0, screen.width - _size),
                  (from.dy + d.delta.dy).clamp(
                    pad.top + 8,
                    screen.height - pad.bottom - _size - 8,
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
              opacity: _dragging ? 0.98 : (_idle ? 0.42 : 0.88),
              child: AnimatedScale(
                duration: const Duration(milliseconds: 160),
                scale: _dragging ? 1.08 : 1.0,
                child: Container(
                  width: _size,
                  height: _size,
                  decoration: BoxDecoration(
                    color: const Color(0xD9141923),
                    shape: BoxShape.circle,
                    border: Border.all(
                      color: isPrecision
                          ? OsmiumColors.accentOnGlass.withValues(alpha: 0.85)
                          : Colors.white.withValues(alpha: 0.24),
                      width: 1.4,
                    ),
                    boxShadow: const [
                      BoxShadow(
                        color: Color(0x66000000),
                        blurRadius: 14,
                        offset: Offset(0, 4),
                      ),
                    ],
                  ),
                  child: Icon(
                    isPrecision
                        ? Icons.near_me_rounded
                        : Icons.touch_app_rounded,
                    size: 21,
                    color: isPrecision
                        ? OsmiumColors.accentOnGlass
                        : Colors.white,
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
