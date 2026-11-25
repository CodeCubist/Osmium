import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_miuix/miuix.dart';

import '../../../core/constants/osmium_constants.dart';
import '../../../core/models/host_status.dart';
import '../../../core/models/osmium_settings.dart';
import '../../../core/theme/osmium_motion.dart';
import '../../../core/theme/osmium_theme.dart';
import '../../../services/session/remote_session_controller.dart';
import 'connect_transition_overlay.dart';
import 'control_panel_sheet.dart';
import 'osd_capsule.dart';
import 'session_rail.dart';
import 'session_signals.dart';
import 'touch_control_layer.dart';
import 'virtual_keyboard_overlay.dart';

/// The remote session surface.
///
/// Everything here listens to the one [SessionSignals] value it paints, so a
/// 300 ms stats tick repaints the OSD numbers and the panel's latency pill —
/// never the page, the video texture or the touch layer.
class SessionView extends StatefulWidget {
  const SessionView({
    super.key,
    required this.controller,
    required this.signals,
    required this.settings,
    required this.panelOpen,
    required this.keyboardMode,
    required this.onOpenPanel,
    required this.onClosePanel,
    required this.onToggleControlMode,
    required this.onKeyboardModeChanged,
    required this.onSettingsChanged,
    required this.onDisconnect,
    required this.onRetry,
  });

  final RemoteSessionController controller;
  final SessionSignals signals;
  final OsmiumSettings settings;
  final bool panelOpen;
  final KeyboardOverlayMode keyboardMode;
  final VoidCallback onOpenPanel;
  final VoidCallback onClosePanel;
  final VoidCallback onToggleControlMode;
  final ValueChanged<KeyboardOverlayMode> onKeyboardModeChanged;
  final ValueChanged<OsmiumSettings> onSettingsChanged;
  final VoidCallback onDisconnect;
  final VoidCallback onRetry;

  @override
  State<SessionView> createState() => _SessionViewState();
}

