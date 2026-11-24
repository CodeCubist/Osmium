import 'dart:io';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter/physics.dart';
import 'package:flutter_miuix/miuix.dart';

import '../../../core/constants/osmium_constants.dart';
import '../../../core/models/host_status.dart';
import '../../../core/models/osmium_settings.dart';
import '../../../core/theme/osmium_motion.dart';
import '../../../core/theme/osmium_theme.dart';
import '../../../services/session/aux_channel.dart';
import '../../../services/session/remote_session_controller.dart';
import 'session_signals.dart';
import 'virtual_keyboard_overlay.dart';

/// Frosted session panel: a strip of quick actions over grouped settings.
/// Slides in from the right in landscape and up from the bottom in portrait.
class ControlPanelSheet extends StatefulWidget {
  static const Key surfaceKey = ValueKey('control-panel-surface');

  const ControlPanelSheet({
    super.key,
    required this.controller,
    required this.signals,
    required this.settings,
    required this.onSettingsChanged,
    required this.onOpenKeyboard,
    required this.onClose,
    required this.onDisconnect,
    this.visible = true,
  });

  final RemoteSessionController controller;
  final SessionSignals signals;

  /// Kept mounted while false so the sheet can spring away instead of vanishing.
  final bool visible;
  final OsmiumSettings settings;
  final ValueChanged<OsmiumSettings> onSettingsChanged;
  final ValueChanged<KeyboardOverlayMode> onOpenKeyboard;
  final VoidCallback onClose;
  final VoidCallback onDisconnect;

  @override
  State<ControlPanelSheet> createState() => _ControlPanelSheetState();
}

