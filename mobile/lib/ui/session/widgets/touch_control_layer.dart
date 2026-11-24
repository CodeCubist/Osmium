import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/physics.dart';
import 'package:flutter/scheduler.dart';
import 'package:flutter_miuix/miuix.dart';

import '../../../core/models/host_status.dart';
import '../../../core/models/osmium_settings.dart';
import '../../../core/theme/osmium_motion.dart';
import '../../../core/theme/osmium_theme.dart';
import '../../../services/session/remote_session_controller.dart';
import 'session_signals.dart';

enum _TwoFingerMode { undecided, pinch, scroll }

class _TapRipple {
  _TapRipple({
    required this.position,
    required this.isRightClick,
    required this.startUs,
  });

  final Offset position;
  final bool isRightClick;

  /// Microseconds on [_TouchControlLayerState._rippleTicker] when the ripple
  /// started. Ages are measured against the ticker, never against wall time.
  final int startUs;
}

/// Lets the ripple painter repaint from a ticker without rebuilding widgets.
class _RippleRepaint extends ChangeNotifier {
  void tick() => notifyListeners();
}

/// Core remote video canvas & gesture engine.
/// - One finger: tap clicks; sliding pans the zoomed picture, or moves the
///   pointer when there is nothing to pan; holding arms a press that drags with
///   the left button once the finger moves, or right-clicks on release.
/// - [ControlMode.directTouch] acts where the finger is. [ControlMode.precisionMouse]
///   acts at a relative trackpad cursor that the zoomed view follows.
/// - Two fingers: pinch zooms (1x..4x), sliding scrolls, a tap right-clicks.
/// - Haptics only confirm what the eye cannot: the hold arming, the drag lock.
/// - On hosts that take Windows touch input, one finger in
///   [ControlMode.directTouch] is forwarded as a touchscreen contact and
///   Windows recognises taps, holds and swipes itself. Two fingers stay the
///   phone's: pinch zooms the picture, sliding scrolls the host, or moves the
///   picture once it is zoomed.
class TouchControlLayer extends StatefulWidget {
  const TouchControlLayer({
    super.key,
    required this.controller,
    required this.signals,
    required this.videoReveal,
    required this.controlMode,
    required this.sensitivity,
    required this.magnifierEnabled,
    required this.enableHaptics,
    required this.onUserInteraction,
    this.keyboardBar,
  });

  final RemoteSessionController controller;
  final SessionSignals signals;

  /// 0..1: fades and slightly scales the video in when the first frame lands.
  final Animation<double> videoReveal;
  final ControlMode controlMode;
  final double sensitivity;
  final bool magnifierEnabled;
  final bool enableHaptics;
  final VoidCallback onUserInteraction;

  /// Visible height of the keyboard bar above the system IME; with the IME
  /// it is what the picture lifts the host caret clear of.
  final ValueListenable<double>? keyboardBar;

  @override
  State<TouchControlLayer> createState() => TouchControlLayerState();
}