class _SessionViewState extends State<SessionView>
    with SingleTickerProviderStateMixin {
  static bool _savedRailFolded = false;

  late final AnimationController _videoReveal;
  final ValueNotifier<double> _keyboardBar = ValueNotifier(0);
  bool _railFolded = _savedRailFolded;

  // The last host focus the IME came up for, and whether the IME is up only
  // because of it (so focus leaving may take it down again).
  int? _raisedForSerial;
  bool _imeRaisedByFocus = false;
  // Focus hopping between two fields passes through nothing for a poll or
  // two; the IME waits that out instead of flickering.
  Timer? _lowerTimer;

  @override
  void initState() {
    super.initState();
    _videoReveal = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 420),
      value: widget.signals.firstFrame.value ? 1 : 0,
    );
    widget.signals.phase.addListener(_onPhaseChanged);
    widget.signals.firstFrame.addListener(_onFirstFrame);
    widget.signals.textFocus.addListener(_onTextFocus);
    _onFirstFrame();
  }

  @override
  void didUpdateWidget(covariant SessionView oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.panelOpen && !oldWidget.panelOpen && _railFolded) {
      _railFolded = false;
      _savedRailFolded = false;
    }
  }

  @override
  void dispose() {
    widget.signals.phase.removeListener(_onPhaseChanged);
    widget.signals.firstFrame.removeListener(_onFirstFrame);
    widget.signals.textFocus.removeListener(_onTextFocus);
    _lowerTimer?.cancel();
    _videoReveal.dispose();
    _keyboardBar.dispose();
    super.dispose();
  }

  void _onPhaseChanged() {
    if (mounted) setState(() {});
  }

  void _setRailFolded(bool folded) {
    if (folded && widget.panelOpen) widget.onClosePanel();
    if (_railFolded == folded) return;
    setState(() {
      _railFolded = folded;
      _savedRailFolded = folded;
    });
  }

  void _onFirstFrame() {
    if (!mounted || !widget.signals.firstFrame.value) return;
    if (_videoReveal.value >= 1) return;
    if (OsmiumMotion.reduced(context)) {
      _videoReveal.value = 1;
    } else {
      _videoReveal.forward();
    }
  }

  /// A text field taking focus on the host brings up the phone IME; focus
  /// leaving takes it down again, unless the user has since taken over.
  void _onTextFocus() {
    if (!mounted) return;
    final focus = widget.signals.textFocus.value;
    if (focus.active) {
      _lowerTimer?.cancel();
      if (focus.serial == _raisedForSerial) return;
      _raisedForSerial = focus.serial;
      if (widget.keyboardMode != KeyboardOverlayMode.none ||
          widget.panelOpen ||
          widget.signals.phase.value != SessionPhase.streaming) {
        return;
      }
      _imeRaisedByFocus = true;
      widget.onKeyboardModeChanged(KeyboardOverlayMode.systemIme);
    } else if (_imeRaisedByFocus) {
      _lowerTimer?.cancel();
      _lowerTimer = Timer(const Duration(milliseconds: 400), () {
        if (!mounted || !_imeRaisedByFocus) return;
        _imeRaisedByFocus = false;
        if (widget.keyboardMode == KeyboardOverlayMode.systemIme) {
          widget.onKeyboardModeChanged(KeyboardOverlayMode.none);
        }
      });
    }
  }

  void _setKeyboardMode(KeyboardOverlayMode mode) {
    _lowerTimer?.cancel();
    _imeRaisedByFocus = false;
    if (mode != KeyboardOverlayMode.none && widget.panelOpen) {
      widget.onClosePanel();
    }
    widget.onKeyboardModeChanged(mode);
  }

  void _cycleOsdLevel() {
    final next = switch (widget.settings.osdLevel) {
      OsdLevel.compact => OsdLevel.standard,
      OsdLevel.standard => OsdLevel.detailed,
      OsdLevel.detailed || OsdLevel.off => OsdLevel.compact,
    };
    OsmiumHaptics.selection();
    widget.onSettingsChanged(widget.settings.copyWith(osdLevel: next));
  }

  @override
  Widget build(BuildContext context) {
    final settings = widget.settings;
    final size = MediaQuery.sizeOf(context);
    final pad = MediaQuery.paddingOf(context);
    final isLandscape = size.width > size.height;
    final phase = widget.signals.phase.value;
    final railVisible =
        phase == SessionPhase.streaming &&
        widget.keyboardMode == KeyboardOverlayMode.none;
    final railExpanded = railVisible && !_railFolded;
    final railRightInset = railExpanded && isLandscape
        ? SessionRail.landscapeWidth
        : 0.0;
    final railBottomInset = railExpanded && !isLandscape
        ? SessionRail.portraitHeight
        : 0.0;
    final panelRailInset = railExpanded
        ? (isLandscape
              ? SessionRail.landscapeWidth + pad.right
              : SessionRail.portraitHeight + pad.bottom)
        : 0.0;

    // The session glass is always dark; Miuix switches, sliders and glass
    // rows must draw for it whatever the app theme is.
    return MiuixTheme(
      data: MiuixTheme.of(
        context,
      ).copyWith(colors: OsmiumTheme.darkColors(), brightness: Brightness.dark),
      child: Stack(
        fit: StackFit.expand,
        children: [
          // Full-bleed video texture & direct/precision touch gesture surface
          TouchControlLayer(
            controller: widget.controller,
            signals: widget.signals,
            videoReveal: _videoReveal,
            controlMode: settings.controlMode,
            sensitivity: settings.cursorSensitivity,
            magnifierEnabled: settings.precisionMagnifierEnabled,
            enableHaptics: settings.enableHaptics,
            onUserInteraction: () {},
            keyboardBar: _keyboardBar,
            rightInset: railRightInset,
            bottomInset: railBottomInset,
          ),

          // Draggable OSD telemetry capsule, avoiding the docked rail
          _DraggableOsd(
            keyboardBar: _keyboardBar,
            rightInset: railRightInset,
            // Clear the docked bottom rail (portrait) and precision mouse dock.
            bottomInset:
                railBottomInset +
                (settings.controlMode == ControlMode.precisionMouse ? 52 : 0),
            onTap: _cycleOsdLevel,
            child: RepaintBoundary(
              child: _OsdHost(
                signals: widget.signals,
                level: settings.osdLevel,
              ),
            ),
          ),

          // Frosted control panel docked flush beside/above the primary rail
          RepaintBoundary(
            child: ControlPanelSheet(
              controller: widget.controller,
              signals: widget.signals,
              settings: settings,
              railInset: panelRailInset,
              visible: widget.panelOpen,
              onSettingsChanged: widget.onSettingsChanged,
              onOpenKeyboard: _setKeyboardMode,
              onClose: widget.onClosePanel,
              onDisconnect: widget.onDisconnect,
            ),
          ),

          // Docked primary action rail (right in landscape, bottom in portrait)
          SessionRail(
            visible: railVisible,
            folded: _railFolded,
            onFoldChanged: _setRailFolded,
            actions: [
              SessionRailAction(
                icon: Icons.tune_rounded,
                label: '操作',
                active: widget.panelOpen,
                onTap: widget.panelOpen
                    ? widget.onClosePanel
                    : widget.onOpenPanel,
              ),
              SessionRailAction(
                icon: Icons.keyboard_alt_outlined,
                label: '键盘',
                onTap: () => _setKeyboardMode(KeyboardOverlayMode.systemIme),
              ),
              SessionRailAction(
                icon: Icons.near_me_rounded,
                label: '鼠标',
                active: settings.controlMode == ControlMode.precisionMouse,
                onTap: widget.onToggleControlMode,
              ),
              SessionRailAction(
                icon: Icons.desktop_windows_outlined,
                label: '桌面',
                onTap: () => widget.controller.sendCombo([
                  WinKeys.lWin,
                  WinKeys.byLabel['D']!,
                ]),
              ),
              SessionRailAction(
                icon: Icons.grid_view_rounded,
                label: '多任务',
                onTap: () => widget.controller.sendCombo([
                  WinKeys.lWin,
                  WinKeys.tab,
                ]),
              ),
            ],
          ),

          // Keyboard overlay (sticky modifiers + system IME / full PC keyboard)
          VirtualKeyboardOverlay(
            mode: widget.keyboardMode,
            opacity: settings.keyboardOpacity,
            controller: widget.controller,
            onModeChanged: _setKeyboardMode,
            visibleHeight: _keyboardBar,
          ),

          // Staged connection transition & error recovery overlay
          RepaintBoundary(
            child: ConnectTransitionOverlay(
              signals: widget.signals,
              onCancel: widget.onDisconnect,
              onRetry: widget.onRetry,
            ),
          ),

          // Floating status toast pill
          Positioned(
            top: pad.top + 54,
            left: 0,
            right: 0,
            child: RepaintBoundary(child: _ToastHost(signals: widget.signals)),
          ),
        ],
      ),
    );
  }
}

