import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/physics.dart';
import 'package:flutter_miuix/miuix.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/account_models.dart';
import '../../core/theme/osmium_motion.dart';
import '../../core/theme/osmium_theme.dart';
import '../../state/app_controller.dart';
import '../shared/home_widgets.dart';
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
                                onPressed: _submit,
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
                                  child: _submitting
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
          ],
        );
      },
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
