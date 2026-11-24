import 'dart:async';
import 'dart:io';

import 'package:file_picker/file_picker.dart';
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
import '../auth/captcha_webview_page.dart';
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
  final TextEditingController _nameCtrl = TextEditingController();
  final TextEditingController _nicknameCtrl = TextEditingController();
  final TextEditingController _emailPasswordCtrl = TextEditingController();
  final TextEditingController _newEmailCtrl = TextEditingController();
  final TextEditingController _emailCodeCtrl = TextEditingController();

  bool _scrolled = false;
  bool _accountMenuExpanded = false;
  bool _avatarBusy = false;
  String? _accountError;

  bool _nameVisible = false;
  bool _logoutVisible = false;

  bool _nicknameVisible = false;
  bool _nicknameSubmitting = false;
  String? _nicknameError;

  bool _emailVisible = false;
  bool _emailSubmitting = false;
  bool _emailSendingCode = false;
  bool _obscureEmailPassword = true;
  int _emailCodeCooldown = 0;
  Timer? _emailCooldownTimer;
  String? _emailError;

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
    _emailCooldownTimer?.cancel();
    _nameCtrl.dispose();
    _nicknameCtrl.dispose();
    _emailPasswordCtrl.dispose();
    _newEmailCtrl.dispose();
    _emailCodeCtrl.dispose();
    super.dispose();
  }

  void _syncDock() {
    _dockHidden?.value =
        _nameVisible || _logoutVisible || _nicknameVisible || _emailVisible;
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

  void _toggleAccountMenu() {
    OsmiumHaptics.selection();
    setState(() {
      _accountMenuExpanded = !_accountMenuExpanded;
      if (!_accountMenuExpanded) _accountError = null;
    });
  }

  Future<void> _pickAndUploadAvatar() async {
    if (_avatarBusy) return;
    OsmiumHaptics.selection();
    setState(() {
      _avatarBusy = true;
      _accountError = null;
    });
    try {
      final result = await FilePicker.platform.pickFiles(
        type: FileType.image,
        withData: true,
      );
      if (!mounted || result == null || result.files.isEmpty) return;

      final picked = result.files.first;
      Uint8List? rawBytes = picked.bytes;
      if ((rawBytes == null || rawBytes.isEmpty) &&
          picked.path != null &&
          picked.path!.isNotEmpty) {
        rawBytes = await File(picked.path!).readAsBytes();
      }
      if (rawBytes == null || rawBytes.isEmpty) {
        if (mounted) setState(() => _accountError = '无法读取所选图片');
        return;
      }

      final dataUri = await ref
          .read(platformBridgeProvider)
          .compressAvatar(rawBytes);
      if (!mounted) return;
      if (dataUri == null || dataUri.isEmpty) {
        setState(() => _accountError = '图片处理失败，请更换其他图片');
        return;
      }

      final res = await ref
          .read(appControllerProvider)
          .updateAccountProfile(avatarData: dataUri);
      if (!mounted) return;
      if (res.ok) {
        OsmiumHaptics.confirm();
        setState(() {
          _accountError = null;
          _accountMenuExpanded = false;
        });
      } else {
        setState(
          () => _accountError = res.message.isNotEmpty ? res.message : '更新头像失败',
        );
      }
    } catch (_) {
      if (mounted) setState(() => _accountError = '头像上传失败');
    } finally {
      if (mounted) setState(() => _avatarBusy = false);
    }
  }

  Future<void> _clearAvatar() async {
    if (_avatarBusy) return;
    OsmiumHaptics.confirm();
    setState(() {
      _avatarBusy = true;
      _accountError = null;
    });
    try {
      final res = await ref
          .read(appControllerProvider)
          .updateAccountProfile(clearAvatar: true);
      if (!mounted) return;
      if (res.ok) {
        setState(() {
          _accountError = null;
          _accountMenuExpanded = false;
        });
      } else {
        setState(
          () => _accountError = res.message.isNotEmpty ? res.message : '移除头像失败',
        );
      }
    } finally {
      if (mounted) setState(() => _avatarBusy = false);
    }
  }

  void _openNicknameDialog(String currentName) {
    OsmiumHaptics.selection();
    _nicknameCtrl.text = currentName;
    _nicknameCtrl.selection = TextSelection(
      baseOffset: 0,
      extentOffset: currentName.length,
    );
    setState(() {
      _accountMenuExpanded = false;
      _nicknameError = null;
      _nicknameVisible = true;
    });
    _syncDock();
  }

  void _dismissNicknameDialog() {
    if (!_nicknameVisible) return;
    FocusScope.of(context).unfocus();
    setState(() {
      _nicknameVisible = false;
      _nicknameSubmitting = false;
      _nicknameError = null;
    });
    _syncDock();
  }

  Future<void> _saveNickname() async {
    if (_nicknameSubmitting) return;
    final trimmed = _nicknameCtrl.text.trim();
    if (trimmed.isEmpty) {
      setState(() => _nicknameError = '请输入昵称');
      return;
    }
    setState(() {
      _nicknameSubmitting = true;
      _nicknameError = null;
    });
    try {
      final res = await ref
          .read(appControllerProvider)
          .updateAccountProfile(username: trimmed);
      if (!mounted) return;
      if (res.ok) {
        OsmiumHaptics.confirm();
        _dismissNicknameDialog();
      } else {
        setState(
          () =>
              _nicknameError = res.message.isNotEmpty ? res.message : '修改昵称失败',
        );
      }
    } finally {
      if (mounted) setState(() => _nicknameSubmitting = false);
    }
  }

  void _openChangeEmailDialog() {
    OsmiumHaptics.selection();
    _emailPasswordCtrl.clear();
    _newEmailCtrl.clear();
    _emailCodeCtrl.clear();
    setState(() {
      _accountMenuExpanded = false;
      _emailError = null;
      _obscureEmailPassword = true;
      _emailVisible = true;
    });
    _syncDock();
  }

  void _dismissChangeEmailDialog() {
    if (!_emailVisible) return;
    FocusScope.of(context).unfocus();
    setState(() {
      _emailVisible = false;
      _emailSubmitting = false;
      _emailSendingCode = false;
      _emailError = null;
    });
    _syncDock();
  }

  void _startEmailCooldown() {
    _emailCooldownTimer?.cancel();
    setState(() => _emailCodeCooldown = 60);
    _emailCooldownTimer = Timer.periodic(const Duration(seconds: 1), (timer) {
      if (!mounted) {
        timer.cancel();
        return;
      }
      if (_emailCodeCooldown <= 1) {
        timer.cancel();
        setState(() => _emailCodeCooldown = 0);
      } else {
        setState(() => _emailCodeCooldown -= 1);
      }
    });
  }

  Future<void> _sendChangeEmailCode() async {
    if (_emailSendingCode || _emailCodeCooldown > 0) return;
    final password = _emailPasswordCtrl.text;
    final newEmail = _newEmailCtrl.text.trim();
    if (password.isEmpty) {
      setState(() => _emailError = '请输入当前密码');
      return;
    }
    if (newEmail.isEmpty || !newEmail.contains('@')) {
      setState(() => _emailError = '请输入有效的新邮箱');
      return;
    }
    setState(() {
      _emailSendingCode = true;
      _emailError = null;
    });
    try {
      final app = ref.read(appControllerProvider);
      final res = await app.startChangeEmailSendCode(
        password: password,
        newEmail: newEmail,
      );
      if (!mounted) return;
      if (res.needsCaptcha && res.challenge != null) {
        final verified = await Navigator.of(context).push<Map<String, dynamic>>(
          MaterialPageRoute(
            builder: (_) => CaptchaWebViewPage(
              challenge: res.challenge!,
              accountService: ref.read(accountServiceProvider),
            ),
          ),
        );
        if (!mounted) return;
        if (verified != null) {
          _startEmailCooldown();
        }
        return;
      }
      if (res.ok) {
        _startEmailCooldown();
      } else {
        setState(
          () => _emailError = res.message.isNotEmpty ? res.message : '发送失败',
        );
      }
    } finally {
      if (mounted) setState(() => _emailSendingCode = false);
    }
  }

  Future<void> _confirmChangeEmail() async {
    if (_emailSubmitting) return;
    final password = _emailPasswordCtrl.text;
    final newEmail = _newEmailCtrl.text.trim();
    final code = _emailCodeCtrl.text.trim();
    if (password.isEmpty) {
      setState(() => _emailError = '请输入当前密码');
      return;
    }
    if (newEmail.isEmpty || !newEmail.contains('@')) {
      setState(() => _emailError = '请输入有效的新邮箱');
      return;
    }
    if (code.isEmpty) {
      setState(() => _emailError = '请输入验证码');
      return;
    }
    setState(() {
      _emailSubmitting = true;
      _emailError = null;
    });
    try {
      final res = await ref
          .read(appControllerProvider)
          .confirmChangeEmail(
            password: password,
            newEmail: newEmail,
            code: code,
          );
      if (!mounted) return;
      if (res.ok) {
        OsmiumHaptics.confirm();
        _dismissChangeEmailDialog();
      } else {
        setState(
          () => _emailError = res.message.isNotEmpty ? res.message : '更换邮箱失败',
        );
      }
    } finally {
      if (mounted) setState(() => _emailSubmitting = false);
    }
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
    setState(() {
      _accountMenuExpanded = false;
      _accountError = null;
    });
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
    final topPad = MediaQuery.paddingOf(context).top;
    final headerHeight = topPad + OsmiumTopHeader.barHeight;
    final keyboardInset = MediaQuery.viewInsetsOf(context).bottom;
    final scrollBottom = keyboardInset > 0 ? 20.0 : widget.bottomInset + 24;

    return MiuixScaffold(
      content: (_) {
        return Stack(
          children: [
            Padding(
              padding: EdgeInsets.only(bottom: keyboardInset),
              child: NotificationListener<ScrollNotification>(
                onNotification: (n) {
                  if (n.metrics.axis == Axis.vertical) {
                    final next = n.metrics.pixels > 4;
                    if (next != _scrolled) {
                      setState(() => _scrolled = next);
                    }
                  }
                  return false;
                },
                child: MiuixPullToRefresh(
                  isRefreshing: false,
                  onRefresh: () async {
                    if (app.isLoggedIn) {
                      await app.refreshAccountProfile();
                    }
                  },
                  color: colors.primary,
                  contentPadding: EdgeInsets.only(top: headerHeight + 14),
                  refreshTexts: const ['下拉刷新', '松开刷新', '刷新中…', '已刷新'],
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
                          headerHeight + 8,
                          16,
                          scrollBottom,
                        ),
                        children: [
                          _AccountHeroCard(
                            app: app,
                            expanded: _accountMenuExpanded,
                            avatarBusy: _avatarBusy,
                            errorText: _accountError,
                            onLogin: _openLogin,
                            onToggleExpand: _toggleAccountMenu,
                            onPickAvatar: _pickAndUploadAvatar,
                            onClearAvatar: _clearAvatar,
                            onEditNickname: () => _openNicknameDialog(
                              app.account?.username ?? '',
                            ),
                            onChangeEmail: _openChangeEmailDialog,
                          ),
                          const OsmiumSectionHeader('本机'),
                          OsmiumSurfaceCard(
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
                          OsmiumSurfaceCard(
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
                          OsmiumSurfaceCard(
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
                                    _update(
                                      s.copyWith(desiredFps: _fpsList[i]),
                                    );
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
                          OsmiumSurfaceCard(
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
                          OsmiumSurfaceCard(
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
                                      : (s.themePreference ==
                                                ThemePreference.dark
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
                              minHeight: 44,
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
            ),
            Positioned(
              top: 0,
              left: 0,
              right: 0,
              child: OsmiumTopHeader(
                title: '我的',
                scrolled: _scrolled,
                actions: [
                  if (app.isLoggedIn)
                    MiuixIconButton(
                      onPressed: () {
                        OsmiumHaptics.selection();
                        Navigator.of(context).push<bool>(
                          MaterialPageRoute(
                            builder: (_) => const QrScannerPage(),
                          ),
                        );
                      },
                      minWidth: 40,
                      minHeight: 40,
                      child: Icon(
                        Icons.qr_code_scanner_rounded,
                        size: 21,
                        color: colors.onSurface,
                        semanticLabel: '扫码登录',
                      ),
                    ),
                ],
              ),
            ),
            _buildSelfNameDialog(context),
            _buildNicknameDialog(context),
            _buildChangeEmailDialog(context),
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
              cornerRadius: OsmiumFieldDefaults.cornerRadius,
              insideMargin: OsmiumFieldDefaults.floatingMargin,
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
                  child: MiuixTextButton(
                    '取消',
                    onPressed: _dismissSelfName,
                  ),
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

  Widget _buildNicknameDialog(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final isDark = theme.brightness == Brightness.dark;

    return MiuixOverlayDialog(
      show: _nicknameVisible,
      title: '修改昵称',
      onDismissRequest: _dismissNicknameDialog,
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            MiuixTextField(
              cornerRadius: OsmiumFieldDefaults.cornerRadius,
              insideMargin: OsmiumFieldDefaults.floatingMargin,
              controller: _nicknameCtrl,
              label: '昵称',
              singleLine: true,
              autofocus: true,
              onSubmitted: (_) => _saveNickname(),
            ),
            OsmiumAnimatedReveal(
              visible: _nicknameError != null,
              child: Padding(
                padding: const EdgeInsets.only(top: 10),
                child: Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 12,
                    vertical: 8,
                  ),
                  decoration: BoxDecoration(
                    color: OsmiumColors.dangerBg(isDark),
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: MiuixText(
                    _nicknameError ?? '',
                    style: theme.textStyles.body2,
                    color: OsmiumColors.dangerFg(isDark),
                  ),
                ),
              ),
            ),
            const SizedBox(height: 18),
            Row(
              children: [
                Expanded(
                  child: MiuixTextButton(
                    '取消',
                    onPressed: _dismissNicknameDialog,
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: MiuixTextButton(
                    _nicknameSubmitting ? '保存中…' : '保存',
                    enabled: !_nicknameSubmitting,
                    onPressed: _saveNickname,
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

  Widget _buildChangeEmailDialog(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;

    return MiuixOverlayDialog(
      show: _emailVisible,
      title: '更换绑定邮箱',
      onDismissRequest: _dismissChangeEmailDialog,
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            MiuixTextField(
              cornerRadius: OsmiumFieldDefaults.cornerRadius,
              insideMargin: OsmiumFieldDefaults.floatingMargin,
              controller: _emailPasswordCtrl,
              label: '当前密码',
              singleLine: true,
              obscureText: _obscureEmailPassword,
              textInputAction: TextInputAction.next,
              trailingIcon: MiuixIconButton(
                onPressed: () {
                  OsmiumHaptics.selection();
                  setState(
                    () => _obscureEmailPassword = !_obscureEmailPassword,
                  );
                },
                minWidth: 34,
                minHeight: 34,
                child: Icon(
                  _obscureEmailPassword
                      ? Icons.visibility_off_outlined
                      : Icons.visibility_outlined,
                  size: 18,
                  color: colors.onSurfaceVariantSummary,
                ),
              ),
            ),
            const SizedBox(height: 12),
            MiuixTextField(
              cornerRadius: OsmiumFieldDefaults.cornerRadius,
              insideMargin: OsmiumFieldDefaults.floatingMargin,
              controller: _newEmailCtrl,
              label: '新邮箱',
              singleLine: true,
              keyboardType: TextInputType.emailAddress,
              textInputAction: TextInputAction.next,
            ),
            const SizedBox(height: 12),
            Row(
              children: [
                Expanded(
                  child: MiuixTextField(
                    cornerRadius: OsmiumFieldDefaults.cornerRadius,
                    insideMargin: OsmiumFieldDefaults.floatingMargin,
                    controller: _emailCodeCtrl,
                    label: '验证码',
                    singleLine: true,
                    keyboardType: TextInputType.number,
                    textInputAction: TextInputAction.done,
                    onSubmitted: (_) => _confirmChangeEmail(),
                  ),
                ),
                const SizedBox(width: 8),
                MiuixTextButton(
                  _emailCodeCooldown > 0
                      ? '${_emailCodeCooldown}s'
                      : (_emailSendingCode ? '…' : '获取'),
                  enabled: !_emailSendingCode && _emailCodeCooldown == 0,
                  onPressed: _sendChangeEmailCode,
                  minHeight: 44,
                ),
              ],
            ),
            OsmiumAnimatedReveal(
              visible: _emailError != null,
              child: Padding(
                padding: const EdgeInsets.only(top: 10),
                child: Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 12,
                    vertical: 8,
                  ),
                  decoration: BoxDecoration(
                    color: OsmiumColors.dangerBg(isDark),
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: MiuixText(
                    _emailError ?? '',
                    style: theme.textStyles.body2,
                    color: OsmiumColors.dangerFg(isDark),
                  ),
                ),
              ),
            ),
            const SizedBox(height: 18),
            Row(
              children: [
                Expanded(
                  child: MiuixTextButton(
                    '取消',
                    onPressed: _dismissChangeEmailDialog,
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: MiuixTextButton(
                    _emailSubmitting ? '提交中…' : '确认',
                    enabled: !_emailSubmitting,
                    onPressed: _confirmChangeEmail,
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
          Expanded(
            child: MiuixTextButton(
              '取消',
              onPressed: _closeLogout,
            ),
          ),
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
  const _AccountHeroCard({
    required this.app,
    required this.expanded,
    required this.avatarBusy,
    required this.errorText,
    required this.onLogin,
    required this.onToggleExpand,
    required this.onPickAvatar,
    required this.onClearAvatar,
    required this.onEditNickname,
    required this.onChangeEmail,
  });

  final AppController app;
  final bool expanded;
  final bool avatarBusy;
  final String? errorText;
  final VoidCallback onLogin;
  final VoidCallback onToggleExpand;
  final VoidCallback onPickAvatar;
  final VoidCallback onClearAvatar;
  final VoidCallback onEditNickname;
  final VoidCallback onChangeEmail;

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final isDark = theme.brightness == Brightness.dark;
    final acct = app.account;

    if (!app.isLoggedIn || acct == null) {
      return OsmiumSurfaceCard(
        onPressed: onLogin,
        child: SizedBox(
          height: 112,
          child: Stack(
            fit: StackFit.expand,
            children: [
              OsmiumWaveBackdrop(isDark: isDark),
              Padding(
                padding: const EdgeInsets.fromLTRB(18, 16, 16, 16),
                child: Row(
                  children: [
                    Container(
                      width: 48,
                      height: 48,
                      padding: const EdgeInsets.all(6),
                      decoration: ShapeDecoration(
                        color: Colors.white.withValues(alpha: 0.22),
                        shape: MiuixSquircleBorder(cornerRadius: 15),
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
                              fontSize: 20,
                              fontWeight: FontWeight.w700,
                              height: 1.2,
                              color: Colors.white,
                            ),
                          ),
                          const SizedBox(height: 3),
                          Text(
                            '登录账号',
                            style: TextStyle(
                              fontFamily: OsmiumTheme.fontFamily,
                              fontSize: 13,
                              fontWeight: FontWeight.w500,
                              color: Colors.white.withValues(alpha: 0.88),
                            ),
                          ),
                        ],
                      ),
                    ),
                    Icon(
                      Icons.chevron_right_rounded,
                      color: Colors.white.withValues(alpha: 0.9),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      );
    }

    return _LoggedInAccountCard(
      acct: acct,
      isDark: isDark,
      expanded: expanded,
      avatarBusy: avatarBusy,
      errorText: errorText,
      onToggleExpand: onToggleExpand,
      onPickAvatar: onPickAvatar,
      onClearAvatar: onClearAvatar,
      onEditNickname: onEditNickname,
      onChangeEmail: onChangeEmail,
    );
  }
}

class _LoggedInAccountCard extends StatelessWidget {
  const _LoggedInAccountCard({
    required this.acct,
    required this.isDark,
    required this.expanded,
    required this.avatarBusy,
    required this.errorText,
    required this.onToggleExpand,
    required this.onPickAvatar,
    required this.onClearAvatar,
    required this.onEditNickname,
    required this.onChangeEmail,
  });

  final AccountSessionData acct;
  final bool isDark;
  final bool expanded;
  final bool avatarBusy;
  final String? errorText;
  final VoidCallback onToggleExpand;
  final VoidCallback onPickAvatar;
  final VoidCallback onClearAvatar;
  final VoidCallback onEditNickname;
  final VoidCallback onChangeEmail;

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final avatarBytes = decodeAvatarDataUri(acct.avatarData);
    final displayName = acct.username.isNotEmpty ? acct.username : acct.email;
    final showEmail = acct.email.isNotEmpty && acct.email != displayName;

    return OsmiumSurfaceCard(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          MiuixPressable(
            onPressed: onToggleExpand,
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 16, 14, 16),
              child: Row(
                children: [
                  if (avatarBytes != null) ...[
                    ClipOval(
                      child: Image.memory(
                        avatarBytes,
                        width: 48,
                        height: 48,
                        fit: BoxFit.cover,
                        gaplessPlayback: true,
                      ),
                    ),
                    const SizedBox(width: 14),
                  ],
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        MiuixText(
                          displayName,
                          style: theme.textStyles.headline1,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                        ),
                        if (showEmail) ...[
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
                  if (acct.locked) ...[
                    const SizedBox(width: 8),
                    OsmiumStatusPill(
                      label: '已锁定',
                      background: OsmiumColors.dangerBg(isDark),
                      foreground: OsmiumColors.dangerFg(isDark),
                    ),
                  ],
                  if (avatarBusy) ...[
                    const SizedBox(width: 8),
                    SizedBox(
                      width: 16,
                      height: 16,
                      child: CircularProgressIndicator(
                        strokeWidth: 2,
                        color: colors.primary,
                      ),
                    ),
                  ],
                  const SizedBox(width: 4),
                  AnimatedRotation(
                    turns: expanded ? 0.5 : 0,
                    duration: OsmiumMotion.durationOr(
                      context,
                      OsmiumMotion.small,
                    ),
                    curve: MiuixMotion.standardDecelerate,
                    child: Icon(
                      Icons.keyboard_arrow_down_rounded,
                      size: 22,
                      color: colors.onSurfaceVariantSummary,
                    ),
                  ),
                ],
              ),
            ),
          ),
          OsmiumAnimatedReveal(
            visible: errorText != null,
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 12),
              child: Container(
                padding: const EdgeInsets.symmetric(
                  horizontal: 12,
                  vertical: 8,
                ),
                decoration: BoxDecoration(
                  color: OsmiumColors.dangerBg(isDark),
                  borderRadius: BorderRadius.circular(12),
                ),
                child: MiuixText(
                  errorText ?? '',
                  style: theme.textStyles.body2,
                  color: OsmiumColors.dangerFg(isDark),
                ),
              ),
            ),
          ),
          AnimatedSize(
            duration: OsmiumMotion.durationOr(context, OsmiumMotion.tabSwitch),
            curve: MiuixMotion.standardDecelerate,
            alignment: Alignment.topCenter,
            child: expanded
                ? Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      const OsmiumCardDivider(),
                      _AccountDropdownItem(
                        icon: Icons.photo_camera_outlined,
                        title: '修改头像',
                        enabled: !avatarBusy,
                        onTap: onPickAvatar,
                      ),
                      if (avatarBytes != null) ...[
                        const OsmiumCardDivider(),
                        _AccountDropdownItem(
                          icon: Icons.hide_image_outlined,
                          title: '移除头像',
                          enabled: !avatarBusy,
                          onTap: onClearAvatar,
                        ),
                      ],
                      const OsmiumCardDivider(),
                      _AccountDropdownItem(
                        icon: Icons.badge_outlined,
                        title: '修改昵称',
                        onTap: onEditNickname,
                      ),
                      const OsmiumCardDivider(),
                      _AccountDropdownItem(
                        icon: Icons.mail_outline_rounded,
                        title: '更换绑定邮箱',
                        onTap: onChangeEmail,
                      ),
                    ],
                  )
                : const SizedBox.shrink(),
          ),
        ],
      ),
    );
  }
}

class _AccountDropdownItem extends StatelessWidget {
  const _AccountDropdownItem({
    required this.icon,
    required this.title,
    required this.onTap,
    this.enabled = true,
  });

  final IconData icon;
  final String title;
  final VoidCallback onTap;
  final bool enabled;

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;

    return MiuixPressable(
      onPressed: enabled ? onTap : null,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 13),
        child: Row(
          children: [
            Icon(
              icon,
              size: 19,
              color: enabled
                  ? colors.onSurfaceVariantSummary
                  : colors.disabledOnSecondaryVariant,
            ),
            const SizedBox(width: 12),
            Expanded(
              child: MiuixText(
                title,
                style: theme.textStyles.body1,
                color: enabled
                    ? colors.onSurface
                    : colors.disabledOnSecondaryVariant,
              ),
            ),
            Icon(
              Icons.chevron_right_rounded,
              size: 19,
              color: colors.onSurfaceVariantSummary.withValues(alpha: 0.7),
            ),
          ],
        ),
      ),
    );
  }
}