/// Lets the OSD be dragged anywhere inside the safe area. The spot is kept as
/// an [Alignment], so it stays put across rotations and a capsule that grows
/// stays pinned to the edge it sits on. While a keyboard is up the capsule
/// rides above it, then settles back.
class _DraggableOsd extends StatefulWidget {
  const _DraggableOsd({
    required this.keyboardBar,
    required this.rightInset,
    required this.bottomInset,
    required this.onTap,
    required this.child,
  });

  /// Visible height of the keyboard bar above the system IME.
  final ValueListenable<double> keyboardBar;
  final double rightInset;
  final double bottomInset;
  final VoidCallback onTap;
  final Widget child;

  @override
  State<_DraggableOsd> createState() => _DraggableOsdState();
}

class _DraggableOsdState extends State<_DraggableOsd> {
  // Shared across sessions, like the orb's resting spot.
  static Alignment _alignment = Alignment.bottomRight;
  final GlobalKey _capsuleKey = GlobalKey();

  void _drag(DragUpdateDetails d, BoxConstraints area, double covered) {
    final box = _capsuleKey.currentContext?.findRenderObject() as RenderBox?;
    final size = box?.size ?? Size.zero;
    final freeW = area.maxWidth - size.width;
    final freeH = area.maxHeight - size.height;
    // Never park it under the keyboard, or dragging back up hits a dead zone.
    final maxY = freeH > 0 ? (1 - 2 * covered / freeH).clamp(-1.0, 1.0) : 1.0;
    setState(() {
      _alignment = Alignment(
        freeW > 0
            ? (_alignment.x + d.delta.dx * 2 / freeW).clamp(-1.0, 1.0)
            : _alignment.x,
        freeH > 0
            ? (_alignment.y + d.delta.dy * 2 / freeH).clamp(-1.0, maxY)
            : _alignment.y,
      );
    });
  }

