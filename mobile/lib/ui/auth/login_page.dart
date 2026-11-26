import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/physics.dart';
import 'package:flutter_miuix/miuix.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/constants/osmium_constants.dart';
import '../../core/models/account_models.dart';
import '../../core/theme/osmium_motion.dart';
import '../../core/theme/osmium_theme.dart';
import '../../state/app_controller.dart';
import '../shared/home_widgets.dart';
import '../widgets/osmium_motion_widgets.dart';
import '../widgets/social_brand_icons.dart';
import 'captcha_webview_page.dart';

class LoginPage extends ConsumerStatefulWidget {
  const LoginPage({super.key});

  @override
  ConsumerState<LoginPage> createState() => _LoginPageState();
}

class _LoginPageState extends ConsumerState<LoginPage> {
  bool _isRegister = false;
  bool _submitting = false;
  bool _sendingCode = false;
  bool _obscurePassword = true;
  int _codeCooldown = 0;
  Timer? _cooldownTimer;
  String? _error;

  bool _oauthBusy = false;
  bool _oauthDialogVisible = false;
  bool _oauthPolling = false;
  String _oauthProvider = '';
  OAuthStartData? _oauthSession;
  Timer? _oauthPollTimer;
  StreamSubscription<String>? _oauthDeepLinkSub;

  bool _checkingAuthApi = true;
  bool _authApiReachable = false;
  bool _authApiFailed = false;
  OAuthProvidersData _oauthProviders = const OAuthProvidersData();

  bool _loginKeyVisible = false;
  bool _loginKeySubmitting = false;
  bool _obscureLoginKey = true;
  String? _loginKeyError;

  bool _twoFaVisible = false;
  bool _twoFaSubmitting = false;
  String _twoFaToken = '';
  String? _twoFaError;

  final _emailCtrl = TextEditingController();
  final _passwordCtrl = TextEditingController();
  final _usernameCtrl = TextEditingController();
  final _codeCtrl = TextEditingController();
  final _loginKeyCtrl = TextEditingController();
  final _twoFaCtrl = TextEditingController();

  bool get _canLogin => _authApiReachable && !_checkingAuthApi;

  @override
  void initState() {
    super.initState();
    unawaited(_checkAuthAvailability());
  }

  @override
  void dispose() {
    _stopOAuthPolling(cancelRemote: true);
    _cooldownTimer?.cancel();
    _emailCtrl.dispose();
    _passwordCtrl.dispose();
    _usernameCtrl.dispose();
    _codeCtrl.dispose();
    _loginKeyCtrl.dispose();
    _twoFaCtrl.dispose();
    super.dispose();
  }

