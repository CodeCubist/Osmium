import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/account_models.dart';
import '../../core/models/osmium_settings.dart';
import '../../services/session/remote_session_controller.dart';
import '../../state/app_controller.dart';
import '../auth/captcha_webview_page.dart';
import 'widgets/session_signals.dart';
import 'widgets/session_view.dart';
import 'widgets/virtual_keyboard_overlay.dart';

class RemoteSessionPage extends ConsumerStatefulWidget {
  const RemoteSessionPage({super.key, required this.request});

  final SessionConnectRequest request;

  @override
  ConsumerState<RemoteSessionPage> createState() => _RemoteSessionPageState();
}

class _RemoteSessionPageState extends ConsumerState<RemoteSessionPage> {
  late final RemoteSessionController _session;
  late final SessionSignals _signals;
  bool _panelOpen = false;
  KeyboardOverlayMode _keyboardMode = KeyboardOverlayMode.none;

  @override
  void initState() {
    super.initState();
    final app = ref.read(appControllerProvider);
    _session = RemoteSessionController(
      engine: ref.read(osmiumEngineProvider),
      platformBridge: ref.read(platformBridgeProvider),
      signaling: ref.read(signalingClientProvider),
      initialSettings: app.settings,
      solveCaptcha: _solveCaptcha,
      refreshAuthToken: app.refreshLtokenForRemoteConnect,
      onDesktopWallpaperReceived: (deviceId, bytes) {
        app.saveDeviceWallpaper(deviceId, bytes);
      },
    );
    _signals = SessionSignals(_session);

    scheduleMicrotask(_startConnect);
  }

  Future<void> _startConnect() async {
    final app = ref.read(appControllerProvider);
    app.recordConnectionHistory(
      deviceId: widget.request.targetDeviceId,
      deviceName: widget.request.targetDeviceName,
      savedAccessCode: widget.request.sameAccount
          ? ''
          : widget.request.accessCode,
      sameAccount: widget.request.sameAccount,
    );
    String ltoken = app.accountService.currentLtoken;
    final sess = app.accountService.session;
    if (app.accountService.isLoggedIn &&
        (ltoken.isEmpty ||
            sess == null ||
            sess.isLtokenExpiringSoon ||
            !app.signaling.isAccountAuthenticated)) {
      ltoken = (await app.refreshLtokenForRemoteConnect()) ??
          app.accountService.currentLtoken;
    } else {
      await app.accountService.ensureAuthenticated();
      ltoken = app.accountService.currentLtoken;
    }
    if (!mounted) return;
    await _session.connect(
      widget.request,
      ltoken: ltoken,
    );
  }

  Future<bool> _solveCaptcha(AccountChallengeInfo challenge) async {
    if (!mounted) return false;
    final result = await Navigator.of(context).push<Map<String, dynamic>>(
      MaterialPageRoute(
        builder: (_) => CaptchaWebViewPage(
          challenge: challenge,
          accountService: ref.read(accountServiceProvider),
        ),
      ),
    );
    return result != null;
  }

  @override
  void dispose() {
    _signals.dispose();
    _session.dispose();
    super.dispose();
  }

  Future<void> _exitSession() async {
    await _session.disconnect();
    if (mounted) Navigator.of(context).pop();
  }

  void _updateSettings(OsmiumSettings next) {
    ref.read(appControllerProvider).updateSettings(next);
    _session.applySettings(next);
  }

  void _toggleControlMode() {
    final cur = ref.read(appControllerProvider).settings;
    final nextMode = cur.controlMode == ControlMode.directTouch
        ? ControlMode.precisionMouse
        : ControlMode.directTouch;
    _updateSettings(cur.copyWith(controlMode: nextMode));
  }

  void _openPanel() {
    setState(() {
      _keyboardMode = KeyboardOverlayMode.none;
      _panelOpen = true;
    });
  }

  void _closePanel() {
    if (_panelOpen) setState(() => _panelOpen = false);
  }

  void _setKeyboardMode(KeyboardOverlayMode mode) {
    setState(() {
      if (mode != KeyboardOverlayMode.none) _panelOpen = false;
      _keyboardMode = mode;
    });
  }

  @override
  Widget build(BuildContext context) {
    final settings = ref.watch(
      appControllerProvider.select((app) => app.settings),
    );

    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (didPop, _) async {
        if (didPop) return;
        if (_panelOpen) {
          _closePanel();
          return;
        }
        if (_keyboardMode != KeyboardOverlayMode.none) {
          setState(() => _keyboardMode = KeyboardOverlayMode.none);
          return;
        }
        await _exitSession();
      },
      child: Scaffold(
        backgroundColor: Colors.black,
        resizeToAvoidBottomInset: false,
        body: SessionView(
          controller: _session,
          signals: _signals,
          settings: settings,
          panelOpen: _panelOpen,
          keyboardMode: _keyboardMode,
          onOpenPanel: _openPanel,
          onClosePanel: _closePanel,
          onToggleControlMode: _toggleControlMode,
          onKeyboardModeChanged: _setKeyboardMode,
          onSettingsChanged: _updateSettings,
          onDisconnect: _exitSession,
          onRetry: _startConnect,
        ),
      ),
    );
  }
}