class TouchControlLayerState extends State<TouchControlLayer>
    with TickerProviderStateMixin {
  // Video zoom & pan transform
  double _zoom = 1.0;
  Offset _pan = Offset.zero;
  Size _lastViewport = Size.zero;

  // How far the picture is raised to keep the host caret above a keyboard.
  double _lift = 0.0;
  double _liftGoal = 0.0;
  double _imeInset = 0.0;
  late final AnimationController _liftAnim;

  // Virtual cursor normalized coordinates [0..1] for precisionMouse mode.
  // Driven as a notifier so pointer movement repaints the cursor layer only.
  final ValueNotifier<Offset> _cursorNorm = ValueNotifier(
    const Offset(0.5, 0.5),
  );
  bool _precisionDragging = false;
  bool _isFineMoving = false;
  Timer? _fineMoveFadeTimer;
  late final AnimationController _loupe;
  late final AnimationController _zoomSpring;
  late final Listenable _cursorRepaint;

  // InputMouseButton ids: 1 left, 2 middle, 3 right.
  static const int _leftButton = 1;
  static const int _rightButton = 3;

  static const Duration _holdDelay = Duration(milliseconds: 380);
  static const Duration _twoFingerTapWindow = Duration(milliseconds: 400);
  static const double _touchSlop = 8.0;
  // Two-finger jitter allowed before a tap becomes a pinch or a scroll.
  static const double _pinchSlop = 18.0;
  static const double _scrollSlop = 14.0;
  // Matches the Windows default double-click time.
  static const Duration _doubleTapWindow = Duration(milliseconds: 500);
  static const double _doubleTapSlop = 32.0;

  // The last left tap in direct touch, kept briefly so the next tap can land
  // on the exact same host pixel.
  Offset? _lastTapPos;
  Offset? _lastTapNorm;
  Timer? _doubleTapTimer;

  // Single-finger tracking
  final Map<int, Offset> _activePointers = {};
  Offset? _downPos;
  Offset? _lastSinglePos;
  bool _movedBeyondSlop = false;
  bool _armed = false;
  bool _dragging = false;
  // Set once a second finger joins; the finger left behind does nothing.
  bool _singleConsumed = false;
  Timer? _longPressTimer;

  // Native touch: the lone finger forwarded as a touchscreen contact and its
  // position on the host display. Whether the host takes touch is decided
  // when a gesture starts, so a status push cannot split one gesture in two.
  bool _nativeGesture = false;
  int? _touchPointer;
  Offset _touchDownAt = Offset.zero;
  Offset _touchNorm = Offset.zero;
  bool _touchDownSent = false;
  bool _touchTravelled = false;
  bool _touchMovePending = false;
  Timer? _touchDownTimer;
  Timer? _touchHeartbeat;
  // Long enough for the second finger of a pinch to land before the host
  // sees the first.
  static const Duration _touchDownDelay = Duration(milliseconds: 80);
  // The host cancels contacts it has not heard about for 1.5 s.
  static const Duration _touchHeartbeatInterval = Duration(milliseconds: 250);

  // Two-finger tracking
  _TwoFingerMode _twoMode = _TwoFingerMode.undecided;
  double _initialPinchDist = 0.0;
  double _initialZoom = 1.0;
  Offset _lastMultiFocal = Offset.zero;
  Offset _multiStartFocal = Offset.zero;
  Offset _multiTapPos = Offset.zero;
  bool _multiTapEligible = false;
  Timer? _multiTapTimer;
  double _scrollAccumY = 0.0;
  double _scrollAccumX = 0.0;

  // Subtle tap feedback ripples, animated by one ticker + repaint notifier.
  final List<_TapRipple> _ripples = [];
  late final _RippleRepaint _rippleRepaint;
  late final Ticker _rippleTicker;
  int _rippleClockUs = 0;
  static const int _rippleLifeUs = 340000;

  int _buildCount = 0;

  /// Test hooks: proves stats ticks don't rebuild the video surface and that
  /// the ripple ticker starts and clears on its own.
  @visibleForTesting
  int get debugBuildCount => _buildCount;

  @visibleForTesting
  int get activeRippleCount => _ripples.length;

  @visibleForTesting
  bool get debugRippleTicking => _rippleTicker.isTicking;

  @visibleForTesting
  double get debugZoom => _zoom;

  @visibleForTesting
  Offset get debugPan => _pan;

  @visibleForTesting
  double get debugLift => _lift;

  @override
  void initState() {
    super.initState();
    _rippleRepaint = _RippleRepaint();
    _rippleTicker = createTicker(_onRippleTick);
    _loupe = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 180),
      reverseDuration: const Duration(milliseconds: 260),
    );
    _zoomSpring = AnimationController.unbounded(vsync: this)
      ..addListener(_onZoomSpringTick);
    _liftAnim = AnimationController.unbounded(vsync: this)
      ..addListener(_onLiftTick);
    _cursorRepaint = Listenable.merge([_cursorNorm, _loupe]);
    widget.signals.textFocus.addListener(_updateLift);
    widget.keyboardBar?.addListener(_updateLift);
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final ime = MediaQuery.viewInsetsOf(context).bottom;
    if (ime != _imeInset) {
      _imeInset = ime;
      WidgetsBinding.instance.addPostFrameCallback((_) => _updateLift());
    }
  }

  @override
  void didUpdateWidget(covariant TouchControlLayer oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.signals != oldWidget.signals) {
      oldWidget.signals.textFocus.removeListener(_updateLift);
      widget.signals.textFocus.addListener(_updateLift);
    }
    if (widget.keyboardBar != oldWidget.keyboardBar) {
      oldWidget.keyboardBar?.removeListener(_updateLift);
      widget.keyboardBar?.addListener(_updateLift);
    }
    if (widget.magnifierEnabled != oldWidget.magnifierEnabled) {
      _setLoupe(
        widget.magnifierEnabled && _isFineMoving ||
            _precisionDragging && widget.magnifierEnabled,
      );
    }
  }

  @override
  void dispose() {
    if (_touchPointer != null) _touchLift(TouchPhase.cancel);
    _longPressTimer?.cancel();
    _multiTapTimer?.cancel();
    _doubleTapTimer?.cancel();
    _fineMoveFadeTimer?.cancel();
    _rippleTicker.dispose();
    _rippleRepaint.dispose();
    widget.signals.textFocus.removeListener(_updateLift);
    widget.keyboardBar?.removeListener(_updateLift);
    _loupe.dispose();
    _zoomSpring.dispose();
    _liftAnim.dispose();
    _cursorNorm.dispose();
    super.dispose();
  }

  /// Raises the picture just enough for the host caret to clear whatever
  /// keyboard covers the bottom of the screen, and lets it back down after.
  void _updateLift() {
    if (!mounted) return;
    final goal = _liftTarget();
    if ((goal - _liftGoal).abs() < 0.5) return;
    _liftGoal = goal;
    if (OsmiumMotion.reduced(context)) {
      _liftAnim.value = goal;
    } else {
      _liftAnim.animateTo(
        goal,
        duration: const Duration(milliseconds: 220),
        curve: Curves.easeOutCubic,
      );
    }
  }

  double _liftTarget() {
    final focus = widget.signals.textFocus.value;
    final caret = focus.caret;
    final cover = _imeInset + (widget.keyboardBar?.value ?? 0.0);
    if (!focus.active || caret == null || cover <= 0 || _lastViewport.isEmpty) {
      return 0.0;
    }
    final resting = _computeVideoRect(_lastViewport).translate(0, _lift);
    final caretBottom = resting.top + caret.bottom * resting.height;
    final clearance = _lastViewport.height - cover - 16;
    return (caretBottom - clearance).clamp(0.0, cover);
  }

  void _onLiftTick() {
    if (!mounted) return;
    setState(() => _lift = _liftAnim.value);
  }

  void _hapticMedium() {
    if (widget.enableHaptics) OsmiumHaptics.confirm();
  }

  void _hapticHeavy() {
    if (widget.enableHaptics) OsmiumHaptics.lock();
  }

  void _onRippleTick(Duration elapsed) {
    _rippleClockUs = elapsed.inMicroseconds;
    _ripples.removeWhere((r) => _rippleClockUs - r.startUs > _rippleLifeUs);
    _rippleRepaint.tick();
    if (_ripples.isEmpty) _rippleTicker.stop();
  }

  void _addRipple(Offset pos, {bool rightClick = false}) {
    if (!_rippleTicker.isTicking) {
      _rippleClockUs = 0;
      _rippleTicker.start();
    }
    _ripples.add(
      _TapRipple(
        position: pos,
        isRightClick: rightClick,
        startUs: _rippleClockUs,
      ),
    );
    _rippleRepaint.tick();
  }

  double _aspectFrom(Size video) => (video.width > 0 && video.height > 0)
      ? (video.width / video.height)
      : (16.0 / 9.0);

  /// Computes the rendered video rectangle inside the full widget viewport,
  /// taking video aspect ratio, zoom, and pan into account.
  Rect _computeVideoRect(Size viewport) {
    final stats = widget.controller.stats;
    final host = widget.controller.hostStatus;
    final videoW = (stats.videoWidth > 0 ? stats.videoWidth : host.streamWidth)
        .toDouble();
    final videoH =
        (stats.videoHeight > 0 ? stats.videoHeight : host.streamHeight)
            .toDouble();
    return _rectFor(viewport, _aspectFrom(Size(videoW, videoH)));
  }

  Rect _rectFor(Size viewport, double aspect) {
    final viewAspect = viewport.width / math.max(1.0, viewport.height);
    double baseW;
    double baseH;
    if (viewAspect > aspect) {
      baseH = viewport.height;
      baseW = baseH * aspect;
    } else {
      baseW = viewport.width;
      baseH = baseW / aspect;
    }

    final scaledW = baseW * _zoom;
    final scaledH = baseH * _zoom;
    final cx = viewport.width / 2 + _pan.dx;
    final cy = viewport.height / 2 + _pan.dy - _lift;
    return Rect.fromCenter(
      center: Offset(cx, cy),
      width: scaledW,
      height: scaledH,
    );
  }

  /// Keeps the picture's edges from pulling inside the viewport; an axis that
  /// still fits stays centred.
  Offset _clampPan(Offset rawPan, double zoom, Size viewport) {
    if (zoom <= 1.01) return Offset.zero;
    final base = _computeVideoRect(viewport).size / _zoom;
    final maxDx = math.max(0.0, (base.width * zoom - viewport.width) / 2);
    final maxDy = math.max(0.0, (base.height * zoom - viewport.height) / 2);
    return Offset(
      rawPan.dx.clamp(-maxDx, maxDx),
      rawPan.dy.clamp(-maxDy, maxDy),
    );
  }

  bool get _zoomed => _zoom > 1.01;

  /// Fades the precision loupe in and out; it never pops.
  void _setLoupe(bool want) {
    if (!mounted) return;
    final reduced = OsmiumMotion.reduced(context);
    if (!widget.magnifierEnabled) want = false;
    if (want) {
      if (_loupe.value < 1) {
        if (reduced) {
          _loupe.value = 1;
        } else {
          _loupe.forward();
        }
      }
    } else if (_loupe.value > 0) {
      if (reduced) {
        _loupe.value = 0;
      } else {
        _loupe.reverse();
      }
    }
  }

  /// Springs zoom back to 1 after a pinch released below it, and re-clamps a
  /// pan that no longer fits the final zoom.
  void _settleZoom(Size viewport) {
    final target = _zoom.clamp(1.0, 4.0);
    if ((_zoom - target).abs() < 0.001) {
      final clamped = _clampPan(_pan, _zoom, viewport);
      if (clamped != _pan) setState(() => _pan = clamped);
      return;
    }
    if (OsmiumMotion.reduced(context)) {
      setState(() {
        _zoom = target;
        _pan = _clampPan(_pan, target, viewport);
      });
    } else {
      _zoomSpring.animateWith(
        SpringSimulation(OsmiumMotion.standard, _zoom, target, 0),
      );
    }
  }

  void _onZoomSpringTick() {
    if (!mounted) return;
    final next = _zoomSpring.value.clamp(0.88, 4.0);
    setState(() {
      _zoom = next;
      _pan = _clampPan(_pan, next, _lastViewport);
    });
  }

  Offset _viewportToNorm(Offset localPos, Rect videoRect) {
    final nx = ((localPos.dx - videoRect.left) / math.max(1.0, videoRect.width))
        .clamp(0.0, 1.0);
    final ny = ((localPos.dy - videoRect.top) / math.max(1.0, videoRect.height))
        .clamp(0.0, 1.0);
    return Offset(nx, ny);
  }

  Offset _normToViewport(Offset norm, Rect videoRect) {
    return Offset(
      videoRect.left + norm.dx * videoRect.width,
      videoRect.top + norm.dy * videoRect.height,
    );
  }

  /// Moves the host pointer to [pos] on screen (direct touch semantics).
  Offset _pointAt(Offset pos, Size viewport) {
    final norm = _viewportToNorm(pos, _computeVideoRect(viewport));
    _sendPointer(norm);
    return norm;
  }

  void _sendPointer(Offset norm) {
    _cursorNorm.value = norm;
    widget.controller.sendAbsoluteMouse(norm.dx, norm.dy);
  }

  /// Moves the precision cursor by a finger step, slowed down for tiny steps so
  /// small Windows targets stay hittable.
  void _moveCursorBy(Offset step, Size viewport) {
    final videoRect = _computeVideoRect(viewport);
    final speed = step.distance;
    final accel = speed < 3.5
        ? 0.38
        : speed < 10.0
        ? 0.85
        : (1.0 + ((speed - 10.0) * 0.045)).clamp(1.0, 2.2);
    final gain = widget.sensitivity * accel;
    final current = _cursorNorm.value;
    final next = Offset(
      (current.dx + step.dx * gain / math.max(1.0, videoRect.width)).clamp(
        0.0,
        1.0,
      ),
      (current.dy + step.dy * gain / math.max(1.0, videoRect.height)).clamp(
        0.0,
        1.0,
      ),
    );
    // The cursor moves through a notifier, so dragging in precision mode
    // repaints the cursor layer only — never the whole video surface.
    _cursorNorm.value = next;
    _isFineMoving = true;
    _setLoupe(true);
    _fineMoveFadeTimer?.cancel();
    _fineMoveFadeTimer = Timer(const Duration(milliseconds: 550), () {
      _isFineMoving = false;
      if (mounted) _setLoupe(_precisionDragging || _dragging);
    });
    widget.controller.sendAbsoluteMouse(next.dx, next.dy);
    _keepCursorInView(viewport);
  }

  /// When zoomed, the picture follows the precision cursor near the edges.
  void _keepCursorInView(Size viewport) {
    if (!_zoomed) return;
    const margin = 56.0;
    final p = _cursorPosition(viewport);
    double dx = 0;
    double dy = 0;
    if (p.dx < margin) {
      dx = margin - p.dx;
    } else if (p.dx > viewport.width - margin) {
      dx = viewport.width - margin - p.dx;
    }
    if (p.dy < margin) {
      dy = margin - p.dy;
    } else if (p.dy > viewport.height - margin) {
      dy = viewport.height - margin - p.dy;
    }
    if (dx == 0 && dy == 0) return;
    final next = _clampPan(_pan + Offset(dx, dy), _zoom, viewport);
    if (next != _pan) setState(() => _pan = next);
  }

  void _onPointerDown(PointerDownEvent e, Size viewport) {
    widget.onUserInteraction();
    if (_activePointers.isEmpty) {
      _nativeGesture =
          widget.controlMode == ControlMode.directTouch &&
          widget.controller.hostStatus.touchInput;
      if (_nativeGesture) {
        _activePointers[e.pointer] = e.localPosition;
        _touchDown(e, viewport);
        return;
      }
    } else if (_touchPointer != null) {
      // A second finger makes the gesture the phone's; the host forgets the
      // first one without clicking.
      _touchLift(TouchPhase.cancel);
    }
    _activePointers[e.pointer] = e.localPosition;

    if (_activePointers.length == 2) {
      _cancelSingleFingerPending();
      _forgetTap();
      final pts = _activePointers.values.toList();
      _initialPinchDist = (pts[0] - pts[1]).distance;
      _initialZoom = _zoom;
      _lastMultiFocal = (pts[0] + pts[1]) / 2;
      _multiStartFocal = _lastMultiFocal;
      // Between the fingers, not under whichever happened to land first.
      _multiTapPos = _lastMultiFocal;
      _twoMode = _TwoFingerMode.undecided;
      _scrollAccumX = 0;
      _scrollAccumY = 0;
      _multiTapEligible = true;
      _multiTapTimer?.cancel();
      _multiTapTimer = Timer(_twoFingerTapWindow, () {
        _multiTapEligible = false;
      });
      return;
    }

    if (_activePointers.length > 2) {
      _multiTapEligible = false;
      return;
    }

    _downPos = e.localPosition;
    _lastSinglePos = e.localPosition;
    _movedBeyondSlop = false;
    _armed = false;
    _dragging = false;
    _singleConsumed = false;
    _longPressTimer?.cancel();
    _longPressTimer = Timer(_holdDelay, () => _arm(viewport));
  }

  /// The hold registered: moving now drags, lifting right-clicks.
  void _arm(Size viewport) {
    if (!mounted ||
        _movedBeyondSlop ||
        _singleConsumed ||
        _activePointers.length != 1 ||
        _downPos == null) {
      return;
    }
    _armed = true;
    _hapticMedium();
    if (widget.controlMode == ControlMode.directTouch) {
      _pointAt(_downPos!, viewport);
    }
  }

  void _onPointerMove(PointerMoveEvent e, Size viewport) {
    if (e.pointer == _touchPointer) {
      _activePointers[e.pointer] = e.localPosition;
      _touchMove(e, viewport);
      return;
    }
    if (!_activePointers.containsKey(e.pointer)) return;
    _activePointers[e.pointer] = e.localPosition;

    if (_activePointers.length == 2) {
      _onTwoFingerMove(viewport);
      return;
    }

    if (_activePointers.length != 1 ||
        _singleConsumed ||
        _downPos == null ||
        _lastSinglePos == null) {
      return;
    }

    if (!_movedBeyondSlop) {
      if ((e.localPosition - _downPos!).distance <= _touchSlop) return;
      _movedBeyondSlop = true;
      _longPressTimer?.cancel();
      _forgetTap();
      // The dock's drag toggle already holds the button down.
      if (_armed && !_precisionDragging) {
        _dragging = true;
        widget.controller.sendMouseButton(_leftButton, true);
      }
    }

    // Measured from the last applied position, so the slop is not lost.
    final step = e.localPosition - _lastSinglePos!;
    _lastSinglePos = e.localPosition;

    if (widget.controlMode == ControlMode.precisionMouse) {
      _moveCursorBy(step, viewport);
    } else if (_dragging || !_zoomed) {
      _pointAt(e.localPosition, viewport);
    } else {
      setState(() => _pan = _clampPan(_pan + step, _zoom, viewport));
    }
  }

  void _onTwoFingerMove(Size viewport) {
    final pts = _activePointers.values.toList();
    final dist = (pts[0] - pts[1]).distance;
    final focal = (pts[0] + pts[1]) / 2;
    final prevFocal = _lastMultiFocal;
    _lastMultiFocal = focal;

    if (_twoMode == _TwoFingerMode.undecided) {
      final slid = (focal - _multiStartFocal).distance > _scrollSlop;
      // One finger belongs to a touch host, so two move the zoomed picture.
      if ((dist - _initialPinchDist).abs() > _pinchSlop ||
          slid && _nativeGesture && _zoomed) {
        // Rebase so the zoom grows from here instead of jumping by the
        // threshold.
        _twoMode = _TwoFingerMode.pinch;
        _initialPinchDist = dist;
        _initialZoom = _zoom;
      } else if (slid) {
        _twoMode = _TwoFingerMode.scroll;
        if (widget.controlMode == ControlMode.directTouch) {
          // Windows scrolls whatever sits under the pointer.
          _pointAt(_multiStartFocal, viewport);
        }
      } else {
        return;
      }
      _multiTapEligible = false;
      return;
    }

    if (_twoMode == _TwoFingerMode.pinch) {
      final ratio = _initialPinchDist > 12 ? dist / _initialPinchDist : 1.0;
      // A little rubber-band below 1.0; the release springs it back.
      final nextZoom = (_initialZoom * ratio).clamp(0.88, 4.0);
      // Keep the content under the fingers anchored while it scales.
      final center = viewport.center(Offset.zero);
      final anchored =
          focal - center - (prevFocal - center - _pan) * (nextZoom / _zoom);
      setState(() {
        _pan = _clampPan(anchored, nextZoom, viewport);
        _zoom = nextZoom;
      });
      return;
    }

    final deltaFocal = focal - prevFocal;
    _scrollAccumY += deltaFocal.dy;
    _scrollAccumX += deltaFocal.dx;
    if (_scrollAccumY.abs() >= 14) {
      final steps = (_scrollAccumY / 14).truncate();
      _scrollAccumY -= steps * 14;
      widget.controller.sendMouseWheel(dy: steps * 120);
    }
    if (_scrollAccumX.abs() >= 18) {
      final steps = (_scrollAccumX / 18).truncate();
      _scrollAccumX -= steps * 18;
      widget.controller.sendMouseWheel(dx: steps * 120);
    }
  }

  void _onPointerUp(PointerUpEvent e, Size viewport) {
    if (e.pointer == _touchPointer) {
      _activePointers.remove(e.pointer);
      _touchLift(TouchPhase.up, at: e.localPosition, viewport: viewport);
      return;
    }
    if (!_activePointers.containsKey(e.pointer)) return;
    final count = _activePointers.length;
    _activePointers.remove(e.pointer);

    if (count >= 2) {
      if (count == 2) _endTwoFinger(viewport);
      if (_activePointers.length == 2) _rebaseTwoFinger();
      return;
    }

    _longPressTimer?.cancel();
    if (!_singleConsumed) {
      if (_dragging) {
        widget.controller.sendMouseButton(_leftButton, false);
        if (widget.controlMode == ControlMode.precisionMouse) {
          _setLoupe(_precisionDragging || _isFineMoving);
        }
      } else if (!_movedBeyondSlop) {
        _click(right: _armed, at: _downPos ?? e.localPosition, viewport);
      }
    }
    _resetSingle();
  }

  void _onPointerCancel(PointerCancelEvent e) {
    if (e.pointer == _touchPointer) {
      _activePointers.remove(e.pointer);
      _touchLift(TouchPhase.cancel);
      return;
    }
    _activePointers.remove(e.pointer);
    _cancelSingleFingerPending();
    if (_activePointers.isEmpty) _resetSingle();
  }

  void _touchDown(PointerDownEvent e, Size viewport) {
    _touchPointer = e.pointer;
    _touchDownAt = e.localPosition;
    _touchNorm = _viewportToNorm(e.localPosition, _computeVideoRect(viewport));
    _touchDownSent = false;
    _touchTravelled = false;
    _touchDownTimer = Timer(_touchDownDelay, _sendTouchDown);
  }

  /// The finger stayed alone long enough, travelled, or lifted: it is the
  /// host's now.
  void _sendTouchDown() {
    _touchDownTimer?.cancel();
    _touchDownTimer = null;
    if (_touchPointer == null || _touchDownSent) return;
    _touchDownSent = true;
    _sendTouch(TouchPhase.down);
    _addRipple(_touchDownAt);
    _touchHeartbeat = Timer.periodic(
      _touchHeartbeatInterval,
      (_) => _sendTouch(TouchPhase.move),
    );
  }

  void _touchMove(PointerMoveEvent e, Size viewport) {
    if (!_touchTravelled) {
      // One phone pixel spans several host pixels; jitter would read as a
      // drag and break press-and-hold.
      if ((e.localPosition - _touchDownAt).distance <= _touchSlop) return;
      _touchTravelled = true;
      _sendTouchDown();
    }
    _touchNorm = _viewportToNorm(e.localPosition, _computeVideoRect(viewport));
    // Moves delivered together go out as one frame.
    if (_touchMovePending) return;
    _touchMovePending = true;
    scheduleMicrotask(() {
      if (_touchMovePending && _touchPointer != null) {
        _sendTouch(TouchPhase.move);
      }
    });
  }

  void _touchLift(TouchPhase phase, {Offset? at, Size? viewport}) {
    if (phase == TouchPhase.up) {
      _sendTouchDown();
      if (_touchTravelled && at != null && viewport != null) {
        _touchNorm = _viewportToNorm(at, _computeVideoRect(viewport));
      }
    }
    if (_touchDownSent) _sendTouch(phase);
    _touchDownTimer?.cancel();
    _touchDownTimer = null;
    _touchHeartbeat?.cancel();
    _touchHeartbeat = null;
    _touchPointer = null;
    _touchDownSent = false;
    _touchMovePending = false;
  }

  void _sendTouch(TouchPhase phase) {
    _touchMovePending = false;
    widget.controller.sendTouch([
      TouchContact(id: 0, phase: phase, x: _touchNorm.dx, y: _touchNorm.dy),
    ]);
  }

  /// Left or right click: at the finger in direct touch, at the cursor in
  /// precision mode. The ripple is the feedback; no vibration.
  void _click(Size viewport, {required bool right, required Offset at}) {
    Offset ripplePos;
    if (widget.controlMode != ControlMode.directTouch) {
      widget.controller.sendAbsoluteMouse(
        _cursorNorm.value.dx,
        _cursorNorm.value.dy,
      );
      ripplePos = _cursorPosition(viewport);
    } else if (right) {
      _forgetTap();
      _pointAt(at, viewport);
      ripplePos = at;
    } else if (_lastTapPos != null &&
        (at - _lastTapPos!).distance <= _doubleTapSlop) {
      // Windows pairs clicks into a double click only within a few host
      // pixels, and one phone pixel spans about five, so a follow-up tap
      // reuses the first tap's exact spot. Triple clicks chain the same way.
      _sendPointer(_lastTapNorm!);
      ripplePos = _lastTapPos!;
      _rememberTap(ripplePos, _lastTapNorm!);
    } else {
      _rememberTap(at, _pointAt(at, viewport));
      ripplePos = at;
    }
    widget.controller.sendMouseClick(right ? _rightButton : _leftButton);
    _addRipple(ripplePos, rightClick: right);
  }

  void _rememberTap(Offset pos, Offset norm) {
    _lastTapPos = pos;
    _lastTapNorm = norm;
    _doubleTapTimer?.cancel();
    _doubleTapTimer = Timer(_doubleTapWindow, _forgetTap);
  }

  void _forgetTap() {
    _doubleTapTimer?.cancel();
    _lastTapPos = null;
    _lastTapNorm = null;
  }

  void _endTwoFinger(Size viewport) {
    _multiTapTimer?.cancel();
    if (_multiTapEligible) _click(viewport, right: true, at: _multiTapPos);
    _multiTapEligible = false;
    if (_twoMode == _TwoFingerMode.pinch) _settleZoom(viewport);
    _twoMode = _TwoFingerMode.undecided;
  }

  /// A third finger lifted: continue with the remaining two from where they
  /// are, without a jump.
  void _rebaseTwoFinger() {
    final pts = _activePointers.values.toList();
    _initialPinchDist = (pts[0] - pts[1]).distance;
    _initialZoom = _zoom;
    _lastMultiFocal = (pts[0] + pts[1]) / 2;
    _multiStartFocal = _lastMultiFocal;
  }

  void _cancelSingleFingerPending() {
    _longPressTimer?.cancel();
    if (_dragging) {
      _dragging = false;
      widget.controller.sendMouseButton(_leftButton, false);
    }
    _singleConsumed = true;
  }

  void _resetSingle() {
    _downPos = null;
    _lastSinglePos = null;
    _movedBeyondSlop = false;
    _armed = false;
    _dragging = false;
    _singleConsumed = false;
  }

  void _resetZoom() {
    _forgetTap();
    setState(() {
      _zoom = 1.0;
      _pan = Offset.zero;
    });
  }

  void _togglePrecisionDrag() {
    if (!_precisionDragging) _hapticHeavy();
    setState(() => _precisionDragging = !_precisionDragging);
    _setLoupe(_precisionDragging || _isFineMoving);
    widget.controller.sendMouseButton(_leftButton, _precisionDragging);
  }

  @override
  Widget build(BuildContext context) {
    _buildCount++;

    return LayoutBuilder(
      builder: (context, constraints) {
        final viewport = Size(constraints.maxWidth, constraints.maxHeight);
        _lastViewport = viewport;
        final zoomVisible = _zoom > 1.05;

        return Stack(
          fit: StackFit.expand,
          children: [
            // Full-bleed black canvas + hardware-decoded SurfaceProducer Texture
            Container(color: Colors.black),

            // Only this subtree follows the video; it repaints on its own.
            ValueListenableBuilder<SessionVideoInfo>(
              valueListenable: widget.signals.video,
              builder: (context, info, _) {
                final id = info.textureId;
                if (id == null || id < 0) return const SizedBox.shrink();
                final rect = _rectFor(viewport, _aspectFrom(info.size));
                return Positioned.fromRect(
                  rect: rect,
                  child: RepaintBoundary(
                    child: FadeTransition(
                      opacity: widget.videoReveal,
                      child: ScaleTransition(
                        scale: Tween<double>(begin: 1.03, end: 1).animate(
                          CurvedAnimation(
                            parent: widget.videoReveal,
                            curve: MiuixMotion.standardDecelerate,
                          ),
                        ),
                        child: Texture(
                          textureId: id,
                          filterQuality: FilterQuality.medium,
                        ),
                      ),
                    ),
                  ),
                );
              },
            ),

            // Gesture surface
            Listener(
              behavior: HitTestBehavior.opaque,
              onPointerDown: (e) => _onPointerDown(e, viewport),
              onPointerMove: (e) => _onPointerMove(e, viewport),
              onPointerUp: (e) => _onPointerUp(e, viewport),
              onPointerCancel: _onPointerCancel,
              child: const SizedBox.expand(),
            ),

            // Precision virtual cursor & optional fine-control crosshair loupe
            if (widget.controlMode == ControlMode.precisionMouse)
              IgnorePointer(
                child: RepaintBoundary(
                  child: CustomPaint(
                    size: viewport,
                    painter: _PrecisionCursorPainter(
                      norm: _cursorNorm,
                      rectOf: () => _computeVideoRect(viewport),
                      dragging: _precisionDragging,
                      loupe: _loupe,
                      repaint: _cursorRepaint,
                    ),
                  ),
                ),
              ),

            // Tap visual ripples: driven by a ticker + repaint notifier
            IgnorePointer(
              child: RepaintBoundary(
                child: CustomPaint(
                  size: viewport,
                  painter: _TapRipplePainter(
                    ripples: _ripples,
                    nowUs: () => _rippleClockUs,
                    repaint: _rippleRepaint,
                  ),
                ),
              ),
            ),

            // Zoom reset badge; fades in and out instead of popping
            Positioned(
              top: MediaQuery.paddingOf(context).top + 12,
              right: 16,
              child: IgnorePointer(
                ignoring: !zoomVisible,
                child: AnimatedOpacity(
                  opacity: zoomVisible ? 1 : 0,
                  duration: OsmiumMotion.durationOr(
                    context,
                    OsmiumMotion.quick,
                  ),
                  curve: MiuixMotion.standardDecelerate,
                  child: AnimatedScale(
                    scale: zoomVisible ? 1 : 0.9,
                    duration: OsmiumMotion.durationOr(
                      context,
                      OsmiumMotion.quick,
                    ),
                    curve: MiuixMotion.standardDecelerate,
                    child: GestureDetector(
                      onTap: _resetZoom,
                      child: ClipRRect(
                        borderRadius: BorderRadius.circular(7),
                        child: BackdropFilter(
                          filter: OsmiumGlass.filter(14),
                          child: Container(
                            padding: const EdgeInsets.symmetric(
                              horizontal: 10,
                              vertical: 5,
                            ),
                            decoration: BoxDecoration(
                              color: OsmiumColors.sessionGlassFill,
                              borderRadius: BorderRadius.circular(7),
                              border: Border.all(
                                color: OsmiumColors.sessionGlassBorder,
                                width: 0.75,
                              ),
                            ),
                            child: Row(
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                const Icon(
                                  Icons.zoom_out_map_rounded,
                                  size: 13.5,
                                  color: Colors.white,
                                ),
                                const SizedBox(width: 5),
                                Text(
                                  '${_zoom.toStringAsFixed(1)}x',
                                  style: OsmiumTheme.monoStyle(
                                    fontSize: 11.5,
                                    fontWeight: FontWeight.w600,
                                    color: Colors.white,
                                  ),
                                ),
                              ],
                            ),
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            ),

            // Precision mouse quick action dock (Left / Drag / Right / Scroll)
            if (widget.controlMode == ControlMode.precisionMouse)
              Positioned(
                bottom: MediaQuery.paddingOf(context).bottom + 14,
                left: 0,
                right: 0,
                child: Center(
                  child: _PrecisionMouseDock(
                    dragging: _precisionDragging,
                    onLeftClick: () =>
                        _click(viewport, right: false, at: Offset.zero),
                    onToggleDrag: _togglePrecisionDrag,
                    onRightClick: () =>
                        _click(viewport, right: true, at: Offset.zero),
                    onWheelUp: () => widget.controller.sendMouseWheel(dy: 120),
                    onWheelDown: () =>
                        widget.controller.sendMouseWheel(dy: -120),
                  ),
                ),
              ),
          ],
        );
      },
    );
  }

  Offset _cursorPosition(Size viewport) =>
      _normToViewport(_cursorNorm.value, _computeVideoRect(viewport));
}

class _PrecisionMouseDock extends StatelessWidget {
  const _PrecisionMouseDock({
    required this.dragging,
    required this.onLeftClick,
    required this.onToggleDrag,
    required this.onRightClick,
    required this.onWheelUp,
    required this.onWheelDown,
  });

  final bool dragging;
  final VoidCallback onLeftClick;
  final VoidCallback onToggleDrag;
  final VoidCallback onRightClick;
  final VoidCallback onWheelUp;
  final VoidCallback onWheelDown;

  @override
  Widget build(BuildContext context) {
    return DecoratedBox(
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(9),
        boxShadow: const [
          BoxShadow(
            color: Color(0x4D000000),
            blurRadius: 16,
            offset: Offset(0, 6),
          ),
        ],
      ),
      child: ClipRRect(
        borderRadius: BorderRadius.circular(9),
        child: BackdropFilter(
          filter: OsmiumGlass.filter(18),
          child: Container(
            height: 38,
            padding: const EdgeInsets.symmetric(horizontal: 5),
            decoration: BoxDecoration(
              color: const Color(0x750E121B),
              borderRadius: BorderRadius.circular(9),
              border: Border.all(
                color: OsmiumColors.sessionGlassBorder,
                width: 0.8,
              ),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                _DockBtn(label: '左键', onTap: onLeftClick),
                _DockDivider(),
                _DockBtn(
                  label: dragging ? '拖拽中' : '拖拽',
                  active: dragging,
                  onTap: onToggleDrag,
                ),
                _DockDivider(),
                _DockBtn(label: '右键', onTap: onRightClick),
                _DockDivider(),
                _DockIconBtn(
                  icon: Icons.keyboard_arrow_up_rounded,
                  onTap: onWheelUp,
                ),
                _DockIconBtn(
                  icon: Icons.keyboard_arrow_down_rounded,
                  onTap: onWheelDown,
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _DockBtn extends StatelessWidget {
  const _DockBtn({
    required this.label,
    required this.onTap,
    this.active = false,
  });

  final String label;
  final VoidCallback onTap;
  final bool active;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      behavior: HitTestBehavior.opaque,
      onTap: onTap,
      child: Container(
        height: 28,
        padding: const EdgeInsets.symmetric(horizontal: 12),
        alignment: Alignment.center,
        decoration: BoxDecoration(
          color: active
              ? OsmiumColors.accentOnGlass.withValues(alpha: 0.22)
              : Colors.transparent,
          borderRadius: BorderRadius.circular(6),
        ),
        child: Text(
          label,
          style: TextStyle(
            fontFamily: OsmiumTheme.fontFamily,
            fontSize: 12.5,
            fontWeight: FontWeight.w600,
            color: active
                ? OsmiumColors.accentOnGlass
                : Colors.white.withValues(alpha: 0.92),
          ),
        ),
      ),
    );
  }
}

class _DockIconBtn extends StatelessWidget {
  const _DockIconBtn({required this.icon, required this.onTap});

  final IconData icon;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      behavior: HitTestBehavior.opaque,
      onTap: onTap,
      child: SizedBox(
        width: 32,
        height: 32,
        child: Icon(
          icon,
          size: 19,
          color: Colors.white.withValues(alpha: 0.88),
        ),
      ),
    );
  }
}

class _DockDivider extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    return Container(
      width: 0.8,
      height: 16,
      margin: const EdgeInsets.symmetric(horizontal: 2),
      color: Colors.white.withValues(alpha: 0.14),
    );
  }
}

class _PrecisionCursorPainter extends CustomPainter {
  _PrecisionCursorPainter({
    required this.norm,
    required this.rectOf,
    required this.dragging,
    required this.loupe,
    super.repaint,
  });

  final ValueListenable<Offset> norm;
  final Rect Function() rectOf;
  final bool dragging;
  final Animation<double> loupe;

  @override
  void paint(Canvas canvas, Size size) {
    final rect = rectOf();
    final position = Offset(
      rect.left + norm.value.dx * rect.width,
      rect.top + norm.value.dy * rect.height,
    );
    final draggingNow = dragging;

    // Precision crosshair loupe: fades and scales in with the controller.
    final loupeT = loupe.value;
    if (loupeT > 0.001) {
      final scale = 0.86 + 0.14 * loupeT;
      final alpha = loupeT;
      final ringPaint = Paint()
        ..color = (draggingNow ? OsmiumColors.accentOnGlass : Colors.white)
            .withValues(alpha: 0.55 * alpha)
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.4;
      canvas.drawCircle(position, 22 * scale, ringPaint);

      final hairPaint = Paint()
        ..color = OsmiumColors.accentOnGlass.withValues(alpha: 0.72 * alpha)
        ..strokeWidth = 1.0;
      final inner = 4 * scale;
      final outer = 11 * scale;
      canvas.drawLine(
        Offset(position.dx - outer, position.dy),
        Offset(position.dx - inner, position.dy),
        hairPaint,
      );
      canvas.drawLine(
        Offset(position.dx + inner, position.dy),
        Offset(position.dx + outer, position.dy),
        hairPaint,
      );
      canvas.drawLine(
        Offset(position.dx, position.dy - outer),
        Offset(position.dx, position.dy - inner),
        hairPaint,
      );
      canvas.drawLine(
        Offset(position.dx, position.dy + inner),
        Offset(position.dx, position.dy + outer),
        hairPaint,
      );
    }

    // Crisp classic Windows-style high-contrast cursor arrow
    final path = Path()
      ..moveTo(position.dx, position.dy)
      ..lineTo(position.dx, position.dy + 19)
      ..lineTo(position.dx + 4.8, position.dy + 14.5)
      ..lineTo(position.dx + 8.6, position.dy + 22)
      ..lineTo(position.dx + 11.4, position.dy + 20.6)
      ..lineTo(position.dx + 7.6, position.dy + 13.2)
      ..lineTo(position.dx + 13.8, position.dy + 13.2)
      ..close();

    canvas.drawPath(
      path,
      Paint()
        ..color = Colors.black.withValues(alpha: 0.85)
        ..style = PaintingStyle.stroke
        ..strokeWidth = 2.4
        ..strokeJoin = StrokeJoin.round,
    );
    canvas.drawPath(
      path,
      Paint()
        ..color = draggingNow ? OsmiumColors.accentOnGlass : Colors.white
        ..style = PaintingStyle.fill,
    );
  }

  @override
  bool shouldRepaint(covariant _PrecisionCursorPainter oldDelegate) =>
      oldDelegate.norm != norm ||
      oldDelegate.rectOf != rectOf ||
      oldDelegate.dragging != dragging ||
      oldDelegate.loupe != loupe;
}

class _TapRipplePainter extends CustomPainter {
  _TapRipplePainter({
    required this.ripples,
    required this.nowUs,
    super.repaint,
  });

  /// Repaints are driven by the ticker's [CustomPaint.repaint] listenable; the
  /// painter only samples the clock when it is asked to paint.
  final List<_TapRipple> ripples;

  /// Microseconds since the ripple ticker started.
  final int Function() nowUs;

  @override
  void paint(Canvas canvas, Size size) {
    final now = nowUs();
    for (final r in ripples) {
      final elapsedUs = (now - r.startUs).clamp(0, 340000);
      final t = elapsedUs / 340000.0;
      // Ease-out expansion, ease-in-out fade: grows fast, dissolves gently.
      final grow = 1 - math.pow(1 - t, 3.0).toDouble();
      final radius = 8.0 + grow * 26.0;
      final alpha = (1.0 - t) * (1.0 - t) * 0.7;
      final color = r.isRightClick
          ? const Color(0xFFFBBF24)
          : OsmiumColors.accentOnGlass;
      canvas.drawCircle(
        r.position,
        radius,
        Paint()
          ..color = color.withValues(alpha: alpha)
          ..style = PaintingStyle.stroke
          ..strokeWidth = 2.0,
      );
    }
  }

  @override
  bool shouldRepaint(covariant _TapRipplePainter oldDelegate) =>
      oldDelegate.ripples != ripples || oldDelegate.nowUs != nowUs;
}
