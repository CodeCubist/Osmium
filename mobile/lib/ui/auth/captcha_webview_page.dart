import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_miuix/miuix.dart';
import 'package:webview_flutter/webview_flutter.dart';

import '../../core/models/account_models.dart';
import '../../core/theme/osmium_motion.dart';
import '../../services/account/account_service.dart';

/// Displays the server-issued Captcha page and polls until verification completes.
class CaptchaWebViewPage extends StatefulWidget {
  const CaptchaWebViewPage({
    super.key,
    required this.challenge,
    required this.accountService,
  });

  final AccountChallengeInfo challenge;
  final AccountService accountService;

  @override
  State<CaptchaWebViewPage> createState() => _CaptchaWebViewPageState();
}

class _CaptchaWebViewPageState extends State<CaptchaWebViewPage> {
  static const _resultPathHint = '/api/auth/result';
  static const _pollInterval = Duration(milliseconds: 1200);
  static const _pollBurstDelay = Duration(milliseconds: 250);
  static const _pollBurstAttempts = 6;

  late final WebViewController _webController;
  late final Uri _captchaUri;
  DateTime? _deadline;
  Timer? _pollTimer;
  bool _loading = true;
  bool _polling = false;
  bool _completed = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _captchaUri = widget.accountService.resolveUri(widget.challenge.captchaUrl);
    if (widget.challenge.expiresIn > 0) {
      _deadline = DateTime.now().add(
        Duration(seconds: widget.challenge.expiresIn),
      );
    }

    _webController = WebViewController()
      ..setJavaScriptMode(JavaScriptMode.unrestricted)
      ..setNavigationDelegate(
        NavigationDelegate(
          onNavigationRequest: _onNavigationRequest,
          onPageStarted: (_) {
            if (mounted) setState(() => _loading = true);
          },
          onPageFinished: (_) {
            if (mounted) setState(() => _loading = false);
          },
          onWebResourceError: (error) {
            if (error.isForMainFrame == true && mounted) {
              setState(() => _loading = false);
            }
          },
        ),
      )
      ..loadRequest(_captchaUri);

    _pollTimer = Timer.periodic(_pollInterval, (_) => _pollOnce());
  }

  /// The captcha provider redirects to `/api/auth/result` once solved. That URL
  /// is JSON, so keep it out of the WebView and poll immediately instead.
  NavigationDecision _onNavigationRequest(NavigationRequest request) {
    final uri = Uri.tryParse(request.url);
    if (uri == null) return NavigationDecision.prevent;
    if (uri.scheme == 'about') return NavigationDecision.navigate;
    if (!_isAllowedNavigation(uri)) return NavigationDecision.prevent;
    if (_isResultUri(uri)) {
      unawaited(_pollBurst());
      return NavigationDecision.prevent;
    }
    return NavigationDecision.navigate;
  }

  bool _isAllowedNavigation(Uri uri) {
    if (uri.scheme != 'https' && uri.scheme != _captchaUri.scheme) {
      return false;
    }
    if (uri.host == _captchaUri.host && uri.port == _captchaUri.port) {
      return true;
    }
    final host = uri.host.toLowerCase();
    return host == 'geetest.com' ||
        host.endsWith('.geetest.com') ||
        host == 'geevisit.com' ||
        host.endsWith('.geevisit.com') ||
        host == 'gsensebot.com' ||
        host.endsWith('.gsensebot.com');
  }

  bool _isResultUri(Uri uri) =>
      uri.host == _captchaUri.host && uri.path.contains(_resultPathHint);

  Future<void> _pollBurst() async {
    for (var attempt = 0; attempt < _pollBurstAttempts; attempt++) {
      if (_completed || !mounted) return;
      await _pollOnce();
      if (_completed || _error != null) return;
      await Future<void>.delayed(_pollBurstDelay);
    }
  }

  Future<void> _pollOnce() async {
    if (_polling || _completed || !mounted) return;
    if (_deadline != null && DateTime.now().isAfter(_deadline!)) {
      _fail('验证已失效，请重试');
      return;
    }

    _polling = true;
    try {
      final res = await widget.accountService.pollChallenge(
        challenge: widget.challenge,
      );
      if (!mounted || _completed) return;

      if (res.ok) {
        _completed = true;
        _pollTimer?.cancel();
        Navigator.of(context).pop(res.data ?? const <String, dynamic>{});
      } else if (res.isPending) {
        // Code 101: the user is still solving the challenge.
      } else if (res.httpStatusCode == 0) {
        // Transient network failure; keep polling until the deadline.
      } else {
        _fail(res.message.isNotEmpty ? res.message : '验证已失效');
      }
    } finally {
      _polling = false;
    }
  }

  void _fail(String message) {
    if (_completed || !mounted) return;
    _pollTimer?.cancel();
    setState(() => _error = message);
  }

  @override
  void dispose() {
    _pollTimer?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;

    return MiuixScaffold(
      topBar: MiuixSmallTopAppBar(
        title: '安全验证',
        navigationIcon: IconButton(
          icon: Icon(Icons.close_rounded, color: colors.onSurface),
          onPressed: () => Navigator.of(context).pop(),
        ),
        actions: [
          IconButton(
            icon: Icon(Icons.refresh_rounded, color: colors.onSurface),
            onPressed: () => _webController.reload(),
          ),
        ],
      ),
      content: (contentPadding) {
        return Padding(
          padding: contentPadding,
          child: Stack(
            children: [
              WebViewWidget(controller: _webController),
              Positioned(
                top: 0,
                left: 0,
                right: 0,
                child: AnimatedOpacity(
                  opacity: _loading ? 1 : 0,
                  duration: OsmiumMotion.durationOr(
                    context,
                    const Duration(milliseconds: 180),
                  ),
                  curve: MiuixMotion.standardDecelerate,
                  child: const LinearProgressIndicator(minHeight: 2),
                ),
              ),
              Positioned.fill(
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
                        begin: 0.96,
                        end: 1,
                      ).animate(animation),
                      child: child,
                    ),
                  ),
                  child: _error == null
                      ? const SizedBox.shrink(key: ValueKey('ok'))
                      : Container(
                          key: const ValueKey('error'),
                          color: colors.background.withValues(alpha: 0.94),
                          alignment: Alignment.center,
                          padding: const EdgeInsets.all(24),
                          child: Column(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              Icon(
                                Icons.error_outline_rounded,
                                size: 40,
                                color: colors.onSurfaceVariantSummary,
                              ),
                              const SizedBox(height: 12),
                              MiuixText(
                                _error!,
                                style: theme.textStyles.body1,
                                textAlign: TextAlign.center,
                              ),
                              const SizedBox(height: 20),
                              MiuixTextButton(
                                '关闭',
                                onPressed: () => Navigator.of(context).pop(),
                                colors: MiuixButtonDefaults.buttonColorsPrimary(
                                  context,
                                ),
                              ),
                            ],
                          ),
                        ),
                ),
              ),
            ],
          ),
        );
      },
    );
  }
}
