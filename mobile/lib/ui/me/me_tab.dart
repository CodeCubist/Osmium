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
import '../widgets/social_brand_icons.dart';

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
  final TextEditingController _setPwdCodeCtrl = TextEditingController();
  final TextEditingController _setPwdNewCtrl = TextEditingController();
  final TextEditingController _setPwdConfirmCtrl = TextEditingController();

  bool _scrolled = false;
  bool _accountMenuExpanded = false;
  bool _avatarBusy = false;
  String? _accountError;
  String? _accountSuccess;
  Timer? _accountSuccessTimer;

  bool _oauthBindBusy = false;
  bool _oauthBindDialogVisible = false;
  bool _oauthBindPolling = false;
  String _oauthBindProvider = '';
  OAuthStartData? _oauthBindSession;
  Timer? _oauthBindPollTimer;
  StreamSubscription<String>? _oauthBindDeepLinkSub;

  bool _oauthUnbindVisible = false;
  bool _oauthUnbindSubmitting = false;
  String _oauthUnbindProvider = '';

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

  bool _setPwdVisible = false;
  bool _setPwdSubmitting = false;
  bool _setPwdSendingCode = false;
  bool _obscureSetPwd = true;
  bool _obscureSetPwdConfirm = true;
  int _setPwdCodeCooldown = 0;
  Timer? _setPwdCooldownTimer;
  String? _setPwdError;

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
    _stopOAuthBindPolling(cancelRemote: true);
    _accountSuccessTimer?.cancel();
    _copiedTimer?.cancel();
    _emailCooldownTimer?.cancel();
    _setPwdCooldownTimer?.cancel();
    _nameCtrl.dispose();
    _nicknameCtrl.dispose();
    _emailPasswordCtrl.dispose();
    _newEmailCtrl.dispose();
    _emailCodeCtrl.dispose();
    _setPwdCodeCtrl.dispose();
    _setPwdNewCtrl.dispose();
    _setPwdConfirmCtrl.dispose();
    super.dispose();
  }

  void _syncDock() {
    _dockHidden?.value =
        _nameVisible ||
        _logoutVisible ||
        _nicknameVisible ||
        _emailVisible ||
        _setPwdVisible ||
        _oauthBindDialogVisible ||
        _oauthUnbindVisible;
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
    final nextExpanded = !_accountMenuExpanded;
    setState(() {
      _accountMenuExpanded = nextExpanded;
      if (!_accountMenuExpanded) {
        _accountError = null;
        _accountSuccess = null;
      }
    });
    if (nextExpanded) {
      final app = ref.read(appControllerProvider);
      if (app.isLoggedIn && app.oauthBindings == null) {
        unawaited(app.refreshOAuthBindings());
      }
    }
  }

  String _oauthProviderLabel(String provider) {
    switch (provider) {
      case 'google':
        return 'Google';
      case 'qq':
        return 'QQ';
      case 'wechat':
        return '微信';
      default:
        return provider;
    }
  }

  void _showAccountSuccess(String message) {
    _accountSuccessTimer?.cancel();
    setState(() {
      _accountError = null;
      _accountSuccess = message;
    });
    _accountSuccessTimer = Timer(const Duration(seconds: 4), () {
      if (mounted) {
        setState(() => _accountSuccess = null);
      }
    });
  }

  void _stopOAuthBindPolling({required bool cancelRemote}) {
    _oauthBindPollTimer?.cancel();
    _oauthBindPollTimer = null;
    _oauthBindDeepLinkSub?.cancel();
    _oauthBindDeepLinkSub = null;
    _oauthBindPolling = false;
    final sess = _oauthBindSession;
    _oauthBindSession = null;
    if (cancelRemote && sess != null) {
      unawaited(
        ref
            .read(accountServiceProvider)
            .cancelOAuthLogin(state: sess.state, pollSecret: sess.pollSecret),
      );
    }
  }

  void _cancelOAuthBindFlow() {
    _stopOAuthBindPolling(cancelRemote: true);
    if (mounted) {
      setState(() {
        _oauthBindDialogVisible = false;
        _oauthBindBusy = false;
      });
      _syncDock();
    }
  }

  void _handleOAuthBindingTap(String provider) {
    if (_oauthBindBusy || _oauthUnbindSubmitting) return;
    final app = ref.read(appControllerProvider);
    final acct = app.account;
    final bindings = app.oauthBindings;
    if (!app.isLoggedIn ||
        acct == null ||
        acct.locked ||
        !app.isProfileComplete ||
        bindings == null) {
      return;
    }
    if (bindings.isBound(provider)) {
      _openOAuthUnbindDialog(provider);
    } else if (bindings.isAvailable(provider)) {
      unawaited(_startOAuthBind(provider));
    }
  }

  Future<void> _startOAuthBind(String provider) async {
    if (_oauthBindBusy) return;
    OsmiumHaptics.selection();
    setState(() {
      _oauthBindBusy = true;
      _oauthBindProvider = provider;
      _accountError = null;
      _accountSuccess = null;
    });

    final res = await ref.read(appControllerProvider).startOAuthBind(provider);
    if (!mounted) return;
    if (!res.ok || res.data == null || res.data!.authorizeUrl.isEmpty) {
      setState(() {
        _oauthBindBusy = false;
        _accountError = res.message.isNotEmpty ? res.message : '发起绑定失败，请重试';
      });
      return;
    }

    final session = res.data!;
    _stopOAuthBindPolling(cancelRemote: false);
    _oauthBindSession = session;

    final bridge = ref.read(platformBridgeProvider);
    final opened = await bridge.openExternalUrl(session.authorizeUrl);
    if (!mounted) return;
    if (!opened) {
      _stopOAuthBindPolling(cancelRemote: true);
      setState(() {
        _oauthBindBusy = false;
        _accountError = '未能打开浏览器，请检查系统默认浏览器设置';
      });
      return;
    }

    setState(() {
      _oauthBindDialogVisible = true;
    });
    _syncDock();

    _oauthBindDeepLinkSub = bridge.deepLinks.listen((link) {
      if (link.toLowerCase().startsWith('osmium://oauth-done')) {
        unawaited(_pollOAuthBindOnce());
      }
    });

    final intervalMs = session.pollIntervalMs.clamp(1000, 3000);
    _oauthBindPollTimer = Timer.periodic(
      Duration(milliseconds: intervalMs),
      (_) => unawaited(_pollOAuthBindOnce()),
    );
  }

  Future<void> _pollOAuthBindOnce() async {
    final session = _oauthBindSession;
    if (session == null || _oauthBindPolling || !mounted) return;
    _oauthBindPolling = true;
    try {
      final accountSvc = ref.read(accountServiceProvider);
      final res = await accountSvc.pollOAuthStatus(
        pollUrl: session.pollUrl,
        pollSecret: session.pollSecret,
      );
      if (!mounted || _oauthBindSession?.state != session.state) return;

      if (res.ok && res.data != null) {
        if (res.data!.state == 'completed' && res.data!.bound) {
          _stopOAuthBindPolling(cancelRemote: false);
          final label = _oauthProviderLabel(_oauthBindProvider);
          await ref.read(appControllerProvider).refreshOAuthBindings();
          if (!mounted) return;
          setState(() {
            _oauthBindDialogVisible = false;
            _oauthBindBusy = false;
          });
          _syncDock();
          OsmiumHaptics.confirm();
          _showAccountSuccess('已绑定$label账号，之后可直接使用$label登录');
        }
        return;
      }

      if (res.httpStatusCode > 0) {
        _stopOAuthBindPolling(cancelRemote: false);
        unawaited(ref.read(appControllerProvider).refreshOAuthBindings());
        setState(() {
          _oauthBindDialogVisible = false;
          _oauthBindBusy = false;
          _accountError = res.message.isNotEmpty ? res.message : '账号绑定未完成，请重试';
        });
        _syncDock();
      }
    } finally {
      _oauthBindPolling = false;
    }
  }

  void _openOAuthUnbindDialog(String provider) {
    OsmiumHaptics.selection();
    setState(() {
      _oauthUnbindProvider = provider;
      _oauthUnbindSubmitting = false;
      _oauthUnbindVisible = true;
    });
    _syncDock();
  }

  void _dismissOAuthUnbindDialog() {
    if (!_oauthUnbindVisible || _oauthUnbindSubmitting) return;
    setState(() {
      _oauthUnbindVisible = false;
    });
    _syncDock();
  }

  Future<void> _confirmOAuthUnbind() async {
    if (_oauthUnbindSubmitting) return;
    final provider = _oauthUnbindProvider;
    final label = _oauthProviderLabel(provider);
    setState(() => _oauthUnbindSubmitting = true);
    try {
      final res = await ref
          .read(appControllerProvider)
          .unbindOAuthProvider(provider);
      if (!mounted) return;
      setState(() {
        _oauthUnbindSubmitting = false;
        _oauthUnbindVisible = false;
      });
      _syncDock();
      if (res.ok) {
        OsmiumHaptics.confirm();
        _showAccountSuccess('已解除$label账号绑定');
      } else {
        setState(
          () => _accountError = res.message.isNotEmpty ? res.message : '解除绑定失败',
        );
      }
    } catch (_) {
      if (mounted) {
        setState(() {
          _oauthUnbindSubmitting = false;
          _oauthUnbindVisible = false;
          _accountError = '解除绑定失败';
        });
        _syncDock();
      }
    }
  }

  Future<void> _pickAndUploadAvatar() async {
    if (_avatarBusy) return;
    if (!ref.read(appControllerProvider).isProfileComplete) return;
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
    if (!ref.read(appControllerProvider).isProfileComplete) return;
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
    if (!ref.read(appControllerProvider).isProfileComplete) return;
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
    final acct = ref.read(appControllerProvider).account;
    if (acct != null && acct.hasEmail && !acct.hasPassword) return;
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
    final app = ref.read(appControllerProvider);
    final acct = app.account;
    final isAddEmail = acct != null && !acct.hasEmail && !acct.hasPassword;
    final password = isAddEmail ? null : _emailPasswordCtrl.text;
    final newEmail = _newEmailCtrl.text.trim();
    if (!isAddEmail && (password == null || password.isEmpty)) {
      setState(() => _emailError = '请输入当前密码');
      return;
    }
    if (newEmail.isEmpty || !newEmail.contains('@')) {
      setState(() => _emailError = isAddEmail ? '请输入有效的邮箱地址' : '请输入有效的新邮箱');
      return;
    }
    setState(() {
      _emailSendingCode = true;
      _emailError = null;
    });
    try {
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
    final app = ref.read(appControllerProvider);
    final acct = app.account;
    final isAddEmail = acct != null && !acct.hasEmail && !acct.hasPassword;
    final password = isAddEmail ? null : _emailPasswordCtrl.text;
    final newEmail = _newEmailCtrl.text.trim();
    final code = _emailCodeCtrl.text.trim();
    if (!isAddEmail && (password == null || password.isEmpty)) {
      setState(() => _emailError = '请输入当前密码');
      return;
    }
    if (newEmail.isEmpty || !newEmail.contains('@')) {
      setState(() => _emailError = isAddEmail ? '请输入有效的邮箱地址' : '请输入有效的新邮箱');
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
      final res = await app.confirmChangeEmail(
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
          () => _emailError = res.message.isNotEmpty
              ? res.message
              : (isAddEmail ? '绑定邮箱失败' : '更换邮箱失败'),
        );
      }
    } finally {
      if (mounted) setState(() => _emailSubmitting = false);
    }
  }

  void _openSetPasswordDialog() {
    final acct = ref.read(appControllerProvider).account;
    if (acct == null || !acct.hasEmail || acct.hasPassword) return;
    OsmiumHaptics.selection();
    _setPwdCodeCtrl.clear();
    _setPwdNewCtrl.clear();
    _setPwdConfirmCtrl.clear();
    setState(() {
      _accountMenuExpanded = false;
      _setPwdError = null;
      _obscureSetPwd = true;
      _obscureSetPwdConfirm = true;
      _setPwdVisible = true;
    });
    _syncDock();
  }

  void _dismissSetPasswordDialog() {
    if (!_setPwdVisible) return;
    FocusScope.of(context).unfocus();
    setState(() {
      _setPwdVisible = false;
      _setPwdSubmitting = false;
      _setPwdSendingCode = false;
      _setPwdError = null;
    });
    _syncDock();
  }

  void _startSetPwdCooldown() {
    _setPwdCooldownTimer?.cancel();
    setState(() => _setPwdCodeCooldown = 60);
    _setPwdCooldownTimer = Timer.periodic(const Duration(seconds: 1), (timer) {
      if (!mounted) {
        timer.cancel();
        return;
      }
      if (_setPwdCodeCooldown <= 1) {
        timer.cancel();
        setState(() => _setPwdCodeCooldown = 0);
      } else {
        setState(() => _setPwdCodeCooldown -= 1);
      }
    });
  }

  Future<void> _sendSetPasswordCode() async {
    if (_setPwdSendingCode || _setPwdCodeCooldown > 0) return;
    setState(() {
      _setPwdSendingCode = true;
      _setPwdError = null;
    });
    try {
      final res = await ref
          .read(appControllerProvider)
          .startSetPasswordSendCode();
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
          _startSetPwdCooldown();
        }
        return;
      }
      if (res.ok) {
        _startSetPwdCooldown();
      } else {
        setState(
          () => _setPwdError = res.message.isNotEmpty ? res.message : '发送失败',
        );
      }
    } finally {
      if (mounted) setState(() => _setPwdSendingCode = false);
    }
  }

  Future<void> _confirmSetPassword() async {
    if (_setPwdSubmitting) return;
    final code = _setPwdCodeCtrl.text.trim();
    final newPwd = _setPwdNewCtrl.text;
    final confirmPwd = _setPwdConfirmCtrl.text;
    if (code.length != 6) {
      setState(() => _setPwdError = '请输入6位邮箱验证码');
      return;
    }
    if (newPwd.length < 8 || newPwd.length > 128) {
      setState(() => _setPwdError = '密码长度需在8至128位之间');
      return;
    }
    if (newPwd != confirmPwd) {
      setState(() => _setPwdError = '两次输入的密码不一致');
      return;
    }
    setState(() {
      _setPwdSubmitting = true;
      _setPwdError = null;
    });
    try {
      final res = await ref
          .read(appControllerProvider)
          .confirmSetPassword(code: code, newPassword: newPwd);
      if (!mounted) return;
      if (res.ok) {
        OsmiumHaptics.confirm();
        _dismissSetPasswordDialog();
      } else {
        setState(
          () => _setPwdError = res.message.isNotEmpty ? res.message : '设置密码失败',
        );
      }
    } finally {
      if (mounted) setState(() => _setPwdSubmitting = false);
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
    _stopOAuthBindPolling(cancelRemote: true);
    _accountSuccessTimer?.cancel();
    setState(() {
      _accountMenuExpanded = false;
      _accountError = null;
      _accountSuccess = null;
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
                            oauthBindBusy: _oauthBindBusy,
                            errorText: _accountError,
                            successText: _accountSuccess,
                            onLogin: _openLogin,
                            onToggleExpand: _toggleAccountMenu,
                            onPickAvatar: _pickAndUploadAvatar,
                            onClearAvatar: _clearAvatar,
                            onEditNickname: () => _openNicknameDialog(
                              app.account?.username ?? '',
                            ),
                            onChangeEmail: _openChangeEmailDialog,
                            onSetPassword: _openSetPasswordDialog,
                            onTapOAuthBinding: _handleOAuthBindingTap,
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
                                  title: '双指缩放',
                                  startAction: const OsmiumPrefIcon(
                                    Icons.pinch_outlined,
                                  ),
                                  value: s.pinchZoomEnabled,
                                  onChanged: (v) =>
                                      _update(s.copyWith(pinchZoomEnabled: v)),
                                ),
                                const OsmiumCardDivider(),
                                MiuixSwitchPreference(
                                  title: '自动唤起输入法',
                                  startAction: const OsmiumPrefIcon(
                                    Icons.keyboard_alt_outlined,
                                  ),
                                  value: s.autoShowIme,
                                  onChanged: (v) =>
                                      _update(s.copyWith(autoShowIme: v)),
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
            _buildChangeEmailDialog(context, app.account),
            _buildSetPasswordDialog(context, app.account),
            _buildLogoutDialog(context),
            _buildOAuthBindDialog(context),
            _buildOAuthUnbindDialog(context),
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

  Widget _buildChangeEmailDialog(BuildContext context, AccountSessionData? acct) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;
    final isAddEmail = acct != null && !acct.hasEmail && !acct.hasPassword;

    return MiuixOverlayDialog(
      show: _emailVisible,
      title: isAddEmail ? '绑定邮箱' : '更换绑定邮箱',
      onDismissRequest: _dismissChangeEmailDialog,
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (!isAddEmail) ...[
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
            ],
            MiuixTextField(
              cornerRadius: OsmiumFieldDefaults.cornerRadius,
              insideMargin: OsmiumFieldDefaults.floatingMargin,
              controller: _newEmailCtrl,
              label: isAddEmail ? '邮箱地址' : '新邮箱',
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

  Widget _buildSetPasswordDialog(BuildContext context, AccountSessionData? acct) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;
    final boundEmail = acct?.email ?? '';

    return MiuixOverlayDialog(
      show: _setPwdVisible,
      title: '设置登录密码',
      onDismissRequest: _dismissSetPasswordDialog,
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (boundEmail.isNotEmpty) ...[
              MiuixText(
                '将向$boundEmail发送6位验证码',
                style: theme.textStyles.body2,
                color: colors.onSurfaceVariantSummary,
              ),
              const SizedBox(height: 12),
            ],
            Row(
              children: [
                Expanded(
                  child: MiuixTextField(
                    cornerRadius: OsmiumFieldDefaults.cornerRadius,
                    insideMargin: OsmiumFieldDefaults.floatingMargin,
                    controller: _setPwdCodeCtrl,
                    label: '邮箱验证码',
                    singleLine: true,
                    keyboardType: TextInputType.number,
                    textInputAction: TextInputAction.next,
                  ),
                ),
                const SizedBox(width: 8),
                MiuixTextButton(
                  _setPwdCodeCooldown > 0
                      ? '${_setPwdCodeCooldown}s'
                      : (_setPwdSendingCode ? '…' : '获取'),
                  enabled: !_setPwdSendingCode && _setPwdCodeCooldown == 0,
                  onPressed: _sendSetPasswordCode,
                  minHeight: 44,
                ),
              ],
            ),
            const SizedBox(height: 12),
            MiuixTextField(
              cornerRadius: OsmiumFieldDefaults.cornerRadius,
              insideMargin: OsmiumFieldDefaults.floatingMargin,
              controller: _setPwdNewCtrl,
              label: '登录密码（8-128位）',
              singleLine: true,
              obscureText: _obscureSetPwd,
              textInputAction: TextInputAction.next,
              trailingIcon: MiuixIconButton(
                onPressed: () {
                  OsmiumHaptics.selection();
                  setState(() => _obscureSetPwd = !_obscureSetPwd);
                },
                minWidth: 34,
                minHeight: 34,
                child: Icon(
                  _obscureSetPwd
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
              controller: _setPwdConfirmCtrl,
              label: '确认登录密码',
              singleLine: true,
              obscureText: _obscureSetPwdConfirm,
              textInputAction: TextInputAction.done,
              onSubmitted: (_) => _confirmSetPassword(),
              trailingIcon: MiuixIconButton(
                onPressed: () {
                  OsmiumHaptics.selection();
                  setState(
                    () => _obscureSetPwdConfirm = !_obscureSetPwdConfirm,
                  );
                },
                minWidth: 34,
                minHeight: 34,
                child: Icon(
                  _obscureSetPwdConfirm
                      ? Icons.visibility_off_outlined
                      : Icons.visibility_outlined,
                  size: 18,
                  color: colors.onSurfaceVariantSummary,
                ),
              ),
            ),
            OsmiumAnimatedReveal(
              visible: _setPwdError != null,
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
                    _setPwdError ?? '',
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
                    onPressed: _dismissSetPasswordDialog,
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: MiuixTextButton(
                    _setPwdSubmitting ? '提交中…' : '确认',
                    enabled: !_setPwdSubmitting,
                    onPressed: _confirmSetPassword,
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

  Widget _buildOAuthBindDialog(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final label = _oauthProviderLabel(_oauthBindProvider);

    return MiuixOverlayDialog(
      show: _oauthBindDialogVisible,
      title: '绑定$label账号',
      onDismissRequest: _cancelOAuthBindFlow,
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Center(
            child: Padding(
              padding: const EdgeInsets.symmetric(vertical: 12),
              child: SizedBox(
                width: 28,
                height: 28,
                child: CircularProgressIndicator(
                  strokeWidth: 2.5,
                  color: colors.primary,
                ),
              ),
            ),
          ),
          MiuixText(
            '请在打开的浏览器中完成$label账号授权',
            style: theme.textStyles.body1,
            color: colors.onSurface,
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 6),
          MiuixText(
            '授权完成后将自动返回应用并完成绑定',
            style: theme.textStyles.body2,
            color: colors.onSurfaceVariantSummary,
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 18),
          Row(
            children: [
              Expanded(
                child: MiuixTextButton(
                  '取消',
                  onPressed: _cancelOAuthBindFlow,
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: MiuixTextButton(
                  '重新打开',
                  onPressed: () {
                    final url = _oauthBindSession?.authorizeUrl ?? '';
                    if (url.isNotEmpty) {
                      unawaited(
                        ref.read(platformBridgeProvider).openExternalUrl(url),
                      );
                    }
                  },
                  colors: MiuixButtonDefaults.buttonColorsPrimary(context),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }

  Widget _buildOAuthUnbindDialog(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;
    final label = _oauthProviderLabel(_oauthUnbindProvider);

    return MiuixOverlayDialog(
      show: _oauthUnbindVisible,
      title: '解除账号绑定',
      onDismissRequest: _dismissOAuthUnbindDialog,
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          MiuixText(
            '确定要解除$label账号的绑定吗？解除后将无法通过$label登录当前账号。',
            style: theme.textStyles.body2,
            color: colors.onSurfaceVariantSummary,
          ),
          const SizedBox(height: 18),
          Row(
            children: [
              Expanded(
                child: MiuixTextButton(
                  '取消',
                  enabled: !_oauthUnbindSubmitting,
                  onPressed: _dismissOAuthUnbindDialog,
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: MiuixTextButton(
                  _oauthUnbindSubmitting ? '处理中…' : '解绑',
                  enabled: !_oauthUnbindSubmitting,
                  onPressed: _confirmOAuthUnbind,
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
    required this.oauthBindBusy,
    required this.errorText,
    required this.successText,
    required this.onLogin,
    required this.onToggleExpand,
    required this.onPickAvatar,
    required this.onClearAvatar,
    required this.onEditNickname,
    required this.onChangeEmail,
    required this.onSetPassword,
    required this.onTapOAuthBinding,
  });

  final AppController app;
  final bool expanded;
  final bool avatarBusy;
  final bool oauthBindBusy;
  final String? errorText;
  final String? successText;
  final VoidCallback onLogin;
  final VoidCallback onToggleExpand;
  final VoidCallback onPickAvatar;
  final VoidCallback onClearAvatar;
  final VoidCallback onEditNickname;
  final VoidCallback onChangeEmail;
  final VoidCallback onSetPassword;
  final ValueChanged<String> onTapOAuthBinding;

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
      oauthBindings: app.oauthBindings,
      oauthBindingsLoading: app.oauthBindingsLoading,
      oauthBindingsFailed: app.oauthBindingsFailed,
      isDark: isDark,
      expanded: expanded,
      avatarBusy: avatarBusy,
      oauthBindBusy: oauthBindBusy,
      errorText: errorText,
      successText: successText,
      onToggleExpand: onToggleExpand,
      onPickAvatar: onPickAvatar,
      onClearAvatar: onClearAvatar,
      onEditNickname: onEditNickname,
      onChangeEmail: onChangeEmail,
      onSetPassword: onSetPassword,
      onTapOAuthBinding: onTapOAuthBinding,
      onRetryOAuthBindings: () => app.refreshOAuthBindings(),
    );
  }
}

class _LoggedInAccountCard extends StatelessWidget {
  const _LoggedInAccountCard({
    required this.acct,
    required this.oauthBindings,
    required this.oauthBindingsLoading,
    required this.oauthBindingsFailed,
    required this.isDark,
    required this.expanded,
    required this.avatarBusy,
    required this.oauthBindBusy,
    required this.errorText,
    required this.successText,
    required this.onToggleExpand,
    required this.onPickAvatar,
    required this.onClearAvatar,
    required this.onEditNickname,
    required this.onChangeEmail,
    required this.onSetPassword,
    required this.onTapOAuthBinding,
    required this.onRetryOAuthBindings,
  });

  final AccountSessionData acct;
  final OAuthBindingsData? oauthBindings;
  final bool oauthBindingsLoading;
  final bool oauthBindingsFailed;
  final bool isDark;
  final bool expanded;
  final bool avatarBusy;
  final bool oauthBindBusy;
  final String? errorText;
  final String? successText;
  final VoidCallback onToggleExpand;
  final VoidCallback onPickAvatar;
  final VoidCallback onClearAvatar;
  final VoidCallback onEditNickname;
  final VoidCallback onChangeEmail;
  final VoidCallback onSetPassword;
  final ValueChanged<String> onTapOAuthBinding;
  final VoidCallback onRetryOAuthBindings;

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final avatarBytes = decodeAvatarDataUri(acct.avatarData);
    final displayName = acct.username.isNotEmpty
        ? acct.username
        : (acct.hasEmail && acct.email.isNotEmpty ? acct.email : 'Osmium用户');
    final emailSubtitle = !acct.hasEmail
        ? '未绑定邮箱'
        : (acct.email.isNotEmpty && acct.email != displayName ? acct.email : '');
    final profileComplete = acct.isProfileComplete;
    final canManageEmail = !acct.hasEmail || profileComplete;
    final canSetPassword = acct.hasEmail && !acct.hasPassword;

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
                        if (emailSubtitle.isNotEmpty) ...[
                          const SizedBox(height: 2),
                          MiuixText(
                            emailSubtitle,
                            style: theme.textStyles.body2,
                            color: colors.onSurfaceVariantSummary,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                          ),
                        ],
                      ],
                    ),
                  ),
                  if (!profileComplete) ...[
                    const SizedBox(width: 8),
                    OsmiumStatusPill(
                      label: '待完善',
                      background: isDark
                          ? const Color(0x33F59E0B)
                          : const Color(0x22D97706),
                      foreground: isDark
                          ? const Color(0xFFFBBF24)
                          : const Color(0xFFB45309),
                    ),
                  ],
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
          if (!profileComplete)
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 12),
              child: Container(
                padding: const EdgeInsets.symmetric(
                  horizontal: 12,
                  vertical: 9,
                ),
                decoration: BoxDecoration(
                  color: isDark
                      ? const Color(0x26F59E0B)
                      : const Color(0x1AD97706),
                  borderRadius: BorderRadius.circular(12),
                ),
                child: MiuixText(
                  !acct.hasEmail
                      ? '为了保障账号安全，请先绑定邮箱并设置登录密码。完成后即可使用远程开关机及修改个人资料。'
                      : '为了保障账号安全，请先设置登录密码。设置完成后即可使用远程开关机及修改个人资料。',
                  style: theme.textStyles.body2,
                  color: isDark
                      ? const Color(0xFFFBBF24)
                      : const Color(0xFFB45309),
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
          OsmiumAnimatedReveal(
            visible: successText != null,
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 12),
              child: Container(
                padding: const EdgeInsets.symmetric(
                  horizontal: 12,
                  vertical: 8,
                ),
                decoration: BoxDecoration(
                  color: isDark
                      ? const Color(0x2610B981)
                      : const Color(0x1A059669),
                  borderRadius: BorderRadius.circular(12),
                ),
                child: MiuixText(
                  successText ?? '',
                  style: theme.textStyles.body2,
                  color: isDark
                      ? const Color(0xFF34D399)
                      : const Color(0xFF047857),
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
                        trailingTag: !profileComplete
                            ? (acct.hasEmail ? '需先设密码' : '请先完善账号')
                            : null,
                        enabled: !avatarBusy && profileComplete,
                        onTap: onPickAvatar,
                      ),
                      if (avatarBytes != null) ...[
                        const OsmiumCardDivider(),
                        _AccountDropdownItem(
                          icon: Icons.hide_image_outlined,
                          title: '移除头像',
                          trailingTag: !profileComplete
                              ? (acct.hasEmail ? '需先设密码' : '请先完善账号')
                              : null,
                          enabled: !avatarBusy && profileComplete,
                          onTap: onClearAvatar,
                        ),
                      ],
                      const OsmiumCardDivider(),
                      _AccountDropdownItem(
                        icon: Icons.badge_outlined,
                        title: '修改昵称',
                        trailingTag: !profileComplete
                            ? (acct.hasEmail ? '需先设密码' : '请先完善账号')
                            : null,
                        enabled: profileComplete,
                        onTap: onEditNickname,
                      ),
                      const OsmiumCardDivider(),
                      _AccountDropdownItem(
                        icon: Icons.mail_outline_rounded,
                        title: acct.hasEmail ? '更换绑定邮箱' : '绑定邮箱',
                        trailingTag: !acct.hasEmail
                            ? '未绑定'
                            : (!profileComplete ? '需先设密码' : null),
                        enabled: canManageEmail,
                        onTap: onChangeEmail,
                      ),
                      if (!acct.hasPassword) ...[
                        const OsmiumCardDivider(),
                        _AccountDropdownItem(
                          icon: Icons.lock_outline_rounded,
                          title: '设置登录密码',
                          trailingTag: acct.hasEmail ? '未设置' : '请先绑定邮箱',
                          enabled: canSetPassword,
                          onTap: onSetPassword,
                        ),
                      ],
                      if (oauthBindings == null &&
                          (oauthBindingsLoading || oauthBindingsFailed)) ...[
                        const OsmiumCardDivider(),
                        _AccountDropdownItem(
                          icon: Icons.link_rounded,
                          title: '第三方平台绑定',
                          trailingTag: oauthBindingsFailed ? '点击重试' : '加载中…',
                          trailingHighlight: oauthBindingsFailed,
                          enabled: oauthBindingsFailed,
                          onTap: onRetryOAuthBindings,
                        ),
                      ],
                      if (oauthBindings != null &&
                          oauthBindings!.hasAnyVisible) ...[
                        for (final provider in const [
                          'google',
                          'qq',
                          'wechat',
                        ])
                          if (oauthBindings!.isVisible(provider)) ...[
                            const OsmiumCardDivider(),
                            _buildOAuthBindingRow(
                              provider: provider,
                              isBound: oauthBindings!.isBound(provider),
                              isAvailable: oauthBindings!.isAvailable(provider),
                              profileComplete: profileComplete,
                            ),
                          ],
                      ],
                    ],
                  )
                : const SizedBox.shrink(),
          ),
        ],
      ),
    );
  }

  Widget _buildOAuthBindingRow({
    required String provider,
    required bool isBound,
    required bool isAvailable,
    required bool profileComplete,
  }) {
    final enabled =
        profileComplete &&
        !acct.locked &&
        !oauthBindBusy &&
        (isBound || isAvailable);
    final Widget brandIcon = switch (provider) {
      'google' => const GoogleBrandIcon(size: 19),
      'qq' => const QqBrandIcon(size: 19),
      'wechat' => const WechatBrandIcon(size: 19),
      _ => const Icon(Icons.link_rounded, size: 19),
    };
    final title = switch (provider) {
      'google' => 'Google账号',
      'qq' => 'QQ账号',
      'wechat' => '微信账号',
      _ => provider,
    };
    final String trailingTag = isBound
        ? '已绑定'
        : (!profileComplete
              ? (acct.hasEmail ? '需先设密码' : '请先完善账号')
              : '去绑定');

    return _AccountDropdownItem(
      leading: Opacity(opacity: enabled ? 1.0 : 0.45, child: brandIcon),
      title: title,
      trailingTag: trailingTag,
      trailingHighlight: !isBound && enabled,
      enabled: enabled,
      onTap: () => onTapOAuthBinding(provider),
    );
  }
}

class _AccountDropdownItem extends StatelessWidget {
  const _AccountDropdownItem({
    this.icon,
    this.leading,
    required this.title,
    required this.onTap,
    this.enabled = true,
    this.trailingTag,
    this.trailingHighlight = true,
  });

  final IconData? icon;
  final Widget? leading;
  final String title;
  final VoidCallback onTap;
  final bool enabled;
  final String? trailingTag;
  final bool trailingHighlight;

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
            leading ??
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
            if (trailingTag != null) ...[
              MiuixText(
                trailingTag!,
                style: theme.textStyles.body2,
                color: (enabled && trailingHighlight)
                    ? colors.primary
                    : colors.onSurfaceVariantSummary,
              ),
              const SizedBox(width: 4),
            ],
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