  Future<void> _checkAuthAvailability() async {
    setState(() {
      _checkingAuthApi = true;
      _authApiReachable = false;
      _authApiFailed = false;
      _oauthProviders = const OAuthProvidersData();
    });

    final res = await ref.read(accountServiceProvider).fetchOAuthProviders();
    if (!mounted) return;
    if (res.ok && res.data != null) {
      setState(() {
        _checkingAuthApi = false;
        _authApiReachable = true;
        _authApiFailed = false;
        _oauthProviders = res.data!;
      });
    } else {
      setState(() {
        _checkingAuthApi = false;
        _authApiReachable = false;
        _authApiFailed = true;
        _oauthProviders = const OAuthProvidersData();
      });
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

  void _stopOAuthPolling({required bool cancelRemote}) {
    _oauthPollTimer?.cancel();
    _oauthPollTimer = null;
    _oauthDeepLinkSub?.cancel();
    _oauthDeepLinkSub = null;
    _oauthPolling = false;
    final sess = _oauthSession;
    _oauthSession = null;
    if (cancelRemote && sess != null) {
      unawaited(
        ref
            .read(accountServiceProvider)
            .cancelOAuthLogin(state: sess.state, pollSecret: sess.pollSecret),
      );
    }
  }

  void _cancelOAuthFlow() {
    _stopOAuthPolling(cancelRemote: true);
    if (mounted) {
      setState(() {
        _oauthDialogVisible = false;
        _oauthBusy = false;
      });
    }
  }

  Future<void> _startOAuthLogin(String provider) async {
    if (!_canLogin ||
        !_oauthProviders.isAvailable(provider) ||
        _submitting ||
        _oauthBusy) {
      return;
    }
    OsmiumHaptics.selection();
    setState(() {
      _oauthBusy = true;
      _oauthProvider = provider;
      _error = null;
    });

    final app = ref.read(appControllerProvider);
    final accountSvc = ref.read(accountServiceProvider);
    final res = await accountSvc.startOAuthLogin(
      provider: provider,
      identity: app.identity,
    );
    if (!mounted) return;
    if (!res.ok || res.data == null || res.data!.authorizeUrl.isEmpty) {
      setState(() {
        _oauthBusy = false;
        _error = res.message.isNotEmpty ? res.message : '发起登录失败，请重试';
      });
      return;
    }

    final session = res.data!;
    _stopOAuthPolling(cancelRemote: false);
    _oauthSession = session;

    final bridge = ref.read(platformBridgeProvider);
    final opened = await bridge.openExternalUrl(session.authorizeUrl);
    if (!mounted) return;
    if (!opened) {
      _stopOAuthPolling(cancelRemote: true);
      setState(() {
        _oauthBusy = false;
        _error = '未能打开浏览器，请检查系统默认浏览器设置';
      });
      return;
    }

    setState(() {
      _oauthDialogVisible = true;
    });

    _oauthDeepLinkSub = bridge.deepLinks.listen((link) {
      if (link.toLowerCase().startsWith('osmium://oauth-done')) {
        unawaited(_pollOAuthOnce());
      }
    });

    final intervalMs = session.pollIntervalMs.clamp(1000, 3000);
    _oauthPollTimer = Timer.periodic(
      Duration(milliseconds: intervalMs),
      (_) => unawaited(_pollOAuthOnce()),
    );
  }

  Future<void> _pollOAuthOnce() async {
    final session = _oauthSession;
    if (session == null || _oauthPolling || !mounted) return;
    _oauthPolling = true;
    try {
      final accountSvc = ref.read(accountServiceProvider);
      final res = await accountSvc.pollOAuthStatus(
        pollUrl: session.pollUrl,
        pollSecret: session.pollSecret,
      );
      if (!mounted || _oauthSession?.state != session.state) return;

      if (res.ok && res.data != null) {
        if (res.data!.state == 'completed' && accountSvc.isLoggedIn) {
          _stopOAuthPolling(cancelRemote: false);
          setState(() {
            _oauthDialogVisible = false;
            _oauthBusy = false;
          });
          OsmiumHaptics.confirm();
          Navigator.of(context).pop(true);
        }
        return;
      }

      if (res.httpStatusCode > 0) {
        _stopOAuthPolling(cancelRemote: false);
        setState(() {
          _oauthDialogVisible = false;
          _oauthBusy = false;
          _error = res.message.isNotEmpty ? res.message : '登录授权未完成，请重试';
        });
      }
    } finally {
      _oauthPolling = false;
    }
  }

  void _startCooldown() {
    _cooldownTimer?.cancel();
    setState(() => _codeCooldown = 60);
    _cooldownTimer = Timer.periodic(const Duration(seconds: 1), (timer) {
      if (!mounted) {
        timer.cancel();
        return;
      }
      if (_codeCooldown <= 1) {
        timer.cancel();
        setState(() => _codeCooldown = 0);
      } else {
        setState(() => _codeCooldown -= 1);
      }
    });
  }

  Future<Map<String, dynamic>?> _runCaptcha(AccountChallengeInfo challenge) {
    final accountSvc = ref.read(accountServiceProvider);
    return Navigator.of(context).push<Map<String, dynamic>>(
      MaterialPageRoute(
        builder: (_) => CaptchaWebViewPage(
          challenge: challenge,
          accountService: accountSvc,
        ),
      ),
    );
  }

  /// The captcha page only pops with data once the poll endpoint returned a
  /// terminal success and the account service persisted the session (or
  /// returned a 2FA challenge token).
  Future<void> _finishCaptchaSignIn(
    AccountChallengeInfo challenge,
    String fallbackError,
  ) async {
    final verified = await _runCaptcha(challenge);
    if (!mounted || verified == null) return;
    final twoFaToken = (verified['two_fa_token'] ?? '').toString().trim();
    if (twoFaToken.isNotEmpty) {
      _openTwoFaDialog(twoFaToken);
      return;
    }
    if (ref.read(accountServiceProvider).isLoggedIn) {
      Navigator.of(context).pop(true);
    } else {
      setState(() => _error = fallbackError);
    }
  }

  void _openTwoFaDialog(String token) {
    _twoFaCtrl.clear();
    setState(() {
      _twoFaToken = token;
      _twoFaError = null;
      _twoFaSubmitting = false;
      _twoFaVisible = true;
    });
  }

  void _dismissTwoFaDialog() {
    if (!_twoFaVisible || _twoFaSubmitting) return;
    FocusScope.of(context).unfocus();
    setState(() {
      _twoFaVisible = false;
      _twoFaToken = '';
      _twoFaError = null;
    });
  }

  Future<void> _submitTwoFa() async {
    if (_twoFaSubmitting || _twoFaToken.isEmpty) return;
    final digits = _twoFaCtrl.text.replaceAll(RegExp(r'\D'), '');
    if (digits.length != 6) {
      setState(() => _twoFaError = '请输入6位动态验证码');
      return;
    }
    OsmiumHaptics.confirm();
    setState(() {
      _twoFaSubmitting = true;
      _twoFaError = null;
    });
    try {
      final accountSvc = ref.read(accountServiceProvider);
      final res = await accountSvc.verifyTwoFaLogin(
        twoFaToken: _twoFaToken,
        code: digits,
      );
      if (!mounted) return;
      if (res.needsCaptcha && res.challenge != null) {
        setState(() => _twoFaSubmitting = false);
        await _runCaptcha(res.challenge!);
        if (!mounted) return;
        if (accountSvc.isLoggedIn) {
          setState(() => _twoFaVisible = false);
          Navigator.of(context).pop(true);
        } else {
          _twoFaCtrl.clear();
        }
        return;
      }
      if (res.ok && accountSvc.isLoggedIn) {
        setState(() => _twoFaVisible = false);
        Navigator.of(context).pop(true);
        return;
      }
      if (res.code == OsmiumErrorCode.twoFaExpired) {
        FocusScope.of(context).unfocus();
        setState(() {
          _twoFaVisible = false;
          _twoFaToken = '';
          _twoFaError = null;
          _error = res.message.isNotEmpty ? res.message : '两步验证已超时，请重新登录';
        });
        return;
      }
      setState(
        () => _twoFaError = res.message.isNotEmpty ? res.message : '动态验证码不正确，请重试',
      );
    } finally {
      if (mounted) setState(() => _twoFaSubmitting = false);
    }
  }

  void _openLoginKeyDialog() {
    if (!_canLogin || _submitting || _oauthBusy) return;
    OsmiumHaptics.selection();
    _loginKeyCtrl.clear();
    setState(() {
      _loginKeyError = null;
      _obscureLoginKey = true;
      _loginKeySubmitting = false;
      _loginKeyVisible = true;
    });
  }

  void _dismissLoginKeyDialog() {
    if (!_loginKeyVisible || _loginKeySubmitting) return;
    FocusScope.of(context).unfocus();
    setState(() {
      _loginKeyVisible = false;
      _loginKeyError = null;
    });
  }

  Future<void> _submitLoginKey() async {
    if (_loginKeySubmitting) return;
    final key = _loginKeyCtrl.text.trim();
    if (key.isEmpty) {
      setState(() => _loginKeyError = '请输入登录密钥');
      return;
    }
    OsmiumHaptics.confirm();
    setState(() {
      _loginKeySubmitting = true;
      _loginKeyError = null;
    });
    try {
      final app = ref.read(appControllerProvider);
      final accountSvc = ref.read(accountServiceProvider);
      final res = await accountSvc.loginWithKey(
        loginKey: key,
        identity: app.identity,
      );
      if (!mounted) return;
      if (res.needsCaptcha && res.challenge != null) {
        setState(() => _loginKeySubmitting = false);
        await _runCaptcha(res.challenge!);
        if (!mounted) return;
        if (accountSvc.isLoggedIn) {
          setState(() => _loginKeyVisible = false);
          Navigator.of(context).pop(true);
        }
        return;
      }
      if (res.ok && accountSvc.isLoggedIn) {
        setState(() => _loginKeyVisible = false);
        Navigator.of(context).pop(true);
        return;
      }
      setState(
        () => _loginKeyError = res.message.isNotEmpty
            ? res.message
            : '登录密钥无效或已移除',
      );
    } finally {
      if (mounted) setState(() => _loginKeySubmitting = false);
    }
  }

  Future<void> _sendRegisterCode() async {
    if (!_canLogin) return;
    final email = _emailCtrl.text.trim();
    if (email.isEmpty || !email.contains('@')) {
      setState(() => _error = '请输入有效邮箱');
      return;
    }
    setState(() {
      _sendingCode = true;
      _error = null;
    });
    try {
      final accountSvc = ref.read(accountServiceProvider);
      final res = await accountSvc.startRegisterSendCode(email);
      if (!mounted) return;
      if (res.needsCaptcha && res.challenge != null) {
        final verified = await _runCaptcha(res.challenge!);
        if (!mounted) return;
        if (verified != null) {
          _startCooldown();
        }
        return;
      }
      if (res.ok) {
        _startCooldown();
      } else {
        setState(() => _error = res.message.isNotEmpty ? res.message : '发送失败');
      }
    } finally {
      if (mounted) setState(() => _sendingCode = false);
    }
  }

  Future<void> _submit() async {
    if (!_canLogin || _submitting || _oauthBusy) return;
    final email = _emailCtrl.text.trim();
    final password = _passwordCtrl.text;
    if (email.isEmpty || password.isEmpty) {
      setState(() => _error = '请输入邮箱和密码');
      return;
    }
    if (_isRegister && _codeCtrl.text.trim().isEmpty) {
      setState(() => _error = '请输入邮箱验证码');
      return;
    }

    OsmiumHaptics.confirm();
    setState(() {
      _submitting = true;
      _error = null;
    });

    try {
      final app = ref.read(appControllerProvider);
      final accountSvc = ref.read(accountServiceProvider);

      if (_isRegister) {
        final res = await accountSvc.completeRegister(
          email: email,
          code: _codeCtrl.text.trim(),
          password: password,
          username: _usernameCtrl.text.trim().isNotEmpty
              ? _usernameCtrl.text.trim()
              : email.split('@').first,
          identity: app.identity,
        );
        if (!mounted) return;
        if (res.needsCaptcha && res.challenge != null) {
          await _finishCaptchaSignIn(res.challenge!, '注册失败');
          return;
        }
        if (res.ok) {
          Navigator.of(context).pop(true);
        } else {
          setState(
            () => _error = res.message.isNotEmpty ? res.message : '注册失败',
          );
        }
      } else {
        final res = await accountSvc.startLogin(
          email: email,
          password: password,
          identity: app.identity,
        );
        if (!mounted) return;
        if (res.needsCaptcha && res.challenge != null) {
          await _finishCaptchaSignIn(res.challenge!, '登录失败');
          return;
        }
        if (res.ok) {
          Navigator.of(context).pop(true);
        } else {
          setState(
            () => _error = res.message.isNotEmpty ? res.message : '登录失败',
          );
        }
      }
    } finally {
      if (mounted) setState(() => _submitting = false);
    }
  }

  Widget _buildAvailableOAuthRow(bool isDark) {
    final items = <Widget>[];
    void addBtn(String provider, String label, Widget icon) {
      if (items.isNotEmpty) {
        items.add(const SizedBox(width: 8));
      }
      items.add(
        Expanded(
          child: _OAuthProviderButton(
            label: label,
            icon: icon,
            enabled: _canLogin && !_submitting && !_oauthBusy,
            isDark: isDark,
            onPressed: () => _startOAuthLogin(provider),
          ),
        ),
      );
    }

    if (_oauthProviders.google) {
      addBtn('google', 'Google', const GoogleBrandIcon(size: 18));
    }
    if (_oauthProviders.qq) {
      addBtn('qq', 'QQ', const QqBrandIcon(size: 18));
    }
    if (_oauthProviders.wechat) {
      addBtn('wechat', '微信', const WechatBrandIcon(size: 18));
    }
    return Row(children: items);
  }

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;
    final pad = MediaQuery.paddingOf(context);
    final keyboard = MediaQuery.viewInsetsOf(context).bottom;

    final fieldColors = MiuixTextFieldColors(
      backgroundColor: isDark
          ? Colors.white.withValues(alpha: 0.07)
          : Colors.white.withValues(alpha: 0.62),
      labelColor: colors.onSurfaceVariantSummary,
      borderColor: colors.primary,
    );

    return MiuixScaffold(
      content: (_) {
        return Stack(
          fit: StackFit.expand,
          children: [
            _LoginBackdrop(isDark: isDark, background: colors.background),
            LayoutBuilder(
              builder: (context, constraints) => SingleChildScrollView(
                keyboardDismissBehavior:
                    ScrollViewKeyboardDismissBehavior.onDrag,
                // Anchored from the top, not centred: revealing the register
                // fields grows the card downwards and the toggle stays put.
                padding: EdgeInsets.fromLTRB(
                  20,
                  pad.top + (constraints.maxHeight * 0.13).clamp(56.0, 132.0),
                  20,
                  pad.bottom + keyboard + 24,
                ),
                child: Align(
                  alignment: Alignment.topCenter,
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(maxWidth: 400),
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Row(
                          mainAxisAlignment: MainAxisAlignment.center,
                          children: [
                            ClipRRect(
                              borderRadius: BorderRadius.circular(9),
                              child: Image.asset(
                                'assets/images/osmium_logo.png',
                                width: 36,
                                height: 36,
                                fit: BoxFit.cover,
                              ),
                            ),
                            const SizedBox(width: 10),
                            Text(
                              'Osmium',
                              style: TextStyle(
                                fontFamily: OsmiumTheme.fontFamily,
                                fontSize: 24,
                                fontWeight: FontWeight.w700,
                                letterSpacing: -0.2,
                                color: colors.onSurface,
                              ),
                            ),
                          ],
                        ),
                        const SizedBox(height: 22),
                        OsmiumGlassSurface(
                          cornerRadius: 24,
                          padding: const EdgeInsets.fromLTRB(16, 16, 16, 18),
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              _ModeToggle(
                                isRegister: _isRegister,
                                onChanged: (v) => setState(() {
                                  _isRegister = v;
                                  _error = null;
                                }),
                              ),
                              const SizedBox(height: 16),
                              MiuixTextField(
                                controller: _emailCtrl,
                                label: '邮箱',
                                singleLine: true,
                                colors: fieldColors,
                                cornerRadius: OsmiumFieldDefaults.cornerRadius,
                                insideMargin:
                                    OsmiumFieldDefaults.floatingMargin,
                                keyboardType: TextInputType.emailAddress,
                                textInputAction: TextInputAction.next,
                              ),
                              OsmiumAnimatedReveal(
                                visible: _isRegister,
                                child: Column(
                                  crossAxisAlignment:
                                      CrossAxisAlignment.stretch,
                                  children: [
                                    const SizedBox(height: 10),
                                    MiuixTextField(
                                      controller: _usernameCtrl,
                                      label: '昵称',
                                      singleLine: true,
                                      colors: fieldColors,
                                      cornerRadius:
                                          OsmiumFieldDefaults.cornerRadius,
                                      insideMargin:
                                          OsmiumFieldDefaults.floatingMargin,
                                      textInputAction: TextInputAction.next,
                                    ),
                                    const SizedBox(height: 10),
                                    Row(
                                      children: [
                                        Expanded(
                                          child: MiuixTextField(
                                            controller: _codeCtrl,
                                            label: '验证码',
                                            singleLine: true,
                                            colors: fieldColors,
                                            cornerRadius: OsmiumFieldDefaults
                                                .cornerRadius,
                                            insideMargin: OsmiumFieldDefaults
                                                .floatingMargin,
                                            keyboardType: TextInputType.number,
                                            textInputAction:
                                                TextInputAction.next,
                                          ),
                                        ),
                                        const SizedBox(width: 8),
                                        MiuixTextButton(
                                          _codeCooldown > 0
                                              ? '${_codeCooldown}s'
                                              : '获取',
                                          enabled:
                                              _canLogin &&
                                              !_sendingCode &&
                                              _codeCooldown == 0,
                                          onPressed: _sendRegisterCode,
                                          minHeight: 44,
                                          colors: MiuixButtonColors(
                                            color: fieldColors.backgroundColor,
                                            disabledColor:
                                                fieldColors.backgroundColor,
                                            contentColor: colors.primary,
                                            disabledContentColor:
                                                colors.onSurfaceVariantSummary,
                                          ),
                                        ),
                                      ],
                                    ),
                                  ],
                                ),
                              ),
                              const SizedBox(height: 10),
                              MiuixTextField(
                                controller: _passwordCtrl,
                                label: '密码',
                                singleLine: true,
                                colors: fieldColors,
                                cornerRadius: OsmiumFieldDefaults.cornerRadius,
                                insideMargin:
                                    OsmiumFieldDefaults.floatingMargin,
                                obscureText: _obscurePassword,
                                textInputAction: TextInputAction.done,
                                onSubmitted: (_) => _submit(),
                                trailingIcon: MiuixIconButton(
                                  onPressed: () {
                                    OsmiumHaptics.selection();
                                    setState(
                                      () =>
                                          _obscurePassword = !_obscurePassword,
                                    );
                                  },
                                  minWidth: 40,
                                  minHeight: 36,
                                  child: Icon(
                                    _obscurePassword
                                        ? Icons.visibility_off_outlined
                                        : Icons.visibility_outlined,
                                    size: 18,
                                    color: colors.onSurfaceVariantSummary,
                                  ),
                                ),
                              ),
                              OsmiumAnimatedReveal(
                                visible: _authApiFailed,
                                child: Padding(
                                  padding: const EdgeInsets.only(top: 10),
                                  child: Container(
                                    padding: const EdgeInsets.fromLTRB(
                                      12,
                                      10,
                                      10,
                                      10,
                                    ),
                                    decoration: BoxDecoration(
                                      color: OsmiumColors.dangerBg(isDark)
                                          .withValues(alpha: 0.85),
                                      borderRadius: BorderRadius.circular(12),
                                    ),
                                    child: Row(
                                      children: [
                                        Expanded(
                                          child: MiuixText(
                                            '当前无法连接到服务器，暂不支持登录，请检查网络后重试。',
                                            style: theme.textStyles.body2,
                                            color: OsmiumColors.dangerFg(
                                              isDark,
                                            ),
                                          ),
                                        ),
                                        const SizedBox(width: 8),
                                        MiuixTextButton(
                                          '重试',
                                          enabled: !_checkingAuthApi,
                                          onPressed: _checkAuthAvailability,
                                          minHeight: 32,
                                        ),
                                      ],
                                    ),
                                  ),
                                ),
                              ),
                              OsmiumAnimatedReveal(
                                visible: _error != null,
                                child: Padding(
                                  padding: const EdgeInsets.only(top: 10),
                                  child: Container(
                                    padding: const EdgeInsets.symmetric(
                                      horizontal: 12,
                                      vertical: 8,
                                    ),
                                    decoration: BoxDecoration(
                                      color: OsmiumColors.dangerBg(isDark)
                                          .withValues(alpha: 0.85),
                                      borderRadius: BorderRadius.circular(12),
                                    ),
                                    child: MiuixText(
                                      _error ?? '',
                                      style: theme.textStyles.body2,
                                      color: OsmiumColors.dangerFg(isDark),
                                    ),
                                  ),
                                ),
                              ),
                              const SizedBox(height: 16),
                              MiuixButton(
                                onPressed:
                                    (_canLogin && !_submitting && !_oauthBusy)
                                    ? _submit
                                    : null,
                                colors: MiuixButtonDefaults.buttonColorsPrimary(
                                  context,
                                ),
                                minHeight: 44,
                                child: AnimatedSwitcher(
                                  duration: OsmiumMotion.durationOr(
                                    context,
                                    OsmiumMotion.small,
                                  ),
                                  switchInCurve: MiuixMotion.standardDecelerate,
                                  switchOutCurve:
                                      MiuixMotion.standardAccelerate,
                                  transitionBuilder: (child, animation) =>
                                      FadeTransition(
                                        opacity: animation,
                                        child: ScaleTransition(
                                          scale: Tween<double>(
                                            begin: 0.9,
                                            end: 1,
                                          ).animate(animation),
                                          child: child,
                                        ),
                                      ),
                                  child: (_submitting || _checkingAuthApi)
                                      ? SizedBox(
                                          key: const ValueKey('submitting'),
                                          width: 18,
                                          height: 18,
                                          child: CircularProgressIndicator(
                                            strokeWidth: 2,
                                            color: colors.onPrimary,
                                          ),
                                        )
                                      : Text(
                                          _isRegister ? '注册并登录' : '登录',
                                          key: const ValueKey('idle'),
                                        ),
                                ),
                              ),
                              OsmiumAnimatedReveal(
                                visible: !_isRegister,
                                child: Padding(
                                  padding: const EdgeInsets.only(top: 10),
                                  child: MiuixTextButton(
                                    '使用登录密钥登录',
                                    enabled:
                                        _canLogin &&
                                        !_submitting &&
                                        !_oauthBusy,
                                    onPressed: _openLoginKeyDialog,
                                    minHeight: 40,
                                    colors: MiuixButtonColors(
                                      color: fieldColors.backgroundColor,
                                      disabledColor: fieldColors.backgroundColor
                                          .withValues(alpha: 0.45),
                                      contentColor: colors.onSurface,
                                      disabledContentColor:
                                          colors.disabledOnSecondaryVariant,
                                    ),
                                  ),
                                ),
                              ),
                              if (_authApiReachable &&
                                  _oauthProviders.hasAny) ...[
                                const SizedBox(height: 18),
                                Row(
                                  children: [
                                    Expanded(
                                      child: Divider(
                                        height: 1,
                                        color: colors.onSurface.withValues(
                                          alpha: isDark ? 0.14 : 0.1,
                                        ),
                                      ),
                                    ),
                                    Padding(
                                      padding: const EdgeInsets.symmetric(
                                        horizontal: 10,
                                      ),
                                      child: MiuixText(
                                        '其他登录方式',
                                        style: theme.textStyles.footnote1,
                                        color: colors.onSurfaceVariantSummary,
                                      ),
                                    ),
                                    Expanded(
                                      child: Divider(
                                        height: 1,
                                        color: colors.onSurface.withValues(
                                          alpha: isDark ? 0.14 : 0.1,
                                        ),
                                      ),
                                    ),
                                  ],
                                ),
                                const SizedBox(height: 14),
                                _buildAvailableOAuthRow(isDark),
                              ],
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            ),
            Positioned(
              top: pad.top + 6,
              left: 8,
              child: MiuixIconButton(
                onPressed: () => Navigator.of(context).pop(),
                minWidth: 40,
                minHeight: 40,
                child: Icon(
                  Icons.arrow_back_ios_new_rounded,
                  size: 19,
                  color: colors.onSurface,
                  semanticLabel: '返回',
                ),
              ),
            ),
            MiuixOverlayDialog(
              show: _oauthDialogVisible,
              title: '使用${_oauthProviderLabel(_oauthProvider)}登录',
              onDismissRequest: _cancelOAuthFlow,
              content: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Center(
                    child: Padding(
                      padding: const EdgeInsets.symmetric(vertical: 8),
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
                  const SizedBox(height: 8),
                  MiuixText(
                    '请在浏览器中完成${_oauthProviderLabel(_oauthProvider)}登录授权，完成后将自动返回Osmium。',
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
                          onPressed: _cancelOAuthFlow,
                        ),
                      ),
                      const SizedBox(width: 10),
                      Expanded(
                        child: MiuixTextButton(
                          '重新打开',
                          onPressed: () {
                            final url = _oauthSession?.authorizeUrl;
                            if (url != null && url.isNotEmpty) {
                              unawaited(
                                ref
                                    .read(platformBridgeProvider)
                                    .openExternalUrl(url),
                              );
                            }
                          },
                          colors: MiuixButtonDefaults.buttonColorsPrimary(
                            context,
                          ),
                        ),
                      ),
                    ],
                  ),
                ],
              ),
            ),
            _buildLoginKeyDialog(context),
            _buildTwoFaDialog(context),
          ],
        );
      },
    );
  }

  Widget _buildLoginKeyDialog(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;

    return MiuixOverlayDialog(
      show: _loginKeyVisible,
      title: '使用登录密钥登录',
      onDismissRequest: _dismissLoginKeyDialog,
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            MiuixText(
              '输入已创建的登录密钥即可快速登录账号。',
              style: theme.textStyles.body2,
              color: colors.onSurfaceVariantSummary,
            ),
            const SizedBox(height: 12),
            MiuixTextField(
              cornerRadius: OsmiumFieldDefaults.cornerRadius,
              insideMargin: OsmiumFieldDefaults.floatingMargin,
              controller: _loginKeyCtrl,
              label: '登录密钥',
              singleLine: true,
              obscureText: _obscureLoginKey,
              autofocus: true,
              textInputAction: TextInputAction.done,
              onSubmitted: (_) => _submitLoginKey(),
              trailingIcon: MiuixIconButton(
                onPressed: () {
                  OsmiumHaptics.selection();
                  setState(() => _obscureLoginKey = !_obscureLoginKey);
                },
                minWidth: 34,
                minHeight: 34,
                child: Icon(
                  _obscureLoginKey
                      ? Icons.visibility_off_outlined
                      : Icons.visibility_outlined,
                  size: 18,
                  color: colors.onSurfaceVariantSummary,
                ),
              ),
            ),
            OsmiumAnimatedReveal(
              visible: _loginKeyError != null,
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
                    _loginKeyError ?? '',
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
                    enabled: !_loginKeySubmitting,
                    onPressed: _dismissLoginKeyDialog,
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: MiuixTextButton(
                    _loginKeySubmitting ? '登录中…' : '登录',
                    enabled: !_loginKeySubmitting,
                    onPressed: _submitLoginKey,
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

  Widget _buildTwoFaDialog(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;

    return MiuixOverlayDialog(
      show: _twoFaVisible,
      title: '两步验证',
      onDismissRequest: _dismissTwoFaDialog,
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            MiuixText(
              '当前账号已开启2FA，请输入身份验证器中的6位动态验证码。',
              style: theme.textStyles.body2,
              color: colors.onSurfaceVariantSummary,
            ),
            const SizedBox(height: 12),
            MiuixTextField(
              cornerRadius: OsmiumFieldDefaults.cornerRadius,
              insideMargin: OsmiumFieldDefaults.floatingMargin,
              controller: _twoFaCtrl,
              label: '6位动态验证码',
              singleLine: true,
              autofocus: true,
              keyboardType: TextInputType.number,
              textInputAction: TextInputAction.done,
              onSubmitted: (_) => _submitTwoFa(),
            ),
            OsmiumAnimatedReveal(
              visible: _twoFaError != null,
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
                    _twoFaError ?? '',
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
                    enabled: !_twoFaSubmitting,
                    onPressed: _dismissTwoFaDialog,
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: MiuixTextButton(
                    _twoFaSubmitting ? '验证中…' : '验证并登录',
                    enabled: !_twoFaSubmitting,
                    onPressed: _submitTwoFa,
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
}

class _OAuthProviderButton extends StatelessWidget {
  const _OAuthProviderButton({
    required this.label,
    required this.icon,
    required this.enabled,
    required this.isDark,
    required this.onPressed,
  });

  final String label;
  final Widget icon;
  final bool enabled;
  final bool isDark;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) {
    final colors = MiuixTheme.of(context).colors;
    final bg = isDark
        ? Colors.white.withValues(alpha: 0.08)
        : Colors.white.withValues(alpha: 0.72);
    return MiuixButton(
      onPressed: enabled ? onPressed : null,
      minHeight: 40,
      insideMargin: const EdgeInsets.symmetric(horizontal: 8, vertical: 8),
      colors: MiuixButtonColors(
        color: bg,
        disabledColor: bg.withValues(alpha: 0.45),
        contentColor: colors.onSurface,
        disabledContentColor: colors.disabledOnSecondaryVariant,
      ),
      child: Row(
        mainAxisAlignment: MainAxisAlignment.center,
        mainAxisSize: MainAxisSize.min,
        children: [
          icon,
          const SizedBox(width: 6),
          Flexible(
            child: Text(
              label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontFamily: OsmiumTheme.fontFamily,
                fontSize: 13,
                fontWeight: FontWeight.w500,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// Soft drifting colour fields behind the login glass, so the frost has
/// something to refract.
class _LoginBackdrop extends StatefulWidget {
  const _LoginBackdrop({required this.isDark, required this.background});

  final bool isDark;
  final Color background;

  @override
  State<_LoginBackdrop> createState() => _LoginBackdropState();
}

class _LoginBackdropState extends State<_LoginBackdrop>
    with SingleTickerProviderStateMixin {
  late final AnimationController _drift = AnimationController(
    vsync: this,
    duration: const Duration(seconds: 18),
    value: 0.5,
  );

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (OsmiumMotion.reduced(context)) {
      _drift
        ..stop()
        ..value = 0.5;
    } else if (!_drift.isAnimating) {
      _drift.repeat(reverse: true);
    }
  }

  @override
  void dispose() {
    _drift.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return RepaintBoundary(
      child: CustomPaint(
        painter: _BlobPainter(
          drift: _drift,
          isDark: widget.isDark,
          background: widget.background,
        ),
      ),
    );
  }
}

class _BlobPainter extends CustomPainter {
  _BlobPainter({
    required this.drift,
    required this.isDark,
    required this.background,
  }) : super(repaint: drift);

  final Animation<double> drift;
  final bool isDark;
  final Color background;

  @override
  void paint(Canvas canvas, Size size) {
    canvas.drawRect(Offset.zero & size, Paint()..color = background);
    final t = Curves.easeInOutSine.transform(drift.value) * 2 * math.pi;
    final w = size.width, h = size.height;
    final k = isDark ? 0.62 : 1.0;

    void blob(Offset c, double r, Color color) {
      final rect = Rect.fromCircle(center: c, radius: r);
      canvas.drawCircle(
        c,
        r,
        Paint()
          ..shader = RadialGradient(colors: [color, color.withValues(alpha: 0)])
              .createShader(rect),
      );
    }

    blob(
      Offset(w * (0.12 + 0.05 * math.sin(t)), h * (0.2 + 0.03 * math.cos(t))),
      w * 0.78,
      const Color(0xFF3482FF).withValues(alpha: 0.5 * k),
    );
    blob(
      Offset(w * (0.96 - 0.04 * math.cos(t)), h * (0.44 + 0.04 * math.sin(t))),
      w * 0.62,
      const Color(0xFF2FC6E0).withValues(alpha: 0.42 * k),
    );
    blob(
      Offset(w * (0.3 + 0.04 * math.cos(t)), h * (0.86 - 0.03 * math.sin(t))),
      w * 0.7,
      const Color(0xFF8A7DFF).withValues(alpha: 0.34 * k),
    );
  }

  @override
  bool shouldRepaint(_BlobPainter oldDelegate) =>
      oldDelegate.isDark != isDark || oldDelegate.background != background;
}

class _ModeToggle extends StatefulWidget {
  const _ModeToggle({required this.isRegister, required this.onChanged});

  final bool isRegister;
  final ValueChanged<bool> onChanged;

  @override
  State<_ModeToggle> createState() => _ModeToggleState();
}

class _ModeToggleState extends State<_ModeToggle>
    with SingleTickerProviderStateMixin {
  late final AnimationController _slide = AnimationController.unbounded(
    vsync: this,
    value: widget.isRegister ? 1 : 0,
  );

  bool _reduced = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _reduced = OsmiumMotion.reduced(context);
  }

  @override
  void didUpdateWidget(_ModeToggle oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.isRegister == widget.isRegister) return;
    final target = widget.isRegister ? 1.0 : 0.0;
    if (_reduced) {
      _slide.value = target;
    } else {
      _slide.animateWith(
        SpringSimulation(OsmiumMotion.emphasized, _slide.value, target, 0),
      );
    }
  }

  @override
  void dispose() {
    _slide.dispose();
    super.dispose();
  }

  void _select(bool register) {
    if (register == widget.isRegister) return;
    OsmiumHaptics.selection();
    widget.onChanged(register);
  }

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;

    return Container(
      height: 36,
      padding: const EdgeInsets.all(3),
      decoration: BoxDecoration(
        color: colors.onSurface.withValues(alpha: isDark ? 0.07 : 0.05),
        borderRadius: BorderRadius.circular(12),
      ),
      child: Stack(
        children: [
          Positioned.fill(
            child: AnimatedBuilder(
              animation: _slide,
              builder: (context, child) => Align(
                // A half-width pill inside a full-width parent only travels
                // between the edges when the alignment spans -1 ... +1.
                alignment: Alignment(
                  (-1 + 2 * _slide.value).clamp(-1.0, 1.0),
                  0,
                ),
                child: FractionallySizedBox(
                  widthFactor: 0.5,
                  heightFactor: 1,
                  child: child,
                ),
              ),
              child: DecoratedBox(
                decoration: BoxDecoration(
                  color: isDark
                      ? Colors.white.withValues(alpha: 0.14)
                      : Colors.white.withValues(alpha: 0.92),
                  borderRadius: BorderRadius.circular(9),
                  boxShadow: isDark
                      ? null
                      : const [
                          BoxShadow(
                            color: Color(0x14000000),
                            blurRadius: 6,
                            offset: Offset(0, 1),
                          ),
                        ],
                ),
              ),
            ),
          ),
          Row(
            children: [
              Expanded(
                child: _TabPill(
                  label: '登录',
                  register: false,
                  animation: _slide,
                  onTap: () => _select(false),
                ),
              ),
              Expanded(
                child: _TabPill(
                  label: '注册',
                  register: true,
                  animation: _slide,
                  onTap: () => _select(true),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

class _TabPill extends StatelessWidget {
  const _TabPill({
    required this.label,
    required this.register,
    required this.animation,
    required this.onTap,
  });

  final String label;

  /// Whether this pill is the register side of the toggle.
  final bool register;
  final Animation<double> animation;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final colors = MiuixTheme.of(context).colors;
    final reduced = OsmiumMotion.reduced(context);

    return GestureDetector(
      behavior: HitTestBehavior.opaque,
      onTap: onTap,
      child: AnimatedBuilder(
        animation: animation,
        builder: (context, child) {
          final value = animation.value.clamp(0.0, 1.0);
          final double active = register ? value : 1 - value;
          return Center(
            child: Text(
              label,
              style: TextStyle(
                fontFamily: OsmiumTheme.fontFamily,
                fontSize: 14,
                fontWeight: FontWeight.w600,
                color: Color.lerp(
                  colors.onSurfaceVariantSummary,
                  colors.onSurface,
                  reduced ? (active > 0.5 ? 1 : 0) : active,
                ),
              ),
            ),
          );
        },
      ),
    );
  }
}