  @override
  Widget build(BuildContext context) {
    final pad = MediaQuery.paddingOf(context);
    final ime = MediaQuery.viewInsetsOf(context).bottom;
    final right = pad.right + 14 + widget.rightInset;
    final bottom = pad.bottom + 14 + widget.bottomInset;
    return Positioned.fill(
      child: AnimatedPadding(
        duration: OsmiumMotion.durationOr(context, OsmiumMotion.small),
        curve: Curves.easeOutCubic,
        padding: EdgeInsets.fromLTRB(pad.left + 14, pad.top + 10, right, bottom),
        child: ValueListenableBuilder<double>(
          valueListenable: widget.keyboardBar,
          builder: (context, bar, capsule) {
            final keyboard = ime + bar;
            // How far the keyboard (plus a 10dp gap) reaches into the area.
            final covered = keyboard > 0
                ? math.max(0.0, keyboard + 10 - bottom)
                : 0.0;
            return LayoutBuilder(
              builder: (context, area) => CustomSingleChildLayout(
                delegate: _OsdLayout(_alignment, covered),
                child: GestureDetector(
                  behavior: HitTestBehavior.opaque,
                  onTap: widget.onTap,
                  onPanUpdate: (d) => _drag(d, area, covered),
                  child: capsule,
                ),
              ),
            );
          },
          child: KeyedSubtree(key: _capsuleKey, child: widget.child),
        ),
      ),
    );
  }
}

/// Places the capsule at its [alignment], lifted clear of the bottom
/// [covered] strip.
class _OsdLayout extends SingleChildLayoutDelegate {
  const _OsdLayout(this.alignment, this.covered);

  final Alignment alignment;
  final double covered;

  @override
  BoxConstraints getConstraintsForChild(BoxConstraints constraints) =>
      constraints.loosen();

  @override
  Offset getPositionForChild(Size size, Size childSize) {
    final freeW = math.max(0.0, size.width - childSize.width);
    final freeH = math.max(0.0, size.height - childSize.height);
    final top = math.min((alignment.y + 1) / 2 * freeH, freeH - covered);
    return Offset((alignment.x + 1) / 2 * freeW, math.max(0.0, top));
  }

  @override
  bool shouldRelayout(_OsdLayout oldDelegate) =>
      oldDelegate.alignment != alignment || oldDelegate.covered != covered;
}

class _OsdHost extends StatelessWidget {
  const _OsdHost({required this.signals, required this.level});

  final SessionSignals signals;
  final OsdLevel level;

  @override
  Widget build(BuildContext context) {
    return ValueListenableBuilder<SessionPhase>(
      valueListenable: signals.phase,
      builder: (context, phase, _) {
        final show = phase == SessionPhase.streaming;
        return AnimatedSwitcher(
          duration: OsmiumMotion.durationOr(context, OsmiumMotion.small),
          switchInCurve: Curves.easeOutCubic,
          switchOutCurve: Curves.easeInCubic,
          transitionBuilder: (child, animation) => FadeTransition(
            opacity: animation,
            child: ScaleTransition(
              scale: Tween<double>(begin: 0.9, end: 1).animate(animation),
              child: child,
            ),
          ),
          child: show
              ? ValueListenableBuilder<SessionStatsSnapshot>(
                  key: const ValueKey('osd'),
                  valueListenable: signals.stats,
                  builder: (context, stats, _) =>
                      OsdCapsule(level: level, stats: stats),
                )
              : const SizedBox.shrink(key: ValueKey('osd-empty')),
        );
      },
    );
  }
}

class _ToastHost extends StatelessWidget {
  const _ToastHost({required this.signals});

  final SessionSignals signals;

  @override
  Widget build(BuildContext context) {
    return ValueListenableBuilder<String?>(
      valueListenable: signals.toast,
      builder: (context, message, _) {
        return AnimatedSwitcher(
          duration: OsmiumMotion.durationOr(context, OsmiumMotion.small),
          switchInCurve: Curves.easeOutCubic,
          switchOutCurve: Curves.easeInCubic,
          transitionBuilder: (child, animation) => FadeTransition(
            opacity: animation,
            child: SlideTransition(
              position: Tween<Offset>(
                begin: const Offset(0, -0.4),
                end: Offset.zero,
              ).animate(animation),
              child: child,
            ),
          ),
          child: message == null
              ? const SizedBox.shrink(key: ValueKey('toast-empty'))
              : Center(
                  key: ValueKey(message),
                  child: ClipRRect(
                    borderRadius: BorderRadius.circular(8),
                    child: BackdropFilter(
                      filter: OsmiumGlass.filter(16),
                      child: Container(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 12,
                          vertical: 6,
                        ),
                        decoration: BoxDecoration(
                          color: const Color(0x800E121B),
                          borderRadius: BorderRadius.circular(8),
                          border: Border.all(
                            color: OsmiumColors.sessionGlassBorder,
                            width: 0.75,
                          ),
                        ),
                        child: Text(
                          message,
                          style: const TextStyle(
                            fontFamily: OsmiumTheme.fontFamily,
                            fontSize: 12.5,
                            fontWeight: FontWeight.w500,
                            color: Colors.white,
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
        );
      },
    );
  }
}
