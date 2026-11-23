import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/physics.dart';
import 'package:flutter_miuix/miuix.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/account_models.dart';
import '../../core/theme/osmium_motion.dart';
import '../../core/theme/osmium_theme.dart';
import '../../state/app_controller.dart';
import '../widgets/osmium_motion_widgets.dart';
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

  final _emailCtrl = TextEditingController();
  final _passwordCtrl = TextEditingController();
  final _usernameCtrl = TextEditingController();
  final _codeCtrl = TextEditingController();

  @override
  void dispose() {
    _cooldownTimer?.cancel();
    _emailCtrl.dispose();
    _passwordCtrl.dispose();
    _usernameCtrl.dispose();
    _codeCtrl.dispose();
    super.dispose();
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
  /// terminal success and the account service persisted the session.
  Future<void> _finishCaptchaSignIn(
    AccountChallengeInfo challenge,
    String fallbackError,
  ) async {
    final verified = await _runCaptcha(challenge);
    if (!mounted || verified == null) return;
    if (ref.read(accountServiceProvider).isLoggedIn) {
      Navigator.of(context).pop(true);
    } else {
      setState(() => _error = fallbackError);
    }
  }

  Future<void> _sendRegisterCode() async {
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

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;

    return MiuixScaffold(
      topBar: MiuixSmallTopAppBar(
        title: _isRegister ? '注册账号' : '登录 Osmium',
        navigationIcon: IconButton(
          icon: Icon(
            Icons.arrow_back_ios_new_rounded,
            size: 20,
            color: colors.onSurface,
          ),
          onPressed: () => Navigator.of(context).pop(),
        ),
      ),
      content: (contentPadding) {
        return ListView(
          padding: contentPadding.add(
            const EdgeInsets.fromLTRB(20, 12, 20, 32),
          ),
          children: [
            Center(
              child: ClipRRect(
                borderRadius: BorderRadius.circular(20),
                child: Image.asset(
                  'assets/images/osmium_logo.png',
                  width: 68,
                  height: 68,
                  fit: BoxFit.cover,
                ),
              ),
            ),
            const SizedBox(height: 24),
            _ModeToggle(
              isRegister: _isRegister,
              onChanged: (v) => setState(() {
                _isRegister = v;
                _error = null;
              }),
            ),
            const SizedBox(height: 18),
            MiuixCard(
              cornerRadius: 20,
              insideMargin: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  MiuixTextField(
                    controller: _emailCtrl,
                    label: '邮箱',
                    singleLine: true,
                    keyboardType: TextInputType.emailAddress,
                    textInputAction: TextInputAction.next,
                  ),
                  OsmiumAnimatedReveal(
                    visible: _isRegister,
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        const SizedBox(height: 12),
                        MiuixTextField(
                          controller: _usernameCtrl,
                          label: '昵称',
                          singleLine: true,
                          textInputAction: TextInputAction.next,
                        ),
                        const SizedBox(height: 12),
                        Row(
                          children: [
                            Expanded(
                              child: MiuixTextField(
                                controller: _codeCtrl,
                                label: '验证码',
                                singleLine: true,
                                keyboardType: TextInputType.number,
                                textInputAction: TextInputAction.next,
                              ),
                            ),
                            const SizedBox(width: 10),
                            MiuixTextButton(
                              _codeCooldown > 0 ? '${_codeCooldown}s' : '获取',
                              enabled: !_sendingCode && _codeCooldown == 0,
                              onPressed: _sendRegisterCode,
                              minHeight: 54,
                            ),
                          ],
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(height: 12),
                  MiuixTextField(
                    controller: _passwordCtrl,
                    label: '密码',
                    singleLine: true,
                    obscureText: _obscurePassword,
                    textInputAction: TextInputAction.done,
                    onSubmitted: (_) => _submit(),
                    trailingIcon: IconButton(
                      icon: Icon(
                        _obscurePassword
                            ? Icons.visibility_off_rounded
                            : Icons.visibility_rounded,
                        size: 20,
                        color: colors.onSurfaceVariantSummary,
                      ),
                      onPressed: () {
                        OsmiumHaptics.selection();
                        setState(() => _obscurePassword = !_obscurePassword);
                      },
                    ),
                  ),
                  OsmiumAnimatedReveal(
                    visible: _error != null,
                    child: Padding(
                      padding: const EdgeInsets.only(top: 12),
                      child: Container(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 12,
                          vertical: 10,
                        ),
                        decoration: BoxDecoration(
                          color: OsmiumColors.dangerBg(isDark),
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
                  const SizedBox(height: 18),
                  SizedBox(
                    width: double.infinity,
                    child: MiuixButton(
                      onPressed: _submit,
                      colors: MiuixButtonDefaults.buttonColorsPrimary(context),
                      minHeight: 50,
                      child: AnimatedSwitcher(
                        duration: OsmiumMotion.durationOr(
                          context,
                          OsmiumMotion.small,
                        ),
                        switchInCurve: MiuixMotion.standardDecelerate,
                        switchOutCurve: MiuixMotion.standardAccelerate,
                        transitionBuilder: (child, animation) => FadeTransition(
                          opacity: animation,
                          child: ScaleTransition(
                            scale: Tween<double>(
                              begin: 0.9,
                              end: 1,
                            ).animate(animation),
                            child: child,
                          ),
                        ),
                        child: _submitting
                            ? SizedBox(
                                key: const ValueKey('submitting'),
                                width: 20,
                                height: 20,
                                child: CircularProgressIndicator(
                                  strokeWidth: 2.2,
                                  color: colors.onPrimary,
                                ),
                              )
                            : Text(
                                _isRegister ? '注册并登录' : '登录',
                                key: const ValueKey('idle'),
                              ),
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ],
        );
      },
    );
  }
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

    return Container(
      height: 46,
      padding: const EdgeInsets.all(4),
      decoration: BoxDecoration(
        color: colors.surfaceContainer,
        borderRadius: BorderRadius.circular(14),
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
                  color: colors.primary,
                  borderRadius: BorderRadius.circular(11),
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
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
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
            child: MiuixText(
              label,
              style: theme.textStyles.headline2,
              color: Color.lerp(
                colors.onSurfaceVariantSummary,
                colors.onPrimary,
                reduced ? (active > 0.5 ? 1 : 0) : active,
              ),
            ),
          );
        },
      ),
    );
  }
}
