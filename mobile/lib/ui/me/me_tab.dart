import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_miuix/miuix.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/constants/osmium_constants.dart';
import '../../core/models/account_models.dart';
import '../../core/models/device_identity.dart';
import '../../core/models/osmium_settings.dart';
import '../../core/theme/osmium_motion.dart';
import '../../core/theme/osmium_theme.dart';
import '../../state/app_controller.dart';
import '../auth/login_page.dart';
import '../auth/qr_scanner_page.dart';
import '../shared/home_widgets.dart';
import '../widgets/osmium_dock_scope.dart';
import '../widgets/osmium_motion_widgets.dart';

class MeTab extends ConsumerStatefulWidget {
  const MeTab({super.key, required this.bottomInset});

  final double bottomInset;

  @override
  ConsumerState<MeTab> createState() => _MeTabState();
}

class _MeTabState extends ConsumerState<MeTab> {
  final _scrollBehavior = MiuixExitUntilCollapsedScrollBehavior();
  final TextEditingController _nameCtrl = TextEditingController();
  bool _nameVisible = false;
  bool _logoutVisible = false;
  bool _copied = false;
  Timer? _copiedTimer;
  ValueNotifier<bool>? _dockHidden;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _dockHidden = OsmiumDockScope.maybeOf(context);
  }

  static const _resolutions = <(String, int, int)>[
    ('720p', 1280, 720),
    ('1080p', 1920, 1080),
    ('2K', 2560, 1440),
    ('4K', 3840, 2160),
  ];

  static const _fpsList = OsmiumConstants.fpsPresets;
  static const _bitrates = <int>[6000, 12000, 20000, 35000, 50000];

  @override
  void dispose() {
    _dockHidden?.value = false;
    _copiedTimer?.cancel();
    _nameCtrl.dispose();
    super.dispose();
  }

  void _syncDock() {
    _dockHidden?.value = _nameVisible || _logoutVisible;
  }

  Future<void> _openLogin() async {
    await Navigator.of(context)
        .push<bool>(MaterialPageRoute(builder: (_) => const LoginPage()));
  }

  void _update(OsmiumSettings next) {
    OsmiumHaptics.selection();
    ref.read(appControllerProvider).updateSettings(next);
  }

  void _copyDeviceCode(String code) {
    Clipboard.setData(ClipboardData(text: code));
    OsmiumHaptics.selection();
    _copiedTimer?.cancel();
    setState(() => _copied = true);
    _copiedTimer = Timer(const Duration(milliseconds: 1400), () {
      if (mounted) setState(() => _copied = false);
    });
  }

  void _editSelfName(String currentName) {
    _nameCtrl.text = currentName;
    _nameCtrl.selection = TextSelection(
      baseOffset: 0,
      extentOffset: currentName.length,
    );
    setState(() => _nameVisible = true);
    _syncDock();
  }

  void _dismissSelfName() {
    if (!_nameVisible) return;
    FocusScope.of(context).unfocus();
    setState(() => _nameVisible = false);
    _syncDock();
  }

  Future<void> _saveSelfName() async {
    final trimmed = _nameCtrl.text.trim();
    _dismissSelfName();
    if (trimmed.isNotEmpty) {
      await ref.read(appControllerProvider).renameSelf(trimmed);
    }
  }

  void _confirmLogout() {
    OsmiumHaptics.confirm();
    setState(() => _logoutVisible = true);
    _syncDock();
  }

  void _closeLogout() {
    if (!_logoutVisible) return;
    setState(() => _logoutVisible = false);
    _syncDock();
  }

  Future<void> _logout() async {
    _closeLogout();
    await ref.read(appControllerProvider).logout();
  }

  int _resolutionIndex(OsmiumSettings s) {
    final idx = _resolutions.indexWhere(
      (r) => r.$2 == s.desiredWidth && r.$3 == s.desiredHeight,
    );
    return idx >= 0 ? idx : 1;
  }

  @override
  Widget build(BuildContext context) {
    final app = ref.watch(appControllerProvider);
    final s = app.settings;
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;
    final keyboardInset = MediaQuery.viewInsetsOf(context).bottom;
    final scrollBottom = keyboardInset > 0 ? 20.0 : widget.bottomInset + 24;

    return MiuixScaffold(
      topBar: MiuixTopAppBar(
        title: '我的',
        blurred: true,
        scrollBehavior: _scrollBehavior,
        actions: [
          if (app.isLoggedIn)
            MiuixIconButton(
              onPressed: () {
                OsmiumHaptics.selection();
                Navigator.of(context).push<bool>(
                  MaterialPageRoute(builder: (_) => const QrScannerPage()),
                );
              },
              child: Icon(
                Icons.qr_code_scanner_rounded,
                color: colors.onSurface,
                semanticLabel: '扫码登录',
              ),
            ),
        ],
      ),
      content: (contentPadding) {
        return Stack(
          children: [
            Padding(
              padding: EdgeInsets.only(bottom: keyboardInset),
              child: MiuixScrollBehaviorListener(
                behavior: _scrollBehavior,
                child: Center(
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(maxWidth: 720),
                    child: ListView(
                      keyboardDismissBehavior:
                          ScrollViewKeyboardDismissBehavior.onDrag,
                      physics: const AlwaysScrollableScrollPhysics(
                        parent: BouncingScrollPhysics(),
                      ),
                      padding: EdgeInsets.fromLTRB(
                        16,
                        contentPadding.top + 8,
                        16,
                        scrollBottom,
                      ),
                      children: [
                        _AccountHeroCard(app: app, onLogin: _openLogin),
                        const OsmiumSectionHeader('本机'),
                        MiuixCard(
                          cornerRadius: 20,
                          insideMargin: EdgeInsets.zero,
                          child: Column(
                            children: [
                              MiuixArrowPreference(
                                title: '本机设备码',
                                startAction: const OsmiumPrefIcon(
                                  Icons.tag_rounded,
                                ),
                                endActions: [
                                  Flexible(
                                    child: Text(
                                      DeviceIdentity.formatDeviceId(
                                        app.identity.deviceId,
                                      ),
                                      maxLines: 1,
                                      overflow: TextOverflow.ellipsis,
                                      style: OsmiumTheme.monoStyle(
                                        fontSize: 14,
                                        color: colors.onSurfaceVariantSummary,
                                      ),
                                    ),
                                  ),
                                  const SizedBox(width: 8),
                                  OsmiumCopyMorph(
                                    copied: _copied,
                                    color: _copied
                                        ? colors.primary
                                        : colors.onSurfaceVariantSummary,
                                  ),
                                ],
                                onClick: () =>
                                    _copyDeviceCode(app.identity.deviceId),
                              ),
                              const OsmiumCardDivider(),
                              MiuixArrowPreference(
                                title: '本机名称',
                                startAction: const OsmiumPrefIcon(
                                  Icons.badge_outlined,
                                ),
                                endActions: [
                                  Flexible(
                                    child: MiuixText(
                                      app.identity.deviceName,
                                      style: theme.textStyles.body2,
                                      color: colors.onSurfaceVariantSummary,
                                      maxLines: 1,
                                      overflow: TextOverflow.ellipsis,
                                    ),
                                  ),
                                ],
                                onClick: () =>
                                    _editSelfName(app.identity.deviceName),
                              ),
                            ],
                          ),
                        ),
                        const OsmiumSectionHeader('操控'),
                        MiuixCard(
                          cornerRadius: 20,
                          insideMargin: EdgeInsets.zero,
                          child: Column(
                            children: [
                              MiuixOverlayDropdownPreference(
                                title: '默认操控方式',
                                startAction: const OsmiumPrefIcon(
                                  Icons.touch_app_outlined,
                                ),
                                items: const ['屏幕点控', '精细鼠标'],
                                selectedIndex:
                                    s.controlMode == ControlMode.directTouch
                                    ? 0
                                    : 1,
                                onSelectedIndexChange: (i) {
                                  _update(
                                    s.copyWith(
                                      controlMode: i == 0
                                          ? ControlMode.directTouch
                                          : ControlMode.precisionMouse,
                                    ),
                                  );
                                },
                              ),
                              const OsmiumCardDivider(),
                              MiuixSwitchPreference(
                                title: '精细放大镜',
                                startAction: const OsmiumPrefIcon(
                                  Icons.zoom_in_rounded,
                                ),
                                value: s.precisionMagnifierEnabled,
                                onChanged: (v) => _update(
                                  s.copyWith(precisionMagnifierEnabled: v),
                                ),
                              ),
                              const OsmiumCardDivider(),
                              MiuixSwitchPreference(
                                title: '触觉反馈',
                                startAction: const OsmiumPrefIcon(
                                  Icons.vibration_rounded,
                                ),
                                value: s.enableHaptics,
                                onChanged: (v) =>
                                    _update(s.copyWith(enableHaptics: v)),
                              ),
                            ],
                          ),
                        ),
                        const OsmiumSectionHeader('画面与性能'),
                        MiuixCard(
                          cornerRadius: 20,
                          insideMargin: EdgeInsets.zero,
                          child: Column(
                            children: [
                              MiuixOverlayDropdownPreference(
                                title: '分辨率',
                                startAction: const OsmiumPrefIcon(
                                  Icons.aspect_ratio_rounded,
                                ),
                                items: _resolutions.map((r) => r.$1).toList(),
                                selectedIndex: _resolutionIndex(s),
                                onSelectedIndexChange: (i) {
                                  final res = _resolutions[i];
                                  _update(
                                    s.copyWith(
                                      desiredWidth: res.$2,
                                      desiredHeight: res.$3,
                                    ),
                                  );
                                },
                              ),
                              const OsmiumCardDivider(),
                              MiuixOverlayDropdownPreference(
                                title: '帧率',
                                startAction: const OsmiumPrefIcon(
                                  Icons.speed_rounded,
                                ),
                                items: _fpsList.map((f) => '$f FPS').toList(),
                                selectedIndex: _fpsList
                                    .indexOf(s.desiredFps)
                                    .clamp(0, _fpsList.length - 1),
                                onSelectedIndexChange: (i) {
                                  _update(s.copyWith(desiredFps: _fpsList[i]));
                                },
                              ),
                              const OsmiumCardDivider(),
                              MiuixOverlayDropdownPreference(
                                title: '码率',
                                startAction: const OsmiumPrefIcon(
                                  Icons.data_usage_rounded,
                                ),
                                items: _bitrates
                                    .map((b) => '${b ~/ 1000} Mbps')
                                    .toList(),
                                selectedIndex: _bitrates
                                    .indexOf(s.desiredBitrateKbps)
                                    .clamp(0, _bitrates.length - 1),
                                onSelectedIndexChange: (i) {
                                  _update(
                                    s.copyWith(
                                      desiredBitrateKbps: _bitrates[i],
                                    ),
                                  );
                                },
                              ),
                              const OsmiumCardDivider(),
                              MiuixOverlayDropdownPreference(
                                title: '编码格式',
                                startAction: const OsmiumPrefIcon(
                                  Icons.memory_rounded,
                                ),
                                items: const ['自动', 'H.264', 'HEVC', 'AV1'],
                                selectedIndex: s.codecPref.index,
                                onSelectedIndexChange: (i) {
                                  _update(
                                    s.copyWith(
                                      codecPref: VideoCodecPref.values[i],
                                    ),
                                  );
                                },
                              ),
                              const OsmiumCardDivider(),
                              MiuixSwitchPreference(
                                title: 'HDR',
                                startAction: const OsmiumPrefIcon(
                                  Icons.brightness_high_rounded,
                                ),
                                value: s.enableHdr,
                                onChanged: (v) =>
                                    _update(s.copyWith(enableHdr: v)),
                              ),
                              const OsmiumCardDivider(),
                              MiuixSwitchPreference(
                                title: 'YUV 4:4:4',
                                startAction: const OsmiumPrefIcon(
                                  Icons.gradient_rounded,
                                ),
                                value: s.enableYuv444,
                                onChanged: (v) =>
                                    _update(s.copyWith(enableYuv444: v)),
                              ),
                            ],
                          ),
                        ),
                        const OsmiumSectionHeader('音频与传输'),
                        MiuixCard(
                          cornerRadius: 20,
                          insideMargin: EdgeInsets.zero,
                          child: Column(
                            children: [
                              MiuixSwitchPreference(
                                title: '远程音频',
                                startAction: const OsmiumPrefIcon(
                                  Icons.volume_up_rounded,
                                ),
                                value: s.enableAudio,
                                onChanged: (v) =>
                                    _update(s.copyWith(enableAudio: v)),
                              ),
                              const OsmiumCardDivider(),
                              MiuixSwitchPreference(
                                title: '剪贴板同步',
                                startAction: const OsmiumPrefIcon(
                                  Icons.content_paste_rounded,
                                ),
                                value: s.sharedClipboardEnabled,
                                onChanged: (v) => _update(
                                  s.copyWith(sharedClipboardEnabled: v),
                                ),
                              ),
                            ],
                          ),
                        ),
                        const OsmiumSectionHeader('安全与外观'),
                        MiuixCard(
                          cornerRadius: 20,
                          insideMargin: EdgeInsets.zero,
                          child: Column(
                            children: [
                              MiuixSwitchPreference(
                                title: '端到端加密',
                                startAction: const OsmiumPrefIcon(
                                  Icons.lock_rounded,
                                ),
                                value: s.enableEncryption,
                                onChanged: (v) =>
                                    _update(s.copyWith(enableEncryption: v)),
                              ),
                              const OsmiumCardDivider(),
                              MiuixOverlayDropdownPreference(
                                title: '性能浮窗',
                                startAction: const OsmiumPrefIcon(
                                  Icons.monitor_heart_outlined,
                                ),
                                items: const ['关闭', '精简', '标准', '详细'],
                                selectedIndex: s.osdLevel.index,
                                onSelectedIndexChange: (i) {
                                  _update(
                                    s.copyWith(osdLevel: OsdLevel.values[i]),
                                  );
                                },
                              ),
                              const OsmiumCardDivider(),
                              MiuixOverlayDropdownPreference(
                                title: '外观',
                                startAction: const OsmiumPrefIcon(
                                  Icons.palette_outlined,
                                ),
                                items: const ['跟随系统', '浅色', '深色'],
                                selectedIndex:
                                    s.themePreference == ThemePreference.light
                                    ? 1
                                    : (s.themePreference == ThemePreference.dark
                                          ? 2
                                          : 0),
                                onSelectedIndexChange: (i) {
                                  final pref = switch (i) {
                                    1 => ThemePreference.light,
                                    2 => ThemePreference.dark,
                                    _ => ThemePreference.system,
                                  };
                                  _update(s.copyWith(themePreference: pref));
                                },
                              ),
                            ],
                          ),
                        ),
                        if (app.isLoggedIn) ...[
                          const SizedBox(height: 18),
                          MiuixTextButton(
                            '退出登录',
                            onPressed: _confirmLogout,
                            minHeight: 48,
                            colors: MiuixButtonColors(
                              color: OsmiumColors.dangerBg(isDark),
                              disabledColor: colors.disabledSecondaryVariant,
                              contentColor: OsmiumColors.dangerFg(isDark),
                              disabledContentColor:
                                  colors.disabledOnSecondaryVariant,
                            ),
                          ),
                        ],
                      ],
                    ),
                  ),
                ),
              ),
            ),
            _buildSelfNameDialog(context),
            _buildLogoutDialog(context),
          ],
        );
      },
    );
  }

  Widget _buildSelfNameDialog(BuildContext context) {
    return MiuixOverlayDialog(
      show: _nameVisible,
      title: '本机名称',
      onDismissRequest: _dismissSelfName,
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            MiuixTextField(
              controller: _nameCtrl,
              label: '名称',
              singleLine: true,
              autofocus: true,
              onSubmitted: (_) => _saveSelfName(),
            ),
            const SizedBox(height: 18),
            Row(
              children: [
                Expanded(
                  child: MiuixTextButton('取消', onPressed: _dismissSelfName),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: MiuixTextButton(
                    '保存',
                    onPressed: _saveSelfName,
                    colors: MiuixButtonDefaults.buttonColorsPrimary(context),
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildLogoutDialog(BuildContext context) {
    final colors = MiuixTheme.of(context).colors;
    final isDark = MiuixTheme.of(context).brightness == Brightness.dark;
    return MiuixOverlayDialog(
      show: _logoutVisible,
      title: '退出登录',
      onDismissRequest: _closeLogout,
      content: Row(
        children: [
          Expanded(child: MiuixTextButton('取消', onPressed: _closeLogout)),
          const SizedBox(width: 10),
          Expanded(
            child: MiuixTextButton(
              '退出',
              onPressed: _logout,
              colors: MiuixButtonColors(
                color: OsmiumColors.dangerBg(isDark),
                disabledColor: colors.disabledSecondaryVariant,
                contentColor: OsmiumColors.dangerFg(isDark),
                disabledContentColor: colors.disabledOnSecondaryVariant,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _AccountHeroCard extends StatelessWidget {
  const _AccountHeroCard({required this.app, required this.onLogin});

  final AppController app;
  final VoidCallback onLogin;

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final isDark = theme.brightness == Brightness.dark;
    final acct = app.account;

    if (!app.isLoggedIn || acct == null) {
      return MiuixCard(
        cornerRadius: 24,
        insideMargin: EdgeInsets.zero,
        feedbackType: MiuixPressFeedbackType.sink,
        onPressed: onLogin,
        child: ClipPath(
          clipper: ShapeBorderClipper(
            shape: MiuixSquircleBorder(cornerRadius: 24),
          ),
          child: OsmiumBanner(
            colors: OsmiumBannerGradients.brand(isDark),
            height: 132,
            padding: const EdgeInsets.fromLTRB(20, 20, 18, 20),
            child: Row(
              children: [
                Container(
                  width: 52,
                  height: 52,
                  padding: const EdgeInsets.all(6),
                  decoration: ShapeDecoration(
                    color: Colors.white.withValues(alpha: 0.16),
                    shape: MiuixSquircleBorder(cornerRadius: 16),
                  ),
                  child: ClipRRect(
                    borderRadius: BorderRadius.circular(11),
                    child: Image.asset(
                      'assets/images/osmium_logo.png',
                      fit: BoxFit.cover,
                    ),
                  ),
                ),
                const SizedBox(width: 14),
                Expanded(
                  child: Column(
                    mainAxisAlignment: MainAxisAlignment.center,
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      const Text(
                        'Osmium',
                        style: TextStyle(
                          fontFamily: OsmiumTheme.fontFamily,
                          fontSize: 21,
                          fontWeight: FontWeight.w700,
                          height: 1.2,
                          color: Colors.white,
                        ),
                      ),
                      const SizedBox(height: 4),
                      Text(
                        '登录账号',
                        style: TextStyle(
                          fontFamily: OsmiumTheme.fontFamily,
                          fontSize: 13,
                          fontWeight: FontWeight.w500,
                          color: Colors.white.withValues(alpha: 0.82),
                        ),
                      ),
                    ],
                  ),
                ),
                Icon(
                  Icons.chevron_right_rounded,
                  color: Colors.white.withValues(alpha: 0.86),
                ),
              ],
            ),
          ),
        ),
      );
    }

    return _LoggedInAccountCard(acct: acct, isDark: isDark);
  }
}

class _LoggedInAccountCard extends StatelessWidget {
  const _LoggedInAccountCard({required this.acct, required this.isDark});

  final AccountSessionData acct;
  final bool isDark;

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final initial = acct.username.isNotEmpty
        ? acct.username.characters.first.toUpperCase()
        : (acct.email.isNotEmpty
              ? acct.email.characters.first.toUpperCase()
              : 'O');

    return MiuixCard(
      cornerRadius: 24,
      insideMargin: const EdgeInsets.all(16),
      child: Row(
        children: [
          Container(
            width: 54,
            height: 54,
            alignment: Alignment.center,
            decoration: ShapeDecoration(
              gradient: LinearGradient(
                begin: Alignment.topLeft,
                end: Alignment.bottomRight,
                colors: OsmiumBannerGradients.brand(isDark),
              ),
              shape: MiuixSquircleBorder(cornerRadius: 17),
            ),
            child: Text(
              initial,
              style: const TextStyle(
                fontFamily: OsmiumTheme.fontFamily,
                fontSize: 21,
                fontWeight: FontWeight.w700,
                color: Colors.white,
              ),
            ),
          ),
          const SizedBox(width: 14),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                MiuixText(
                  acct.username.isNotEmpty ? acct.username : acct.email,
                  style: theme.textStyles.headline1,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
                if (acct.email.isNotEmpty) ...[
                  const SizedBox(height: 2),
                  MiuixText(
                    acct.email,
                    style: theme.textStyles.body2,
                    color: colors.onSurfaceVariantSummary,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ],
              ],
            ),
          ),
          if (acct.locked)
            OsmiumStatusPill(
              label: '已锁定',
              background: OsmiumColors.dangerBg(isDark),
              foreground: OsmiumColors.dangerFg(isDark),
            ),
        ],
      ),
    );
  }
}
