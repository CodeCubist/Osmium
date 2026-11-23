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

/// 4-section HyperOS liquid-glass remote control sheet (操控 / 画质 / 音传 / 系统).
class ControlPanelSheet extends StatefulWidget {
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
  int _tabIndex = 0;

  late final AnimationController _slide;
  late final AnimationController _dragSpring;
  double _dragExtent = 0;

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
                  color: Colors.black.withValues(alpha: 0.42 * eased),
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
            ],
          ),
        );
      },
      child: ListenableBuilder(
        listenable: widget.signals.revision,
        builder: (context, _) => Container(
          width: isLandscape ? 380 : double.infinity,
          constraints: BoxConstraints(
            maxHeight: isLandscape
                ? MediaQuery.sizeOf(context).height - 24
                : MediaQuery.sizeOf(context).height * 0.72,
          ),
          margin: isLandscape
              ? EdgeInsets.only(right: pad.right + 12, top: 12, bottom: 12)
              : EdgeInsets.only(left: 12, right: 12, bottom: pad.bottom + 12),
          child: MiuixGlassPanel(
            shape: const MiuixGlassShape(cornerRadius: 26, smoothing: 1.0),
            fill: const Color(0xEB151A24),
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 14, 16, 16),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  // Header row: host name + route + disconnect
                  Row(
                    children: [
                      Expanded(
                        child: Row(
                          children: [
                            Flexible(
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
                                    color: Colors.white,
                                  ),
                                ),
                              ),
                            ),
                            const SizedBox(width: 8),
                            Container(
                              padding: const EdgeInsets.symmetric(
                                horizontal: 7,
                                vertical: 2,
                              ),
                              decoration: BoxDecoration(
                                color: OsmiumColors.accentOnGlass.withValues(
                                  alpha: 0.18,
                                ),
                                borderRadius: BorderRadius.circular(8),
                              ),
                              child:
                                  ValueListenableBuilder<SessionStatsSnapshot>(
                                    valueListenable: widget.signals.stats,
                                    builder: (context, stats, _) => Text(
                                      '${stats.routeLabel} ${stats.rttMs}ms',
                                      style: OsmiumTheme.monoStyle(
                                        fontSize: 11,
                                        fontWeight: FontWeight.w600,
                                        color: OsmiumColors.accentOnGlass,
                                      ),
                                    ),
                                  ),
                            ),
                          ],
                        ),
                      ),
                      GestureDetector(
                        onTap: widget.onDisconnect,
                        child: Container(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 12,
                            vertical: 6,
                          ),
                          decoration: BoxDecoration(
                            color: OsmiumColors.dangerBgDark,
                            borderRadius: BorderRadius.circular(14),
                          ),
                          child: const Text(
                            '断开',
                            style: TextStyle(
                              fontFamily: OsmiumTheme.fontFamily,
                              fontSize: 12.5,
                              fontWeight: FontWeight.w600,
                              color: OsmiumColors.dangerFgDark,
                            ),
                          ),
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 12),

                  // 4-section segmented bar
                  MiuixGlassSegmentedTabRow(
                    tabs: const ['操控', '画质', '音传', '系统'],
                    selectedIndex: _tabIndex,
                    onSelect: (idx) => setState(() => _tabIndex = idx),
                    height: 36,
                    trackColor: Colors.white.withValues(alpha: 0.08),
                    indicatorColor: Colors.white.withValues(alpha: 0.18),
                    selectedContentColor: Colors.white,
                    contentColor: Colors.white60,
                  ),
                  const SizedBox(height: 14),

                  Flexible(
                    child: AnimatedSize(
                      duration: OsmiumMotion.durationOr(
                        context,
                        OsmiumMotion.small,
                      ),
                      curve: MiuixMotion.standardDecelerate,
                      alignment: Alignment.topCenter,
                      child: SingleChildScrollView(
                        child: AnimatedSwitcher(
                          duration: OsmiumMotion.durationOr(
                            context,
                            OsmiumMotion.quick,
                          ),
                          switchInCurve: MiuixMotion.standardDecelerate,
                          switchOutCurve: MiuixMotion.standardAccelerate,
                          layoutBuilder: (current, previous) => Stack(
                            alignment: Alignment.topCenter,
                            children: [...previous, ?current],
                          ),
                          child: KeyedSubtree(
                            key: ValueKey(_tabIndex),
                            child: switch (_tabIndex) {
                              0 => _buildControlSection(),
                              1 => _buildDisplaySection(),
                              2 => _buildAudioTransferSection(),
                              _ => _buildSystemSection(),
                            },
                          ),
                        ),
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

  Widget _buildControlSection() {
    final s = widget.settings;
    final ctrl = widget.controller;
    final isDirect = s.controlMode == ControlMode.directTouch;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // Primary mode switch: Direct Touch (default) vs Precision Mouse
        Row(
          children: [
            Expanded(
              child: _ModeTile(
                icon: Icons.touch_app_rounded,
                label: '屏幕点控',
                selected: isDirect,
                onTap: () => widget.onSettingsChanged(
                  s.copyWith(controlMode: ControlMode.directTouch),
                ),
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: _ModeTile(
                icon: Icons.near_me_rounded,
                label: '精细鼠标',
                selected: !isDirect,
                onTap: () => widget.onSettingsChanged(
                  s.copyWith(controlMode: ControlMode.precisionMouse),
                ),
              ),
            ),
          ],
        ),
        const SizedBox(height: 12),

        // Quick input tiles
        Row(
          children: [
            Expanded(
              child: _ActionTile(
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
              child: _ActionTile(
                icon: Icons.keyboard_rounded,
                label: 'PC 键盘',
                onTap: () {
                  widget.onClose();
                  widget.onOpenKeyboard(KeyboardOverlayMode.pcFull);
                },
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: _ActionTile(
                icon: Icons.mouse_rounded,
                label: '外接捕获',
                active: ctrl.pointerCaptureActive,
                onTap: () => ctrl.togglePointerCapture(),
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: _ActionTile(
                icon: Icons.screen_rotation_rounded,
                label: switch (ctrl.orientationMode) {
                  1 => '竖屏',
                  2 => '横屏',
                  _ => '自动转屏',
                },
                onTap: () => ctrl.cycleOrientation(),
              ),
            ),
          ],
        ),

        if (!isDirect) ...[
          const SizedBox(height: 14),
          _SliderRow(
            label: '光标灵敏度',
            valueText: '${s.cursorSensitivity.toStringAsFixed(2)}x',
            value: s.cursorSensitivity,
            min: 0.4,
            max: 2.5,
            onChanged: (v) =>
                widget.onSettingsChanged(s.copyWith(cursorSensitivity: v)),
          ),
          const SizedBox(height: 8),
          _ToggleRow(
            label: '精细准星辅助',
            value: s.precisionMagnifierEnabled,
            onChanged: (v) => widget.onSettingsChanged(
              s.copyWith(precisionMagnifierEnabled: v),
            ),
          ),
        ],
      ],
    );
  }

  Widget _buildDisplaySection() {
    final s = widget.settings;
    final ctrl = widget.controller;
    final displays = ctrl.hostStatus.displays;

    void applyVideo(OsmiumSettings next) {
      widget.onSettingsChanged(next);
      ctrl.applySettings(next, updateVideo: true);
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (displays.length > 1) ...[
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: displays.map((d) {
              final active = d.index == ctrl.hostStatus.activeDisplayIndex;
              return _PillChoice(
                label: '显示器 ${d.index + 1}',
                selected: active,
                onTap: () => ctrl.switchDisplay(d.index),
              );
            }).toList(),
          ),
          const SizedBox(height: 12),
        ],

        // Resolution presets
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children:
              [
                (1280, 720, '720P'),
                (1920, 1080, '1080P'),
                (2560, 1440, '2K'),
                (3840, 2160, '4K'),
              ].map((preset) {
                final selected =
                    s.desiredWidth == preset.$1 && s.desiredHeight == preset.$2;
                return _PillChoice(
                  label: preset.$3,
                  selected: selected,
                  onTap: () => applyVideo(
                    s.copyWith(
                      desiredWidth: preset.$1,
                      desiredHeight: preset.$2,
                    ),
                  ),
                );
              }).toList(),
        ),
        const SizedBox(height: 10),

        // FPS presets
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: OsmiumConstants.fpsPresets.map((fps) {
            return _PillChoice(
              label: '$fps FPS',
              selected: s.desiredFps == fps,
              onTap: () => applyVideo(s.copyWith(desiredFps: fps)),
            );
          }).toList(),
        ),
        const SizedBox(height: 12),

        _SliderRow(
          label: '目标码率',
          valueText: '${(s.desiredBitrateKbps / 1000).toStringAsFixed(0)} Mbps',
          value: (s.desiredBitrateKbps / 1000.0).clamp(2.0, 50.0),
          min: 2.0,
          max: 50.0,
          onChanged: (v) =>
              applyVideo(s.copyWith(desiredBitrateKbps: (v.round() * 1000))),
        ),
        const SizedBox(height: 8),

        _ToggleRow(
          label: 'HDR 高动态范围',
          value: s.enableHdr,
          onChanged: (v) => applyVideo(s.copyWith(enableHdr: v)),
        ),
        _ToggleRow(
          label: 'YUV 4:4:4 锐利文字',
          value: s.enableYuv444,
          onChanged: (v) => applyVideo(s.copyWith(enableYuv444: v)),
        ),
        const SizedBox(height: 8),

        // OSD level row
        Row(
          children: [
            const Text(
              'OSD 浮窗',
              style: TextStyle(
                fontFamily: OsmiumTheme.fontFamily,
                fontSize: 13.5,
                color: Colors.white70,
              ),
            ),
            const Spacer(),
            ...OsdLevel.values.map((lvl) {
              final text = switch (lvl) {
                OsdLevel.off => '关',
                OsdLevel.compact => '简',
                OsdLevel.standard => '标',
                OsdLevel.detailed => '详',
              };
              return Padding(
                padding: const EdgeInsets.only(left: 6),
                child: _PillChoice(
                  label: text,
                  selected: s.osdLevel == lvl,
                  onTap: () =>
                      widget.onSettingsChanged(s.copyWith(osdLevel: lvl)),
                ),
              );
            }),
          ],
        ),
      ],
    );
  }

  Widget _buildAudioTransferSection() {
    final ctrl = widget.controller;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          children: [
            Expanded(
              child: _ActionTile(
                icon: ctrl.audioMuted
                    ? Icons.volume_off_rounded
                    : Icons.volume_up_rounded,
                label: ctrl.audioMuted ? '已静音' : '声音',
                active: !ctrl.audioMuted,
                onTap: ctrl.toggleMute,
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: _ActionTile(
                icon: Icons.speaker_group_outlined,
                label: '虚拟声卡',
                active: ctrl.virtualSpeaker,
                onTap: ctrl.toggleVirtualSpeaker,
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: _ActionTile(
                icon: ctrl.micEnabled
                    ? Icons.mic_rounded
                    : Icons.mic_none_rounded,
                label: '麦克风',
                active: ctrl.micEnabled,
                onTap: () => ctrl.toggleMicPassthrough(),
              ),
            ),
          ],
        ),
        const SizedBox(height: 10),
        Row(
          children: [
            Expanded(
              child: _ActionTile(
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
              child: _ActionTile(
                icon: Icons.upload_file_rounded,
                label: '发送文件',
                onTap: _pickAndSendFile,
              ),
            ),
          ],
        ),
        ValueListenableBuilder<AuxTransferProgressEvent?>(
          valueListenable: widget.signals.transfer,
          builder: (context, progress, _) {
            return AnimatedSize(
              duration: OsmiumMotion.durationOr(context, OsmiumMotion.small),
              curve: MiuixMotion.standardDecelerate,
              alignment: Alignment.topCenter,
              child: progress == null
                  ? const SizedBox.shrink()
                  : Padding(
                      padding: const EdgeInsets.only(top: 12),
                      child: Container(
                        padding: const EdgeInsets.all(10),
                        decoration: BoxDecoration(
                          color: Colors.white.withValues(alpha: 0.07),
                          borderRadius: BorderRadius.circular(12),
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
                                    color: OsmiumColors.accentOnGlass,
                                  ),
                                ),
                              ],
                            ),
                            const SizedBox(height: 6),
                            ClipRRect(
                              borderRadius: BorderRadius.circular(4),
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
                      ),
                    ),
            );
          },
        ),
      ],
    );
  }

  Widget _buildSystemSection() {
    final ctrl = widget.controller;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          children: [
            Expanded(
              child: _ActionTile(
                icon: Icons.security_rounded,
                label: 'Ctrl+Alt+Del',
                onTap: () {
                  ctrl.sendSystemAction(1);
                  widget.onClose();
                },
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: _ActionTile(
                icon: Icons.analytics_outlined,
                label: '任务管理器',
                onTap: () {
                  ctrl.sendSystemAction(2);
                  widget.onClose();
                },
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: _ActionTile(
                icon: Icons.lock_outline_rounded,
                label: '锁屏',
                onTap: () {
                  ctrl.sendSystemAction(3);
                  widget.onClose();
                },
              ),
            ),
          ],
        ),
        const SizedBox(height: 10),
        Row(
          children: [
            Expanded(
              child: _ActionTile(
                icon: Icons.refresh_rounded,
                label: '刷新画面',
                onTap: () {
                  ctrl.requestIdr();
                  widget.onClose();
                },
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: _ActionTile(
                icon: Icons.restart_alt_rounded,
                label: '重启主机',
                danger: true,
                onTap: () => ctrl.sendSystemAction(4),
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: _ActionTile(
                icon: Icons.power_settings_new_rounded,
                label: '关机',
                danger: true,
                onTap: () => ctrl.sendSystemAction(5),
              ),
            ),
          ],
        ),
      ],
    );
  }
}