class _ControlPanelSheetState extends State<ControlPanelSheet>
    with TickerProviderStateMixin {
  late final AnimationController _slide;
  late final AnimationController _dragSpring;
  double _dragExtent = 0;

  /// A restart or shutdown waiting to be confirmed.
  _PendingPower? _confirm;

  @override
  void initState() {
    super.initState();
    _slide =
        AnimationController(
          vsync: this,
          duration: OsmiumMotion.pagePush,
          reverseDuration: OsmiumMotion.pagePop,
          value: widget.visible ? 1 : 0,
        )..addStatusListener((status) {
          if (status == AnimationStatus.dismissed &&
              mounted &&
              _dragExtent > 0) {
            setState(() => _dragExtent = 0);
          }
        });
    _dragSpring = AnimationController.unbounded(vsync: this, value: 0)
      ..addListener(() {
        if (mounted) setState(() => _dragExtent = _dragSpring.value);
      });
  }

  @override
  void didUpdateWidget(covariant ControlPanelSheet oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.visible == oldWidget.visible) return;
    _confirm = null;
    if (widget.visible) {
      // Reopening: drop any leftover drag offset and come back up.
      _dragSpring.stop();
      setState(() => _dragExtent = 0);
      if (OsmiumMotion.reduced(context)) {
        _slide.value = 1;
      } else {
        _slide.forward();
      }
    } else if (OsmiumMotion.reduced(context)) {
      setState(() => _dragExtent = 0);
      _slide.value = 0;
    } else {
      // Keep the drag offset so a drag-dismiss continues downwards smoothly.
      _slide.reverse();
    }
  }

  @override
  void dispose() {
    _slide.dispose();
    _dragSpring.dispose();
    super.dispose();
  }

  double get _dismissExtent {
    final size = MediaQuery.sizeOf(context);
    return size.width > size.height ? size.width : size.height * 0.9;
  }

  void _onDragUpdate(DragUpdateDetails details) {
    final delta = details.primaryDelta ?? 0;
    // Only the dismiss direction resists; the other way is rubber-banded.
    setState(() {
      _dragExtent = _dragExtent + delta > 0 ? _dragExtent + delta : 0;
    });
  }

  void _onDragEnd(DragEndDetails details) {
    final velocity = details.primaryVelocity ?? 0;
    if (_dragExtent > _dismissExtent * 0.24 || velocity > 700) {
      widget.onClose();
      return;
    }
    if (OsmiumMotion.reduced(context)) {
      setState(() => _dragExtent = 0);
      return;
    }
    _dragSpring.animateWith(
      SpringSimulation(OsmiumMotion.sheetExit, _dragExtent, 0, velocity * 0.35),
    );
  }

  Future<void> _pickAndSendFile() async {
    final result = await FilePicker.platform.pickFiles();
    final path = result?.files.single.path;
    if (path == null || path.isEmpty) return;
    await widget.controller.sendFile(File(path));
    if (mounted) widget.onClose();
  }

  @override
  Widget build(BuildContext context) {
    final size = MediaQuery.sizeOf(context);
    final pad = MediaQuery.paddingOf(context);
    final isLandscape = size.width > size.height;
    final extent = isLandscape ? size.width : size.height * 0.9;

    return AnimatedBuilder(
      animation: Listenable.merge([_slide, _dragSpring]),
      builder: (context, child) {
        final t = _slide.value.clamp(0.0, 1.0);
        if (t == 0 && _dragExtent <= 0) return const SizedBox.shrink();
        final curve = _slide.status == AnimationStatus.reverse
            ? OsmiumMotion.exitCurve
            : OsmiumMotion.pushCurve;
        final eased = curve.transform(t).clamp(0.0, 1.0);
        final offset = (1 - eased) * extent;
        final dx = isLandscape ? offset + _dragExtent : 0.0;
        final dy = isLandscape ? 0.0 : offset + _dragExtent;
        return IgnorePointer(
          ignoring: t < 0.02 && _dragExtent <= 0,
          child: Stack(
            fit: StackFit.expand,
            children: [
              GestureDetector(
                behavior: HitTestBehavior.opaque,
                onTap: widget.onClose,
                child: ColoredBox(
                  color: Colors.black.withValues(alpha: 0.36 * eased),
                ),
              ),
              Align(
                alignment: isLandscape
                    ? Alignment.centerRight
                    : Alignment.bottomCenter,
                child: Transform.translate(
                  offset: Offset(dx, dy),
                  child: GestureDetector(
                    onTap: () {},
                    onHorizontalDragUpdate: isLandscape ? _onDragUpdate : null,
                    onHorizontalDragEnd: isLandscape ? _onDragEnd : null,
                    onVerticalDragUpdate: isLandscape ? null : _onDragUpdate,
                    onVerticalDragEnd: isLandscape ? null : _onDragEnd,
                    child: child,
                  ),
                ),
              ),
              AnimatedSwitcher(
                duration: OsmiumMotion.durationOr(context, OsmiumMotion.small),
                switchInCurve: Curves.easeOutCubic,
                switchOutCurve: Curves.easeInCubic,
                transitionBuilder: (child, animation) => FadeTransition(
                  opacity: animation,
                  child: ScaleTransition(
                    scale: Tween<double>(begin: 0.94, end: 1).animate(animation),
                    child: child,
                  ),
                ),
                child: switch (_confirm) {
                  null => const SizedBox.shrink(),
                  final pending => _PowerConfirm(
                    key: ValueKey(pending),
                    pending: pending,
                    hostName: widget.signals.hostName.value,
                    onCancel: () => setState(() => _confirm = null),
                    onConfirm: () {
                      if (_confirm != pending) return;
                      OsmiumHaptics.confirm();
                      widget.controller.sendSystemAction(pending.code);
                      setState(() => _confirm = null);
                      widget.onClose();
                    },
                  ),
                },
              ),
            ],
          ),
        );
      },
      child: ListenableBuilder(
        listenable: widget.signals.revision,
        builder: (context, _) => Container(
          width: isLandscape ? 348 : double.infinity,
          constraints: BoxConstraints(
            maxHeight: isLandscape ? size.height - 20 : size.height * 0.74,
          ),
          margin: isLandscape
              ? EdgeInsets.only(right: pad.right + 10, top: 10, bottom: 10)
              : EdgeInsets.only(left: 8, right: 8, bottom: pad.bottom + 8),
          child: _PanelShell(
            child: Padding(
              padding: EdgeInsets.fromLTRB(14, isLandscape ? 14 : 8, 14, 0),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  if (!isLandscape) ...[
                    Center(
                      child: Container(
                        width: 32,
                        height: 4,
                        decoration: BoxDecoration(
                          color: Colors.white.withValues(alpha: 0.24),
                          borderRadius: BorderRadius.circular(2),
                        ),
                      ),
                    ),
                    const SizedBox(height: 12),
                  ],
                  _buildHeader(),
                  const SizedBox(height: 14),
                  _buildQuickTiles(),
                  const SizedBox(height: 12),
                  Container(
                    height: 0.75,
                    color: OsmiumColors.sessionGlassHairline,
                  ),
                  Flexible(
                    child: SingleChildScrollView(
                      padding: const EdgeInsets.only(top: 10, bottom: 14),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          const _SectionTitle('操作', first: true),
                          _buildControlGroup(),
                          const _SectionTitle('显示'),
                          _buildDisplayGroup(),
                          const _SectionTitle('声音与传输'),
                          _buildAudioTransferGroup(),
                          const _SectionTitle('系统'),
                          _buildSystemGroup(),
                        ],
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildHeader() {
    return Row(
      children: [
        Expanded(
          child: ValueListenableBuilder<String>(
            valueListenable: widget.signals.hostName,
            builder: (context, name, _) => Text(
              name,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: const TextStyle(
                fontFamily: OsmiumTheme.fontFamily,
                fontSize: 16,
                fontWeight: FontWeight.w600,
                height: 1.25,
                color: Colors.white,
              ),
            ),
          ),
        ),
        const SizedBox(width: 10),
        Container(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
          decoration: BoxDecoration(
            color: OsmiumColors.accentOnGlass.withValues(alpha: 0.14),
            borderRadius: BorderRadius.circular(7),
            border: Border.all(
              color: OsmiumColors.accentOnGlass.withValues(alpha: 0.28),
              width: 0.75,
            ),
          ),
          child: ValueListenableBuilder<SessionStatsSnapshot>(
            valueListenable: widget.signals.stats,
            builder: (context, stats, _) => Text(
              '${stats.routeLabel} ${stats.rttMs}ms',
              style: OsmiumTheme.monoStyle(
                fontSize: 10.5,
                fontWeight: FontWeight.w600,
                color: OsmiumColors.accentOnGlass,
              ),
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildQuickTiles() {
    final ctrl = widget.controller;
    final tiles = [
      _QuickTile(
        icon: Icons.logout_rounded,
        label: '退出远控',
        danger: true,
        onTap: widget.onDisconnect,
      ),
      _QuickTile(
        icon: Icons.screen_rotation_rounded,
        label: switch (ctrl.orientationMode) {
          1 => '竖屏',
          2 => '横屏',
          _ => '自动旋转',
        },
        active: ctrl.orientationMode != 0,
        onTap: ctrl.cycleOrientation,
      ),
      _QuickTile(
        icon: ctrl.audioMuted
            ? Icons.volume_off_rounded
            : Icons.volume_up_rounded,
        label: '声音',
        active: !ctrl.audioMuted,
        onTap: ctrl.toggleMute,
      ),
      _QuickTile(
        icon: ctrl.viewOnly
            ? Icons.visibility_rounded
            : Icons.visibility_outlined,
        label: '仅观看',
        active: ctrl.viewOnly,
        onTap: ctrl.viewOnlyLocked ? null : ctrl.toggleViewOnly,
      ),
      _QuickTile(
        icon: Icons.refresh_rounded,
        label: '刷新画面',
        onTap: () {
          ctrl.requestIdr();
          widget.onClose();
        },
      ),
    ];
    return Row(
      children: [
        for (var i = 0; i < tiles.length; i++) ...[
          if (i > 0) const SizedBox(width: 8),
          Expanded(child: tiles[i]),
        ],
      ],
    );
  }

  Widget _buildControlGroup() {
    final s = widget.settings;
    final ctrl = widget.controller;
    final isDirect = s.controlMode == ControlMode.directTouch;

    return _Group(
      children: [
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: _Segmented(
            height: 38,
            labels: const ['屏幕触控', '鼠标指针'],
            icons: const [Icons.touch_app_rounded, Icons.near_me_rounded],
            selected: isDirect ? 0 : 1,
            onSelect: (i) => widget.onSettingsChanged(
              s.copyWith(
                controlMode: i == 0
                    ? ControlMode.directTouch
                    : ControlMode.precisionMouse,
              ),
            ),
          ),
        ),
        if (!isDirect) ...[
          _SliderRow(
            label: '指针速度',
            valueText: '${s.cursorSensitivity.toStringAsFixed(2)}x',
            value: s.cursorSensitivity,
            min: 0.4,
            max: 2.5,
            onChanged: (v) =>
                widget.onSettingsChanged(s.copyWith(cursorSensitivity: v)),
          ),
          _ToggleRow(
            label: '放大镜',
            value: s.precisionMagnifierEnabled,
            onChanged: (v) => widget.onSettingsChanged(
              s.copyWith(precisionMagnifierEnabled: v),
            ),
          ),
        ],
        _ToggleRow(
          label: '外接鼠标捕获',
          value: ctrl.pointerCaptureActive,
          onChanged: (_) => ctrl.togglePointerCapture(),
        ),
        Padding(
          padding: const EdgeInsets.only(top: 4, bottom: 8),
          child: Row(
            children: [
              Expanded(
                child: _GroupButton(
                  icon: Icons.keyboard_alt_outlined,
                  label: '输入法',
                  onTap: () {
                    widget.onClose();
                    widget.onOpenKeyboard(KeyboardOverlayMode.systemIme);
                  },
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: _GroupButton(
                  icon: Icons.keyboard_rounded,
                  label: 'PC 键盘',
                  onTap: () {
                    widget.onClose();
                    widget.onOpenKeyboard(KeyboardOverlayMode.pcFull);
                  },
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }

  Widget _buildDisplayGroup() {
    final s = widget.settings;
    final ctrl = widget.controller;
    final displays = ctrl.hostStatus.displays;
    const resolutions = [
      (1280, 720, '720P'),
      (1920, 1080, '1080P'),
      (2560, 1440, '2K'),
      (3840, 2160, '4K'),
    ];
    const fps = OsmiumConstants.fpsPresets;

    void applyVideo(OsmiumSettings next) {
      widget.onSettingsChanged(next);
      ctrl.applySettings(next, updateVideo: true);
    }

    return _Group(
      children: [
        if (displays.length > 1)
          _LabeledRow(
            label: '显示器',
            child: _Segmented(
              labels: [for (final d in displays) '${d.index + 1}'],
              selected: displays.indexWhere(
                (d) => d.index == ctrl.hostStatus.activeDisplayIndex,
              ),
              onSelect: (i) => ctrl.switchDisplay(displays[i].index),
            ),
          ),
        _LabeledRow(
          label: '分辨率',
          child: _Segmented(
            mono: true,
            labels: [for (final r in resolutions) r.$3],
            selected: resolutions.indexWhere(
              (r) => s.desiredWidth == r.$1 && s.desiredHeight == r.$2,
            ),
            onSelect: (i) => applyVideo(
              s.copyWith(
                desiredWidth: resolutions[i].$1,
                desiredHeight: resolutions[i].$2,
              ),
            ),
          ),
        ),
        _LabeledRow(
          label: '帧率',
          child: _Segmented(
            mono: true,
            labels: [for (final f in fps) '$f'],
            selected: fps.indexOf(s.desiredFps),
            onSelect: (i) => applyVideo(s.copyWith(desiredFps: fps[i])),
          ),
        ),
        _SliderRow(
          label: '码率',
          valueText: '${(s.desiredBitrateKbps / 1000).toStringAsFixed(0)} Mbps',
          value: (s.desiredBitrateKbps / 1000.0).clamp(2.0, 50.0),
          min: 2.0,
          max: 50.0,
          onChanged: (v) =>
              applyVideo(s.copyWith(desiredBitrateKbps: v.round() * 1000)),
        ),
        _ToggleRow(
          label: 'HDR',
          value: s.enableHdr,
          onChanged: (v) => applyVideo(s.copyWith(enableHdr: v)),
        ),
        _ToggleRow(
          label: 'YUV 4:4:4',
          value: s.enableYuv444,
          onChanged: (v) => applyVideo(s.copyWith(enableYuv444: v)),
        ),
        _LabeledRow(
          label: 'OSD',
          child: _Segmented(
            labels: const ['关', '简', '标', '详'],
            selected: OsdLevel.values.indexOf(s.osdLevel),
            onSelect: (i) => widget.onSettingsChanged(
              s.copyWith(osdLevel: OsdLevel.values[i]),
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildAudioTransferGroup() {
    final ctrl = widget.controller;

    return _Group(
      children: [
        _ToggleRow(
          label: '麦克风',
          value: ctrl.micEnabled,
          onChanged: (_) => ctrl.toggleMicPassthrough(),
        ),
        _ToggleRow(
          label: '虚拟声卡',
          value: ctrl.virtualSpeaker,
          onChanged: (_) => ctrl.toggleVirtualSpeaker(),
        ),
        Padding(
          padding: const EdgeInsets.only(top: 4, bottom: 8),
          child: Row(
            children: [
              Expanded(
                child: _GroupButton(
                  icon: Icons.content_paste_go_rounded,
                  label: '发送剪贴板',
                  onTap: () {
                    ctrl.pushLocalClipboard();
                    widget.onClose();
                  },
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: _GroupButton(
                  icon: Icons.upload_file_rounded,
                  label: '发送文件',
                  onTap: _pickAndSendFile,
                ),
              ),
            ],
          ),
        ),
        ValueListenableBuilder<AuxTransferProgressEvent?>(
          valueListenable: widget.signals.transfer,
          builder: (context, progress, _) {
            return AnimatedSize(
              duration: OsmiumMotion.durationOr(context, OsmiumMotion.small),
              curve: MiuixMotion.standardDecelerate,
              alignment: Alignment.topCenter,
              child: progress == null
                  ? const SizedBox(width: double.infinity)
                  : Padding(
                      padding: const EdgeInsets.only(bottom: 10),
                      child: _TransferProgress(progress: progress),
                    ),
            );
          },
        ),
      ],
    );
  }

  Widget _buildSystemGroup() {
    final ctrl = widget.controller;

    Widget action(
      IconData icon,
      String label,
      VoidCallback onTap, {
      bool danger = false,
    }) => Expanded(
      child: _QuickTile(
        icon: icon,
        label: label,
        danger: danger,
        compact: true,
        onTap: onTap,
      ),
    );

    void sendAndClose(int code) {
      ctrl.sendSystemAction(code);
      widget.onClose();
    }

    return _Group(
      padding: const EdgeInsets.all(8),
      children: [
        Row(
          children: [
            action(
              Icons.security_rounded,
              'Ctrl+Alt+Del',
              () => sendAndClose(1),
            ),
            const SizedBox(width: 8),
            action(Icons.analytics_outlined, '任务管理器', () => sendAndClose(2)),
            const SizedBox(width: 8),
            action(Icons.lock_outline_rounded, '锁屏', () => sendAndClose(3)),
          ],
        ),
        const SizedBox(height: 8),
        Row(
          children: [
            action(
              Icons.restart_alt_rounded,
              '重启主机',
              () => setState(() => _confirm = _PendingPower.restart),
              danger: true,
            ),
            const SizedBox(width: 8),
            action(
              Icons.power_settings_new_rounded,
              '关机',
              () => setState(() => _confirm = _PendingPower.shutdown),
              danger: true,
            ),
          ],
        ),
      ],
    );
  }
}

class _PanelShell extends StatelessWidget {
  const _PanelShell({required this.child});

  final Widget child;

  static const double _radius = 18;

  @override
  Widget build(BuildContext context) {
    final shape = BorderRadius.circular(_radius);
    return DecoratedBox(
      key: ControlPanelSheet.surfaceKey,
      decoration: BoxDecoration(
        borderRadius: shape,
        boxShadow: const [
          BoxShadow(
            color: Color(0x5A000000),
            blurRadius: 30,
            offset: Offset(0, 10),
          ),
        ],
      ),
      child: ClipRRect(
        borderRadius: shape,
        child: BackdropFilter(
          filter: OsmiumGlass.filter(28),
          child: ColoredBox(
            color: OsmiumColors.sessionGlassPanelFill,
            child: DecoratedBox(
              decoration: BoxDecoration(
                borderRadius: shape,
                gradient: LinearGradient(
                  begin: Alignment.topCenter,
                  end: Alignment.bottomCenter,
                  colors: [
                    Colors.white.withValues(alpha: 0.08),
                    Colors.transparent,
                  ],
                  stops: const [0, 0.4],
                ),
                border: Border.all(
                  color: OsmiumColors.sessionGlassBorder,
                  width: 0.85,
                ),
              ),
              child: child,
            ),
          ),
        ),
      ),
    );
  }
}

enum _PendingPower {
  restart(4, Icons.restart_alt_rounded, '重启', '重启'),
  shutdown(5, Icons.power_settings_new_rounded, '关闭', '关机');

  const _PendingPower(this.code, this.icon, this.verb, this.action);

  /// System action code sent to the host.
  final int code;
  final IconData icon;
  final String verb;
  final String action;
}

/// Second step before a restart or shutdown reaches the host.
class _PowerConfirm extends StatelessWidget {
  const _PowerConfirm({
    super.key,
    required this.pending,
    required this.hostName,
    required this.onCancel,
    required this.onConfirm,
  });

  final _PendingPower pending;
  final String hostName;
  final VoidCallback onCancel;
  final VoidCallback onConfirm;

  @override
  Widget build(BuildContext context) {
    final target = hostName.isEmpty ? '远程主机' : hostName;
    return GestureDetector(
      behavior: HitTestBehavior.opaque,
      onTap: onCancel,
      child: ColoredBox(
        color: Colors.black.withValues(alpha: 0.32),
        child: Center(
          child: GestureDetector(
            onTap: () {},
            child: SizedBox(
              width: 296,
              child: _PanelShell(
                child: Padding(
                  padding: const EdgeInsets.fromLTRB(20, 24, 20, 18),
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      Icon(
                        pending.icon,
                        size: 30,
                        color: OsmiumColors.dangerFgDark,
                      ),
                      const SizedBox(height: 12),
                      Text(
                        '${pending.verb} $target？',
                        textAlign: TextAlign.center,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(
                          fontFamily: OsmiumTheme.fontFamily,
                          fontSize: 16,
                          fontWeight: FontWeight.w600,
                          height: 1.35,
                          color: Colors.white,
                        ),
                      ),
                      const SizedBox(height: 22),
                      Row(
                        children: [
                          Expanded(
                            child: _ConfirmButton(
                              label: '取消',
                              onTap: onCancel,
                            ),
                          ),
                          const SizedBox(width: 10),
                          Expanded(
                            child: _ConfirmButton(
                              key: const ValueKey('power-confirm'),
                              label: pending.action,
                              danger: true,
                              onTap: onConfirm,
                            ),
                          ),
                        ],
                      ),
                    ],
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

class _ConfirmButton extends StatelessWidget {
  const _ConfirmButton({
    super.key,
    required this.label,
    required this.onTap,
    this.danger = false,
  });

  final String label;
  final VoidCallback onTap;
  final bool danger;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      behavior: HitTestBehavior.opaque,
      onTap: onTap,
      child: Container(
        height: 44,
        alignment: Alignment.center,
        decoration: BoxDecoration(
          color: danger
              ? OsmiumColors.dangerFgDark.withValues(alpha: 0.9)
              : Colors.white.withValues(alpha: 0.09),
          borderRadius: BorderRadius.circular(12),
          border: danger
              ? null
              : Border.all(
                  color: OsmiumColors.sessionGlassHairline,
                  width: 0.75,
                ),
        ),
        child: Text(
          label,
          style: TextStyle(
            fontFamily: OsmiumTheme.fontFamily,
            fontSize: 14,
            fontWeight: FontWeight.w600,
            color: danger ? Colors.white : Colors.white.withValues(alpha: 0.9),
          ),
        ),
      ),
    );
  }
}

class _SectionTitle extends StatelessWidget {
  const _SectionTitle(this.text, {this.first = false});

  final String text;
  final bool first;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: EdgeInsets.fromLTRB(4, first ? 2 : 18, 4, 8),
      child: Text(
        text,
        style: TextStyle(
          fontFamily: OsmiumTheme.fontFamily,
          fontSize: 12.5,
          fontWeight: FontWeight.w600,
          color: Colors.white.withValues(alpha: 0.5),
        ),
      ),
    );
  }
}

/// A rounded glass card holding one section's rows.
class _Group extends StatelessWidget {
  const _Group({
    required this.children,
    this.padding = const EdgeInsets.symmetric(horizontal: 12, vertical: 2),
  });

  final List<Widget> children;
  final EdgeInsetsGeometry padding;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: padding,
      decoration: BoxDecoration(
        color: Colors.white.withValues(alpha: 0.055),
        borderRadius: BorderRadius.circular(14),
        border: Border.all(
          color: OsmiumColors.sessionGlassHairline,
          width: 0.75,
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: children,
      ),
    );
  }
}

class _QuickTile extends StatelessWidget {
  const _QuickTile({
    required this.icon,
    required this.label,
    required this.onTap,
    this.active = false,
    this.danger = false,
    this.compact = false,
  });

  final IconData icon;
  final String label;

  /// Null shows the tile in its current state without taking taps.
  final VoidCallback? onTap;
  final bool active;
  final bool danger;
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final bg = danger
        ? OsmiumColors.dangerBgDark.withValues(alpha: 0.6)
        : active
        ? OsmiumColors.accentOnGlass.withValues(alpha: 0.2)
        : Colors.white.withValues(alpha: compact ? 0.06 : 0.075);
    final borderColor = danger
        ? OsmiumColors.dangerFgDark.withValues(alpha: 0.28)
        : active
        ? OsmiumColors.accentOnGlass.withValues(alpha: 0.5)
        : OsmiumColors.sessionGlassHairline;
    final fg = danger
        ? OsmiumColors.dangerFgDark
        : active
        ? OsmiumColors.accentOnGlass
        : Colors.white.withValues(alpha: 0.9);

    return GestureDetector(
      behavior: HitTestBehavior.opaque,
      onTap: onTap == null
          ? null
          : () {
              OsmiumHaptics.selection();
              onTap!();
            },
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 160),
        height: compact ? 54 : 62,
        decoration: BoxDecoration(
          color: bg,
          borderRadius: BorderRadius.circular(compact ? 10 : 13),
          border: Border.all(color: borderColor, width: 0.75),
        ),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(icon, size: compact ? 18 : 21, color: fg),
            SizedBox(height: compact ? 4 : 6),
            Text(
              label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontFamily: OsmiumTheme.fontFamily,
                fontSize: compact ? 11 : 10.5,
                fontWeight: FontWeight.w500,
                color: fg,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _GroupButton extends StatelessWidget {
  const _GroupButton({
    required this.icon,
    required this.label,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      behavior: HitTestBehavior.opaque,
      onTap: () {
        OsmiumHaptics.selection();
        onTap();
      },
      child: Container(
        height: 40,
        decoration: BoxDecoration(
          color: Colors.white.withValues(alpha: 0.07),
          borderRadius: BorderRadius.circular(10),
          border: Border.all(
            color: OsmiumColors.sessionGlassHairline,
            width: 0.75,
          ),
        ),
        child: Row(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(icon, size: 16, color: Colors.white.withValues(alpha: 0.82)),
            const SizedBox(width: 6),
            Flexible(
              child: Text(
                label,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: const TextStyle(
                  fontFamily: OsmiumTheme.fontFamily,
                  fontSize: 12.5,
                  fontWeight: FontWeight.w500,
                  color: Colors.white,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// Equal-width segmented choice; [selected] of -1 highlights nothing.
class _Segmented extends StatelessWidget {
  const _Segmented({
    required this.labels,
    required this.selected,
    required this.onSelect,
    this.icons,
    this.height = 32,
    this.mono = false,
  });

  final List<String> labels;
  final List<IconData>? icons;
  final int selected;
  final ValueChanged<int> onSelect;
  final double height;
  final bool mono;

  @override
  Widget build(BuildContext context) {
    return Container(
      height: height,
      padding: const EdgeInsets.all(2.5),
      decoration: BoxDecoration(
        color: Colors.black.withValues(alpha: 0.22),
        borderRadius: BorderRadius.circular(10),
        border: Border.all(
          color: OsmiumColors.sessionGlassHairline,
          width: 0.75,
        ),
      ),
      child: Row(
        children: [
          for (var i = 0; i < labels.length; i++)
            Expanded(
              child: GestureDetector(
                behavior: HitTestBehavior.opaque,
                onTap: () {
                  if (i == selected) return;
                  OsmiumHaptics.selection();
                  onSelect(i);
                },
                child: AnimatedContainer(
                  duration: OsmiumMotion.durationOr(
                    context,
                    OsmiumMotion.quick,
                  ),
                  curve: MiuixMotion.standardDecelerate,
                  alignment: Alignment.center,
                  decoration: BoxDecoration(
                    color: i == selected
                        ? Colors.white.withValues(alpha: 0.16)
                        : Colors.transparent,
                    borderRadius: BorderRadius.circular(7.5),
                    border: Border.all(
                      color: i == selected
                          ? Colors.white.withValues(alpha: 0.1)
                          : Colors.transparent,
                      width: 0.75,
                    ),
                  ),
                  child: _segmentLabel(i),
                ),
              ),
            ),
        ],
      ),
    );
  }

  Widget _segmentLabel(int i) {
    final on = i == selected;
    final color = on ? Colors.white : Colors.white.withValues(alpha: 0.58);
    final text = Text(
      labels[i],
      maxLines: 1,
      overflow: TextOverflow.clip,
      style: mono
          ? OsmiumTheme.monoStyle(
              fontSize: 11.5,
              fontWeight: FontWeight.w600,
              color: color,
            )
          : TextStyle(
              fontFamily: OsmiumTheme.fontFamily,
              fontSize: 12.5,
              fontWeight: on ? FontWeight.w600 : FontWeight.w500,
              color: color,
            ),
    );
    final icon = icons?[i];
    if (icon == null) return text;
    return Row(
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        Icon(icon, size: 15, color: on ? OsmiumColors.accentOnGlass : color),
        const SizedBox(width: 6),
        Flexible(child: text),
      ],
    );
  }
}

class _LabeledRow extends StatelessWidget {
  const _LabeledRow({required this.label, required this.child});

  final String label;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 7),
      child: Row(
        children: [
          SizedBox(
            width: 50,
            child: Text(
              label,
              style: TextStyle(
                fontFamily: OsmiumTheme.fontFamily,
                fontSize: 13.5,
                color: Colors.white.withValues(alpha: 0.86),
              ),
            ),
          ),
          Expanded(child: child),
        ],
      ),
    );
  }
}

class _SliderRow extends StatelessWidget {
  const _SliderRow({
    required this.label,
    required this.valueText,
    required this.value,
    required this.min,
    required this.max,
    required this.onChanged,
  });

  final String label;
  final String valueText;
  final double value;
  final double min;
  final double max;
  final ValueChanged<double> onChanged;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 7),
      child: Column(
        // The slider sizes to its constraints; loose ones collapse it to zero.
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Text(
                label,
                style: TextStyle(
                  fontFamily: OsmiumTheme.fontFamily,
                  fontSize: 13.5,
                  color: Colors.white.withValues(alpha: 0.86),
                ),
              ),
              const Spacer(),
              Text(
                valueText,
                style: OsmiumTheme.monoStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.w600,
                  color: OsmiumColors.accentOnGlass,
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          MiuixSlider(
            value: value.clamp(min, max),
            min: min,
            max: max,
            height: 24,
            onValueChanged: onChanged,
          ),
        ],
      ),
    );
  }
}

class _ToggleRow extends StatelessWidget {
  const _ToggleRow({
    required this.label,
    required this.value,
    required this.onChanged,
  });

  final String label;
  final bool value;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) {
    return SizedBox(
      height: 46,
      child: Row(
        children: [
          Expanded(
            child: Text(
              label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontFamily: OsmiumTheme.fontFamily,
                fontSize: 13.5,
                color: Colors.white.withValues(alpha: 0.92),
              ),
            ),
          ),
          MiuixSwitch(value: value, onChanged: onChanged),
        ],
      ),
    );
  }
}

class _TransferProgress extends StatelessWidget {
  const _TransferProgress({required this.progress});

  final AuxTransferProgressEvent progress;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(10),
      decoration: BoxDecoration(
        color: Colors.black.withValues(alpha: 0.18),
        borderRadius: BorderRadius.circular(10),
        border: Border.all(
          color: OsmiumColors.sessionGlassHairline,
          width: 0.75,
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  progress.summaryName,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(
                    fontFamily: OsmiumTheme.fontFamily,
                    fontSize: 12.5,
                    color: Colors.white,
                  ),
                ),
              ),
              Text(
                progress.failed
                    ? '失败'
                    : progress.completed
                    ? '已完成'
                    : '${(progress.progress * 100).round()}%',
                style: OsmiumTheme.monoStyle(
                  fontSize: 11.5,
                  color: progress.failed
                      ? OsmiumColors.dangerFgDark
                      : OsmiumColors.accentOnGlass,
                ),
              ),
            ],
          ),
          const SizedBox(height: 6),
          ClipRRect(
            borderRadius: BorderRadius.circular(2),
            child: LinearProgressIndicator(
              value: progress.progress,
              minHeight: 4,
              backgroundColor: Colors.white12,
              valueColor: const AlwaysStoppedAnimation(
                OsmiumColors.accentOnGlass,
              ),
            ),
          ),
        ],
      ),
    );
  }
}