class _ModeTile extends StatelessWidget {
  const _ModeTile({
    required this.icon,
    required this.label,
    required this.selected,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 180),
        height: 54,
        decoration: BoxDecoration(
          color: selected
              ? OsmiumColors.accentOnGlass.withValues(alpha: 0.22)
              : Colors.white.withValues(alpha: 0.08),
          borderRadius: BorderRadius.circular(16),
          border: Border.all(
            color: selected
                ? OsmiumColors.accentOnGlass
                : Colors.white.withValues(alpha: 0.08),
            width: 1.2,
          ),
        ),
        child: Row(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(
              icon,
              size: 20,
              color: selected ? OsmiumColors.accentOnGlass : Colors.white70,
            ),
            const SizedBox(width: 8),
            Text(
              label,
              style: TextStyle(
                fontFamily: OsmiumTheme.fontFamily,
                fontSize: 14,
                fontWeight: FontWeight.w600,
                color: selected ? Colors.white : Colors.white70,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _ActionTile extends StatelessWidget {
  const _ActionTile({
    required this.icon,
    required this.label,
    required this.onTap,
    this.active = false,
    this.danger = false,
  });

  final IconData icon;
  final String label;
  final VoidCallback onTap;
  final bool active;
  final bool danger;

  @override
  Widget build(BuildContext context) {
    final bg = danger
        ? OsmiumColors.dangerBgDark.withValues(alpha: 0.72)
        : active
        ? OsmiumColors.accentOnGlass.withValues(alpha: 0.22)
        : Colors.white.withValues(alpha: 0.08);
    final fg = danger
        ? OsmiumColors.dangerFgDark
        : active
        ? OsmiumColors.accentOnGlass
        : Colors.white.withValues(alpha: 0.88);

    return GestureDetector(
      onTap: onTap,
      child: Container(
        height: 62,
        decoration: BoxDecoration(
          color: bg,
          borderRadius: BorderRadius.circular(15),
        ),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(icon, size: 20, color: fg),
            const SizedBox(height: 5),
            Text(
              label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontFamily: OsmiumTheme.fontFamily,
                fontSize: 11.5,
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

class _PillChoice extends StatelessWidget {
  const _PillChoice({
    required this.label,
    required this.selected,
    required this.onTap,
  });

  final String label;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 7),
        decoration: BoxDecoration(
          color: selected
              ? OsmiumColors.accentOnGlass
              : Colors.white.withValues(alpha: 0.09),
          borderRadius: BorderRadius.circular(12),
        ),
        child: Text(
          label,
          style: OsmiumTheme.monoStyle(
            fontSize: 12.5,
            fontWeight: FontWeight.w600,
            color: selected ? const Color(0xFF002438) : Colors.white70,
          ),
        ),
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
    return Column(
      children: [
        Row(
          children: [
            Text(
              label,
              style: const TextStyle(
                fontFamily: OsmiumTheme.fontFamily,
                fontSize: 13.5,
                color: Colors.white70,
              ),
            ),
            const Spacer(),
            Text(
              valueText,
              style: OsmiumTheme.monoStyle(
                fontSize: 12.5,
                fontWeight: FontWeight.w600,
                color: OsmiumColors.accentOnGlass,
              ),
            ),
          ],
        ),
        const SizedBox(height: 6),
        MiuixSlider(
          value: value.clamp(min, max),
          min: min,
          max: max,
          height: 24,
          onValueChanged: onChanged,
        ),
      ],
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
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: Row(
        children: [
          Text(
            label,
            style: const TextStyle(
              fontFamily: OsmiumTheme.fontFamily,
              fontSize: 13.5,
              color: Colors.white,
            ),
          ),
          const Spacer(),
          MiuixSwitch(value: value, onChanged: onChanged),
        ],
      ),
    );
  }
}
