import 'dart:async';
import 'dart:convert';
import 'dart:ffi';
import 'dart:io';
import 'dart:isolate';
import 'dart:math';
import 'dart:typed_data';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:permission_handler/permission_handler.dart';
import 'package:wakelock_plus/wakelock_plus.dart';

import '../../core/constants/osmium_constants.dart';
import '../../core/models/account_models.dart';
import '../../core/models/device_identity.dart';
import '../../core/models/host_status.dart';
import '../../core/models/osmium_settings.dart';
import '../engine/osmium_engine.dart';
import '../engine/platform_bridge.dart';
import '../signaling/signaling_client.dart';
import 'aux_channel.dart';

enum SessionPhase {
  idle,
  requesting,
  authenticating,
  negotiatingTransport,
  connectingRelay,
  startingStream,
  streaming,
  ended,
  failed,
}

class SessionConnectRequest {
  const SessionConnectRequest({
    required this.targetDeviceId,
    required this.targetDeviceName,
    this.accessCode = '',
    this.sameAccount = false,
    this.viewOnly = false,
  });

  final String targetDeviceId;
  final String targetDeviceName;
  final String accessCode;
  final bool sameAccount;
  final bool viewOnly;
}

/// Runs one FFI call that owns a native transport. Production defers it to a
/// worker isolate; tests run it inline against a fake engine.
typedef TransportOpRunner = Future<T> Function<T>(
  int transportAddr,
  FutureOr<T> Function() op,
);

/// Runs a transport-independent FFI call (STUN gather / socket bind).
typedef EngineOpRunner = Future<T> Function<T>(FutureOr<T> Function() op);

Future<T> _defaultTransportOpRunner<T>(
  int transportAddr,
  FutureOr<T> Function() op,
) => Isolate.run(op);

Future<T> _defaultEngineOpRunner<T>(FutureOr<T> Function() op) =>
    Isolate.run(op);

// Isolate.run copies the closure's entire captured context. A closure created
// inside `_connect` drags along its completers and subscriptions, which are
// unsendable, so worker-isolate ops are built here and capture only arguments.
(int, String)? Function() _createTransportOp(
  OsmiumEngine Function() engineFactory,
  String stunHost,
  int stunPort,
) => () {
  final created = engineFactory().createTransport(
    stunHost: stunHost,
    stunPort: stunPort,
  );
  if (created == null) return null;
  return (created.handle.address, created.candidatesJson);
};

(bool, int, String) Function() _connectPeerOp(
  OsmiumEngine Function() engineFactory,
  int transportAddr,
  Uint8List sessionToken,
  String peerCandidatesJson,
  int timeoutMs,
) => () {
  final res = engineFactory().connectPeerP2P(
    transport: Pointer<Void>.fromAddress(transportAddr),
    sessionToken16: sessionToken,
    peerCandidatesJson: peerCandidatesJson,
    timeoutMs: timeoutMs,
  );
  return (res.success, res.route, res.endpoint);
};

(int, String) Function() _authenticateOp(
  OsmiumEngine Function() engineFactory,
  int transportAddr,
  Uint8List authTag,
  OsmiumSettings settings,
  int timeoutMs,
) =>
    () => engineFactory().authenticateClient(
      transport: Pointer<Void>.fromAddress(transportAddr),
      authTag32: authTag,
      settings: settings,
      timeoutMs: timeoutMs,
    );

/// Shows the security captcha for a connect challenge; resolves true once the
/// server marked it solved.
typedef ConnectCaptchaSolver = Future<bool> Function(
  AccountChallengeInfo challenge,
);

const String _accountRequiredText = '仅已登录且状态正常的账户可发起远程控制，被控无需登录';
const String _captchaRequiredText = '当日远控次数已达安全验证阈值，请先完成人机验证';

/// Mirrors Windows `P2PCoordinator.FormatReject` (zh-CN strings).
String formatConnectReject(ConnectRejectEvent rej) {
  switch (int.tryParse(rej.code.trim())) {
    case OsmiumErrorCode.accountAuthRequired:
      return _accountRequiredText;
    case OsmiumErrorCode.accountLocked:
      return '该账户当前处于安全锁定状态，请先解锁';
    case OsmiumErrorCode.blacklistedByHost:
      return '被控端账户已将您拉黑，无法发起远程控制';
    case OsmiumErrorCode.captchaRequired:
      return _captchaRequiredText;
    case OsmiumErrorCode.connectCooldown:
      final waitSeconds = max(1, (max(1000, rej.cooldownMs) / 1000).ceil());
      return '操作过于频繁，请等待 $waitSeconds 秒后再试';
    case OsmiumErrorCode.deviceOffline:
      return '目标设备当前不在线';
    case OsmiumErrorCode.remoteDisabled:
      return '目标设备已关闭允许远程控制';
    case OsmiumErrorCode.selfConnect:
      return '不能连接本机自身';
    case OsmiumErrorCode.rateLimited:
      return '请求过于频繁，请稍后再试';
  }

  // Host-originated string codes; the host sends English reasons.
  switch (rej.code.trim().toUpperCase()) {
    case 'REMOTE_DISABLED':
      return '目标设备已关闭允许远程控制';
    case 'ENCRYPTION_MISMATCH':
      return '加密传输设置不一致，需被控端与主控端同时开启或关闭';
    case 'INVALID_ACCESS_CODE':
      return '远控代码不正确，对方拒绝接入';
    case 'HOST_BUSY':
      return '目标设备正在被其他设备远程控制';
    case 'USER_DECLINED':
      return '对方已拒绝本次连接';
  }

  if (rej.reason.trim().isNotEmpty) return rej.reason.trim();
  return rej.code.trim().isEmpty ? '目标主机拒绝连接' : '连接被拒绝 - ${rej.code.trim()}';
}

class RemoteSessionController extends ChangeNotifier {
  RemoteSessionController({
    required this.engine,
    required this.platformBridge,
    required this.signaling,
    required OsmiumSettings initialSettings,
    this.solveCaptcha,
    this.onDesktopWallpaperReceived,
    OsmiumEngine Function()? isolateEngineFactory,
    TransportOpRunner? transportOpRunner,
    EngineOpRunner? engineOpRunner,
  }) : _settings = initialSettings,
       _isolateEngineFactory = isolateEngineFactory ?? OsmiumEngine.new,
       _transportOpRunner = transportOpRunner ?? _defaultTransportOpRunner,
       _engineOpRunner = engineOpRunner ?? _defaultEngineOpRunner;

  final OsmiumEngine engine;
  final PlatformBridge platformBridge;
  final SignalingClient signaling;

  final ConnectCaptchaSolver? solveCaptcha;
  final void Function(String deviceId, Uint8List wallpaperBytes)?
  onDesktopWallpaperReceived;
  final OsmiumEngine Function() _isolateEngineFactory;
  final TransportOpRunner _transportOpRunner;
  final EngineOpRunner _engineOpRunner;

  OsmiumSettings _settings;
  OsmiumSettings get settings => _settings;

  SessionPhase _phase = SessionPhase.idle;
  SessionPhase get phase => _phase;

  String _statusText = '';
  String get statusText => _statusText;

  String? _errorMessage;
  String? get errorMessage => _errorMessage;

  AccountChallengeInfo? _pendingCaptcha;
  AccountChallengeInfo? get pendingCaptcha => _pendingCaptcha;

  SessionConnectRequest? _request;
  SessionConnectRequest? get request => _request;

  String _sessionId = '';
  String _hostName = '';
  String get hostName =>
      _hostName.isNotEmpty ? _hostName : (_request?.targetDeviceName ?? '');

  int? _textureId;
  int? get textureId => _textureId;

  int _surfaceHandleId = 0;

  SessionStatsSnapshot _stats = const SessionStatsSnapshot();
  SessionStatsSnapshot get stats => _stats;

  HostSessionStatusDto _hostStatus = const HostSessionStatusDto();
  HostSessionStatusDto get hostStatus => _hostStatus;

  HostTextFocus _textFocus = HostTextFocus.none;
  HostTextFocus get textFocus => _textFocus;
  bool _textFocusAnswered = false;
  Timer? _textFocusTimer;

  bool _audioMuted = false;
  bool get audioMuted => _audioMuted;

  bool _virtualSpeaker = false;
  bool get virtualSpeaker => _virtualSpeaker;

  bool _micEnabled = false;
  bool get micEnabled => _micEnabled;

  bool _pointerCaptureActive = false;
  bool get pointerCaptureActive => _pointerCaptureActive;

  bool _viewOnly = false;
  bool get viewOnly => _viewOnly;

  /// The host granted this session view-only, so input can't be turned back on.
  bool get viewOnlyLocked => _request?.viewOnly ?? false;

  int _orientationMode = 0; // 0=auto, 1=portrait, 2=landscape
  int get orientationMode => _orientationMode;

  AuxTransferProgressEvent? _lastTransferProgress;
  AuxTransferProgressEvent? get lastTransferProgress => _lastTransferProgress;

  String? _toastMessage;
  String? get toastMessage => _toastMessage;

  Pointer<Void>? _transportHandle;
  Pointer<Void>? _sessionHandle;
  NativeKeyPair? _keyPair;

  WebSocket? _relayWs;
  Timer? _relayPumpTimer;
  Timer? _eventPollTimer;
  Timer? _statsTimer;

  AuxTransferChannel? _auxChannel;
  StreamSubscription<NativeInputEvent>? _nativeInputSub;
  StreamSubscription<(String, String)>? _sessionEndSub;
  StreamSubscription<String>? _sessionEstablishedSub;
  StreamSubscription<String>? _auxClipSub;
  StreamSubscription<List<String>>? _auxFilesSub;
  StreamSubscription<Uint8List>? _auxWallpaperSub;
  StreamSubscription<AuxTransferProgressEvent>? _auxProgressSub;

  bool _disposed = false;
  bool _cancelling = false;
  bool _hasReceivedFirstFrame = false;
  bool get hasReceivedFirstFrame => _hasReceivedFirstFrame;

  // `bridge_ready` reached means the host already holds a session handle for
  // us, so a failure must be reported with session_end(failed:true) or the host
  // keeps the slot reserved and rejects the retry with HOST_BUSY.
  bool _bridgeReached = false;
  bool _signalingEndSent = false;
  bool _sessionConfirmed = false;
  bool _sessionEstablished = false;
  bool _reliableAuxTransport = false;
  int _relayAttempts = 0;

  // Every connect() run gets a generation. Stale runs may finish their awaits
  // but must never mutate session state or tear down a newer session.
  int _connectEpoch = 0;

  // Native transport addresses referenced by in-flight Isolate.run FFI calls.
  // Cleanup defers destroyTransport for these addresses so a cancel cannot free
  // a transport while connectPeerP2P/authenticateClient is still using it.
  final Set<int> _nativeOpsInFlight = <int>{};
  final Map<int, Pointer<Void>> _deferredTransportDestroy =
      <int, Pointer<Void>>{};

  bool _isStale(int epoch) =>
      _disposed || epoch != _connectEpoch || _cancelling;

  Future<T> _runTransportOp<T>(
    int transportAddr,
    FutureOr<T> Function() op,
  ) async {
    _nativeOpsInFlight.add(transportAddr);
    try {
      return await _transportOpRunner<T>(transportAddr, op);
    } finally {
      _nativeOpsInFlight.remove(transportAddr);
      final deferred = _deferredTransportDestroy.remove(transportAddr);
      if (deferred != null) {
        engine.destroyTransport(deferred);
      }
    }
  }

  void clearToast() {
    if (_toastMessage != null) {
      _toastMessage = null;
      notifyListeners();
    }
  }

  void _emitToast(String msg) {
    _toastMessage = msg;
    notifyListeners();
  }

  void _setPhase(SessionPhase next, String label) {
    if (_disposed) return;
    _phase = next;
    _statusText = label;
    notifyListeners();
  }

  Future<void> connect(SessionConnectRequest req, {required String ltoken}) =>
      _connect(req, ltoken: ltoken, captchaRetried: false);

  Future<void> _connect(
    SessionConnectRequest req, {
    required String ltoken,
    required bool captchaRetried,
  }) async {
    await disconnect(notifySignaling: false, resetState: false);

    final epoch = ++_connectEpoch;
    _cancelling = false;
    _request = req;
    _hostName = req.targetDeviceName;
    _viewOnly = req.viewOnly;
    _errorMessage = null;
    _pendingCaptcha = null;
    _hasReceivedFirstFrame = false;
    _bridgeReached = false;
    _signalingEndSent = false;
    _sessionConfirmed = false;
    _sessionEstablished = false;
    _reliableAuxTransport = false;
    _relayAttempts = 0;

    bool aborted() => _isStale(epoch);

    if (!engine.isAvailable) {
      _fail('原生引擎库加载失败，仅支持 Android 设备运行');
      return;
    }

    if (signaling.state != SignalingConnectionState.connected) {
      // The socket may still be registering (app cold start or reconnect).
      // Windows queues the request instead of failing immediately.
      try {
        await signaling.stateStream
            .firstWhere((s) => s == SignalingConnectionState.connected)
            .timeout(const Duration(seconds: 6));
      } catch (_) {}
      if (aborted()) return;
      if (signaling.state != SignalingConnectionState.connected) {
        _fail('信令服务未连接，请检查网络后重试');
        return;
      }
    }

    if (ltoken.trim().isEmpty) {
      _fail(_accountRequiredText);
      return;
    }

    try {
      await WakelockPlus.enable();
    } catch (_) {}
    if (aborted()) return;

    await platformBridge.setImmersiveMode(true);
    if (aborted()) return;

    // Generate 16-byte session token & uppercase hex sessionId (matches Osmium C# Convert.ToHexString)
    final rng = Random.secure();
    final sessionToken = Uint8List.fromList(
      List<int>.generate(16, (_) => rng.nextInt(256)),
    );
    final sid = sessionToken
        .map((b) => b.toRadixString(16).padLeft(2, '0'))
        .join()
        .toUpperCase();
    _sessionId = sid;

    _setPhase(SessionPhase.requesting, '正在联络目标主机');

    // Step 1: Generate ephemeral ECDH P-256 keypair
    _keyPair = engine.generateKeyPair();
    if (_keyPair == null) {
      _fail('密钥生成失败');
      return;
    }

    // Set up signaling listeners scoped to this run's sessionId.
    final challengeComp = Completer<ConnectChallengeEvent>();
    final authVerifiedComp = Completer<void>();
    final versionComp = Completer<VersionCheckResultEvent>();
    final bridgeComp = Completer<BridgeReadyEvent>();
    final relayFallbackComp = Completer<RelayFallbackEvent>();
    final rejectComp = Completer<ConnectRejectEvent>();

    final subs = <StreamSubscription<dynamic>>[
      signaling.challengeStream.listen((e) {
        if (e.sessionId == sid && !challengeComp.isCompleted) {
          challengeComp.complete(e);
        }
      }),
      signaling.authVerifiedStream.listen((eventSid) {
        if (eventSid == sid && !authVerifiedComp.isCompleted) {
          authVerifiedComp.complete();
        }
      }),
      signaling.versionResultStream.listen((e) {
        if (e.sessionId == sid && !versionComp.isCompleted) {
          versionComp.complete(e);
        }
      }),
      signaling.bridgeReadyStream.listen((e) {
        if (e.sessionId == sid && !bridgeComp.isCompleted) {
          bridgeComp.complete(e);
        }
      }),
      signaling.relayFallbackStream.listen((e) {
        if (e.sessionId == sid && !relayFallbackComp.isCompleted) {
          relayFallbackComp.complete(e);
        }
      }),
      signaling.rejectStream.listen((e) {
        if (e.sessionId == sid && !rejectComp.isCompleted) {
          rejectComp.complete(e);
        }
      }),
    ];

    // Set when the server asks for a captcha before forwarding the request;
    // Windows solves it and resends connect_request exactly once.
    AccountChallengeInfo? captchaToSolve;

    Future<T?> awaitOrReject<T>(
      Future<T> target,
      Duration timeout, {
      bool allowCaptchaRetry = false,
    }) async {
      final result = await Future.any<Object?>([
        target,
        rejectComp.future,
        Future<Object?>.delayed(timeout, () => const _TimeoutSentinel()),
      ]);
      if (aborted()) return null;
      if (result is ConnectRejectEvent) {
        final challenge = result.challenge;
        if (allowCaptchaRetry &&
            !captchaRetried &&
            challenge != null &&
            solveCaptcha != null &&
            int.tryParse(result.code.trim()) ==
                OsmiumErrorCode.captchaRequired) {
          captchaToSolve = challenge;
          return null;
        }
        _pendingCaptcha = challenge;
        _fail(formatConnectReject(result));
        return null;
      }
      if (result is _TimeoutSentinel) {
        _fail('连接超时，请确认目标设备在线');
        return null;
      }
      return result as T;
    }

    Timer? bridgeRetry;
    try {
      // Step 2: Send connect_request
      signaling.sendConnectRequest(
        sessionId: sid,
        targetDeviceId: req.targetDeviceId,
        clientPublicKeyBase64: _keyPair!.publicKeyBase64,
        enableEncryption: _settings.enableEncryption,
        ltoken: ltoken,
        sameAccountConnect: req.sameAccount,
        viewOnly: req.viewOnly,
      );

      // Step 3: Await connect_challenge (Windows uses a 20s budget)
      final challenge = await awaitOrReject(
        challengeComp.future,
        const Duration(seconds: 20),
        allowCaptchaRetry: true,
      );
      if (aborted()) return;
      final captcha = captchaToSolve;
      if (captcha != null) {
        await _retryAfterCaptcha(req, ltoken, captcha, epoch);
        return;
      }
      if (challenge == null) return;
      if (challenge.hostName.isNotEmpty) {
        _hostName = challenge.hostName;
      }

      // Windows rejects any host/controller encryption mismatch before ECDH.
      if (challenge.enableEncryption != _settings.enableEncryption) {
        _fail('加密设置不一致：主机和控制器必须同时启用或关闭加密传输');
        return;
      }

      // Step 4: Derive ECDH + HKDF-SHA256 AES key & HMAC auth tag
      _setPhase(SessionPhase.authenticating, '正在验证安全密钥');
      final peerPubBytes = base64Decode(challenge.hostPublicKeyBase64);
      final nonceBytes = base64Decode(challenge.hostChallengeNonceBase64);
      final effectiveCode =
          (req.sameAccount &&
              (challenge.sameAccountAuthSecret?.isNotEmpty ?? false))
          ? challenge.sameAccountAuthSecret!
          : DeviceIdentity.normalizeAccessCode(req.accessCode);
      if (effectiveCode.trim().isEmpty) {
        _fail('访问码无效');
        return;
      }

      final material = engine.deriveSessionMaterial(
        keyHandle: _keyPair!.handle,
        peerPubSpkiDer: peerPubBytes,
        sessionToken16: sessionToken,
        challengeNonce: nonceBytes,
        accessCode: effectiveCode,
      );
      engine.destroyKeyPair(_keyPair!.handle);
      _keyPair = null;

      if (material == null) {
        _fail('密钥协商失败');
        return;
      }

      // Step 5: Send connect_auth & await connect_auth_verified
      signaling.sendConnectAuth(
        sessionId: sid,
        targetDeviceId: req.targetDeviceId,
        authTagBase64: material.authTagBase64,
      );
      final verified = await awaitOrReject(
        authVerifiedComp.future.then((_) => true),
        const Duration(seconds: 15),
      );
      if (verified != true || aborted()) return;

      // Step 6: Protocol version check
      signaling.sendVersionReport(sessionId: sid);
      final verRes = await awaitOrReject(
        versionComp.future,
        const Duration(seconds: 15),
      );
      if (verRes == null || aborted()) return;
      if (!verRes.compatible) {
        _fail(verRes.reason.isNotEmpty ? verRes.reason : '协议版本不兼容');
        return;
      }

      // Step 7: Create UDP transport & gather STUN candidates off UI thread
      _setPhase(SessionPhase.negotiatingTransport, '正在探测网络直连');
      final stunHost = challenge.stunHost.isNotEmpty
          ? challenge.stunHost
          : signaling.advertisedStunHost;
      final stunPort = challenge.stunPort > 0
          ? challenge.stunPort
          : signaling.advertisedStunPort;

      // Bind the socket + gather STUN candidates off the UI thread.
      final engineFactory = _isolateEngineFactory;
      final transportRes = await _engineOpRunner(
        _createTransportOp(engineFactory, stunHost, stunPort),
      );

      if (transportRes == null) {
        if (!aborted()) _fail('网络传输初始化失败');
        return;
      }
      final transportPtr = Pointer<Void>.fromAddress(transportRes.$1);
      if (aborted()) {
        // Cancelled while the isolate was creating the transport: it was never
        // published to this controller, so destroy it here.
        engine.destroyTransport(transportPtr);
        return;
      }
      _transportHandle = transportPtr;
      final transportAddr = transportPtr.address;
      final localCandidatesJson = transportRes.$2;

      // Step 8: Send candidates & await bridge_ready, retrying like Windows
      // every 4s in case the host's candidate message was not ready yet.
      signaling.sendBridgeCandidates(
        sessionId: sid,
        candidatesJson: localCandidatesJson,
      );
      bridgeRetry = Timer.periodic(const Duration(seconds: 4), (_) {
        if (!aborted() && !bridgeComp.isCompleted) {
          signaling.sendBridgeCandidates(
            sessionId: sid,
            candidatesJson: localCandidatesJson,
          );
        }
      });
      final bridge = await awaitOrReject(
        bridgeComp.future,
        const Duration(seconds: 18),
      );
      bridgeRetry.cancel();
      bridgeRetry = null;
      if (bridge == null || aborted()) return;
      _bridgeReached = true;

      final peerCandidates = bridge.candidatesJson;
      const maxDirectAttempts = 3;
      var p2p = (false, 0, '');
      for (var attempt = 1; attempt <= maxDirectAttempts; attempt++) {
        if (aborted()) return;
        _setPhase(
          SessionPhase.negotiatingTransport,
          attempt == 1
              ? '正在建立网络直连'
              : '直连通道重试中（第 $attempt/$maxDirectAttempts 次）...',
        );
        if (attempt > 1) {
          signaling.sendBridgeCandidates(
            sessionId: sid,
            candidatesJson: localCandidatesJson,
          );
        }
        final attemptStartMs = DateTime.now().millisecondsSinceEpoch;
        p2p = await _runTransportOp(
          transportAddr,
          _connectPeerOp(
            engineFactory,
            transportAddr,
            sessionToken,
            peerCandidates,
            2500,
          ),
        );
        if (aborted()) return;
        if (p2p.$1) break;
        final elapsedMs = DateTime.now().millisecondsSinceEpoch - attemptStartMs;
        if (elapsedMs < 200 || relayFallbackComp.isCompleted) break;
      }

      if (aborted()) return;

      Future<bool> switchToRelay() async {
        _setPhase(SessionPhase.connectingRelay, '正在切换云端高速中继');
        signaling.sendRelayFallback(sessionId: sid);

        var relayToken = bridge.relayToken;
        var relayPath = bridge.relayPath;
        if (relayToken.trim().isEmpty) {
          try {
            final rf = await relayFallbackComp.future.timeout(
              const Duration(seconds: 4),
            );
            if (rf.relayToken.trim().isNotEmpty) relayToken = rf.relayToken;
            if (rf.relayPath.trim().isNotEmpty) relayPath = rf.relayPath;
          } catch (_) {}
        }
        if (aborted() || relayToken.trim().isEmpty) return false;

        final relayOk = await _connectCloudRelay(
          sessionId: sid,
          relayPath: relayPath,
          relayToken: relayToken,
          sessionToken: sessionToken,
          aborted: aborted,
        );
        if (aborted() || !relayOk) return false;
        _relayAttempts++;
        return true;
      }

      var usedRelay = false;
      if (!p2p.$1) {
        if (!await switchToRelay()) {
          if (!aborted()) _fail('直连与云端中继均失败');
          return;
        }
        usedRelay = true;
      }

      _setPhase(SessionPhase.startingStream, '正在启动硬件低延迟解码');
      engine.configureTransportCrypto(
        transport: _transportHandle!,
        aesKey32: material.aesKey32,
        enabled: challenge.enableEncryption && _settings.enableEncryption,
      );
      engine.setTransportKcpEnabled(
        transport: _transportHandle!,
        enabled: bridge.kcpEnabled && !usedRelay,
      );

      OsmiumSettings authSettingsFor(bool relay) =>
          relay && _settings.desiredBitrateKbps > 16000
          ? _settings.copyWith(desiredBitrateKbps: 16000)
          : _settings;
      Future<(int, String)> authenticate({required bool relay}) =>
          _runTransportOp(
            transportAddr,
            _authenticateOp(
              engineFactory,
              transportAddr,
              material.authTag32,
              authSettingsFor(relay),
              relay ? 10000 : (relayFallbackComp.isCompleted ? 2500 : 5000),
            ),
          );

      var authOutcome = await authenticate(relay: usedRelay);
      if (aborted()) return;

      if (authOutcome.$1 == 0 && !usedRelay && _relayAttempts == 0) {
        if (await switchToRelay()) {
          usedRelay = true;
          if (!aborted()) authOutcome = await authenticate(relay: true);
        }
      }
      if (aborted()) return;
      _reliableAuxTransport = usedRelay || bridge.kcpEnabled;

      if (authOutcome.$1 <= 0) {
        _fail(authOutcome.$2.isNotEmpty ? authOutcome.$2 : '流媒体鉴权失败');
        return;
      }

      // Step 12: Create Android SurfaceProducer & start native streaming session
      final surface = await platformBridge.createSurface(
        width: _settings.desiredWidth,
        height: _settings.desiredHeight,
      );
      if (surface == null || surface.textureId < 0 || aborted()) {
        if (!aborted()) _fail('视频渲染画布初始化失败');
        return;
      }

      _textureId = surface.textureId;
      _surfaceHandleId = surface.surfaceHandleId;

      _sessionHandle = engine.startSession(
        transport: _transportHandle!,
        surfaceHandleId: _surfaceHandleId,
        settings: _settings,
      );
      if (_sessionHandle == null) {
        _fail('解码会话启动失败');
        return;
      }
      if (aborted()) return;

      // Step 13: Wire Aux / events / stats / native inputs. `session_confirm`
      // is sent from the first-frame callback (red line: the host must not
      // consider the session agreed before it has pushed real video).
      _setupRuntimePipelines();

      _setPhase(SessionPhase.streaming, '已连接');
    } catch (e) {
      if (!aborted()) {
        _fail('连接异常: $e');
      }
    } finally {
      bridgeRetry?.cancel();
      for (final s in subs) {
        await s.cancel();
      }
    }
  }

  Future<void> _retryAfterCaptcha(
    SessionConnectRequest req,
    String ltoken,
    AccountChallengeInfo challenge,
    int epoch,
  ) async {
    _setPhase(SessionPhase.requesting, _captchaRequiredText);
    final solved = await solveCaptcha!(challenge);
    if (_isStale(epoch)) return;
    if (!solved) {
      _fail('已取消安全人机验证');
      return;
    }
    await _connect(req, ltoken: ltoken, captchaRetried: true);
  }

  Future<bool> _connectCloudRelay({
    required String sessionId,
    required String relayPath,
    required String relayToken,
    required Uint8List sessionToken,
    required bool Function() aborted,
  }) async {
    try {
      final baseUri = Uri.parse(
        SignalingClient.normalizeServerUrl(signaling.serverUrl),
      );
      final normalizedPath = relayPath.startsWith('/')
          ? relayPath
          : '/$relayPath';
      final relayUri = baseUri.replace(
        path: normalizedPath,
        queryParameters: {
          'sessionId': sessionId,
          'role': 'client',
          'token': relayToken,
        },
      );

      final ws = await WebSocket.connect(relayUri.toString())
          .timeout(const Duration(seconds: 8));
      if (aborted() || _transportHandle == null) {
        await ws.close();
        return false;
      }
      final transport = _transportHandle!;
      final transportAddr = transport.address;
      _relayWs = ws;

      if (!engine.enableRelay(
        transport: transport,
        sessionToken16: sessionToken,
        label: 'wss-relay',
      )) {
        if (identical(_relayWs, ws)) _relayWs = null;
        await ws.close();
        return false;
      }

      final peerReady = Completer<bool>();
      ws.listen(
        (dynamic data) {
          if (data is String) {
            try {
              final map = jsonDecode(data) as Map<String, dynamic>;
              if (map['type'] == 'relay_peer_ready' && !peerReady.isCompleted) {
                peerReady.complete(true);
              }
            } catch (_) {}
          } else if (data is List<int>) {
            if (!peerReady.isCompleted && data.length >= 10) {
              peerReady.complete(true);
            }
            if (!aborted() && _transportHandle?.address == transportAddr) {
              final bytes = data is Uint8List ? data : Uint8List.fromList(data);
              engine.pushRelayBatch(transport, bytes);
            }
          }
        },
        onError: (_) {
          if (!peerReady.isCompleted) peerReady.complete(false);
        },
        onDone: () {
          if (!peerReady.isCompleted) peerReady.complete(false);
        },
        cancelOnError: true,
      );

      // Start relay outgoing pump immediately so authenticateClient packets flow
      _relayPumpTimer?.cancel();
      _relayPumpTimer = Timer.periodic(const Duration(milliseconds: 4), (_) {
        if (aborted() || !identical(_relayWs, ws)) return;
        _flushRelayOutgoing(ws, transport);
      });

      final ready = await peerReady.future.timeout(
        const Duration(seconds: 12),
        onTimeout: () => false,
      );
      if (!ready || aborted() || !identical(_relayWs, ws)) {
        if (identical(_relayWs, ws)) {
          _relayPumpTimer?.cancel();
          _relayPumpTimer = null;
          _relayWs = null;
        }
        await ws.close();
        return false;
      }
      return true;
    } catch (_) {
      return false;
    }
  }

  void _flushRelayOutgoing(WebSocket ws, Pointer<Void> transport) {
    if (ws.readyState != WebSocket.open) return;

    final batch = BytesBuilder(copy: false);
    var count = 0;
    while (count < 24) {
      final pkt = engine.pollRelayOutgoing(transport);
      if (pkt == null || pkt.isEmpty) break;
      final hdr = ByteData(2)..setUint16(0, pkt.length, Endian.little);
      batch.add(hdr.buffer.asUint8List());
      batch.add(pkt);
      count++;
      if (batch.length >= 24 * 1024) break;
    }
    if (batch.length > 0) {
      try {
        ws.add(batch.takeBytes());
      } catch (_) {}
    }
  }

  void _setupRuntimePipelines() {
    _auxChannel?.dispose();
    _auxChannel = AuxTransferChannel(
      sendRaw: (bytes) => engine.sendAuxData(_sessionHandle, bytes),
      reliableUnderlyingTransport: _reliableAuxTransport,
    );
    _auxChannel!.sendFeatureConfig(
      sharedClipboard: _settings.sharedClipboardEnabled,
      fileTransfer: _settings.fileTransferEnabled,
    );

    _auxClipSub = _auxChannel!.clipboardTextStream.listen((text) {
      if (_settings.sharedClipboardEnabled && text.isNotEmpty) {
        Clipboard.setData(ClipboardData(text: text));
        _emitToast('已同步远端剪贴板');
      }
    });

    _auxFilesSub = _auxChannel!.filesReceivedStream.listen((paths) {
      _emitToast('已接收 ${paths.length} 个文件');
    });

    _auxWallpaperSub = _auxChannel!.desktopWallpaperStream.listen((bytes) {
      final targetId = _request?.targetDeviceId;
      if (targetId != null && targetId.isNotEmpty && bytes.isNotEmpty) {
        onDesktopWallpaperReceived?.call(targetId, bytes);
      }
    });

    _auxProgressSub = _auxChannel!.progressStream.listen((p) {
      _lastTransferProgress = p;
      notifyListeners();
    });

    _nativeInputSub?.cancel();
    _nativeInputSub = platformBridge.events.listen(_handleNativeInput);

    _sessionEndSub?.cancel();
    _sessionEndSub = signaling.sessionEndedStream.listen((evt) {
      if (evt.$1 == _sessionId && _phase == SessionPhase.streaming) {
        _handleRemoteEnded(evt.$2);
      }
    });

    _sessionEstablishedSub?.cancel();
    _sessionEstablishedSub = signaling.sessionEstablishedStream.listen((sid) {
      if (sid == _sessionId) {
        _sessionEstablished = true;
        notifyListeners();
      }
    });

    // Poll native events every 16ms
    _eventPollTimer?.cancel();
    _eventPollTimer = Timer.periodic(const Duration(milliseconds: 16), (_) {
      final sess = _sessionHandle;
      if (sess == null) return;
      for (var i = 0; i < 32; i++) {
        final ev = engine.pollEvent(sess);
        if (ev == null) break;
        switch (ev.type) {
          case 1: // FirstFrameRendered
            if (!_hasReceivedFirstFrame) {
              _hasReceivedFirstFrame = true;
              _confirmSessionAfterFirstFrame();
              _requestTextFocus();
              notifyListeners();
            }
            break;
          case 2: // HostStatusJson
            try {
              final map =
                  jsonDecode(utf8.decode(ev.payload)) as Map<String, dynamic>;
              final next = HostSessionStatusDto.fromJson(map);
              final sizeChanged =
                  next.streamWidth != _hostStatus.streamWidth ||
                  next.streamHeight != _hostStatus.streamHeight;
              _hostStatus = next;
              _virtualSpeaker = next.virtualSpeakerActive;
              if (sizeChanged &&
                  next.streamWidth > 0 &&
                  next.streamHeight > 0) {
                platformBridge.resizeSurface(
                  width: next.streamWidth,
                  height: next.streamHeight,
                );
              }
              notifyListeners();
            } catch (_) {}
            break;
          case 3: // AuxData
            _auxChannel?.handleIncomingPacket(ev.payload);
            break;
          case 6: // HostTextFocus
            final next = HostTextFocus.fromBytes(ev.payload);
            if (next == null) break;
            _textFocusAnswered = true;
            if (next != _textFocus) {
              _textFocus = next;
              notifyListeners();
            }
            break;
          case 4: // SessionEnded
            final reason = utf8.decode(ev.payload, allowMalformed: true);
            _handleRemoteEnded(reason.isNotEmpty ? reason : '连接已断开');
            break;
          case 5: // RelayOutgoingPacket
            final ws = _relayWs;
            if (ws != null &&
                ws.readyState == WebSocket.open &&
                ev.payload.isNotEmpty) {
              final framed = Uint8List(2 + ev.payload.length);
              ByteData.sublistView(framed)
                  .setUint16(0, ev.payload.length, Endian.little);
              framed.setAll(2, ev.payload);
              try {
                ws.add(framed);
              } catch (_) {}
            }
            break;
        }
      }
    });

    // Poll stats every 300ms
    _statsTimer?.cancel();
    _statsTimer = Timer.periodic(const Duration(milliseconds: 300), (_) {
      if (_sessionHandle == null || _disposed) return;
      final next = engine.getStats(_sessionHandle);
      if (next.firstFrameRendered && !_hasReceivedFirstFrame) {
        _hasReceivedFirstFrame = true;
        _confirmSessionAfterFirstFrame();
        _requestTextFocus();
      }
      _stats = next;
      notifyListeners();
    });
  }

  void _handleNativeInput(NativeInputEvent event) {
    if (_sessionHandle == null) return;
    switch (event) {
      case NativeSurfaceChangedEvent(:final surfaceHandleId):
        _surfaceHandleId = surfaceHandleId;
        engine.updateSurface(_sessionHandle, surfaceHandleId);
        if (surfaceHandleId != 0) {
          engine.requestIdr(_sessionHandle);
        }
        break;
      case NativePointerDeltaEvent(:final dx, :final dy):
        if (!_viewOnly) {
          engine.sendMouseMove(_sessionHandle, absolute: false, x: dx, y: dy);
        }
        break;
      case NativePointerButtonEvent(:final button, :final isDown):
        if (!_viewOnly) {
          engine.sendMouseButton(
            _sessionHandle,
            button: button,
            isDown: isDown,
          );
        }
        break;
      case NativePointerWheelEvent(:final dx, :final dy):
        if (!_viewOnly) {
          engine.sendMouseWheel(_sessionHandle, dx: dx, dy: dy);
        }
        break;
      case NativeGamepadEvent():
        if (!_viewOnly) {
          engine.sendGamepad(
            _sessionHandle,
            controllerIndex: event.controllerIndex,
            connected: event.connected,
            buttonsMask: event.buttonsMask,
            leftTrigger: event.leftTrigger,
            rightTrigger: event.rightTrigger,
            leftThumbX: event.leftThumbX,
            leftThumbY: event.leftThumbY,
            rightThumbX: event.rightThumbX,
            rightThumbY: event.rightThumbY,
          );
        }
        break;
    }
  }

  /// Sent only after the first decoded frame, mirroring Windows
  /// `_clientStartedDelegate -> ConfirmClientSessionAsync`.
  /// Asks the host to report text focus, repeating until it answers: the
  /// request can be lost, and hosts that predate it never answer at all.
  void _requestTextFocus() {
    _textFocusTimer?.cancel();
    var tries = 1;
    engine.setTextFocusTracking(_sessionHandle, true);
    _textFocusTimer = Timer.periodic(const Duration(seconds: 2), (timer) {
      if (_disposed || _textFocusAnswered || tries >= 5) {
        timer.cancel();
        return;
      }
      tries++;
      engine.setTextFocusTracking(_sessionHandle, true);
    });
  }

  void _confirmSessionAfterFirstFrame() {
    if (_disposed || _sessionConfirmed) return;
    final sid = _sessionId;
    if (sid.isEmpty) return;
    _sessionConfirmed = true;
    signaling.sendSessionConfirm(sessionId: sid);

    // The signaling socket may still be reconnecting at that moment, so try
    // once more unless the server already reported mutual confirmation.
    Future.delayed(const Duration(seconds: 2), () {
      if (_disposed || _sessionEstablished || _sessionId != sid) return;
      if (_phase != SessionPhase.streaming) return;
      signaling.sendSessionConfirm(sessionId: sid);
    });
  }

  void _handleRemoteEnded(String reason) {
    // The peer already ended the session; never answer with another end.
    _signalingEndSent = true;
    _cleanupNative(notifySignaling: false);
    _phase = SessionPhase.ended;
    _statusText = reason;
    _errorMessage = reason;
    notifyListeners();
  }

  void _fail(String message) {
    // Once the host created a session handle for us it keeps the slot until it
    // sees session_end; without `failed:true` the retry hits HOST_BUSY.
    if (_bridgeReached &&
        !_signalingEndSent &&
        _sessionId.isNotEmpty &&
        _request != null) {
      _signalingEndSent = true;
      signaling.sendSessionEnd(
        sessionId: _sessionId,
        targetDeviceId: _request!.targetDeviceId,
        reason: message,
        failed: true,
      );
    }
    _cleanupNative(notifySignaling: false);
    _phase = SessionPhase.failed;
    _statusText = message;
    _errorMessage = message;
    notifyListeners();
  }

  // ---------------------------------------------------------------------------
  // Input & Control Actions (called from UI)
  // ---------------------------------------------------------------------------

  void sendAbsoluteMouse(double normX, double normY) {
    if (_viewOnly || _sessionHandle == null) return;
    engine.sendMouseMove(
      _sessionHandle,
      absolute: true,
      x: normX.clamp(0.0, 1.0),
      y: normY.clamp(0.0, 1.0),
    );
  }

  void sendRelativeMouse(double dx, double dy) {
    if (_viewOnly || _sessionHandle == null) return;
    engine.sendMouseMove(_sessionHandle, absolute: false, x: dx, y: dy);
  }

  void sendMouseButton(int button, bool isDown) {
    if (_viewOnly || _sessionHandle == null) return;
    engine.sendMouseButton(_sessionHandle, button: button, isDown: isDown);
  }

  void sendMouseClick(int button) {
    if (_viewOnly || _sessionHandle == null) return;
    engine.sendMouseButton(_sessionHandle, button: button, isDown: true);
    Future.delayed(const Duration(milliseconds: 24), () {
      engine.sendMouseButton(_sessionHandle, button: button, isDown: false);
    });
  }

  void sendMouseWheel({int dx = 0, int dy = 0}) {
    if (_viewOnly || _sessionHandle == null) return;
    engine.sendMouseWheel(_sessionHandle, dx: dx, dy: dy);
  }

  int _touchSeq = 0;

  /// Native Windows touch; only for hosts reporting
  /// [HostSessionStatusDto.touchInput].
  void sendTouch(List<TouchContact> contacts) {
    if (_viewOnly || _sessionHandle == null) return;
    _touchSeq = (_touchSeq + 1) & 0xFFFF;
    engine.sendTouch(_sessionHandle, seq: _touchSeq, contacts: contacts);
  }

  void sendKey(WinKeySpec key, bool isDown) {
    if (_viewOnly || _sessionHandle == null) return;
    engine.sendKey(
      _sessionHandle,
      vk: key.vk,
      scan: key.scan,
      isDown: isDown,
      isExtended: key.isExtended,
    );
  }

  void tapKey(WinKeySpec key) {
    sendKey(key, true);
    Future.delayed(const Duration(milliseconds: 25), () => sendKey(key, false));
  }

  void sendCombo(List<WinKeySpec> keys) {
    if (_viewOnly || _sessionHandle == null || keys.isEmpty) return;
    for (final k in keys) {
      sendKey(k, true);
    }
    Future.delayed(const Duration(milliseconds: 35), () {
      for (final k in keys.reversed) {
        sendKey(k, false);
      }
    });
  }

  void sendUnicodeText(String text) {
    if (_viewOnly || _sessionHandle == null || text.isEmpty) return;
    engine.sendUnicodeText(_sessionHandle, text);
  }

  void applySettings(OsmiumSettings next, {bool updateVideo = false}) {
    _settings = next;
    if (updateVideo && _sessionHandle != null) {
      engine.updateVideoParams(
        _sessionHandle,
        width: next.desiredWidth,
        height: next.desiredHeight,
        fps: next.desiredFps,
        bitrateKbps: next.desiredBitrateKbps,
        hdr: next.enableHdr,
        yuv444: next.enableYuv444,
      );
    }
    notifyListeners();
  }

  void switchDisplay(int displayIndex) {
    if (_sessionHandle == null) return;
    engine.switchDisplay(_sessionHandle, displayIndex);
    _emitToast('正在切换显示器 ${displayIndex + 1}');
  }

  void toggleMute() {
    _audioMuted = !_audioMuted;
    engine.setAudioMute(_sessionHandle, _audioMuted);
    notifyListeners();
  }

  void toggleVirtualSpeaker() {
    _virtualSpeaker = !_virtualSpeaker;
    engine.setVirtualAudio(
      _sessionHandle,
      virtualSpeaker: _virtualSpeaker,
      virtualMic: _micEnabled,
    );
    notifyListeners();
  }

  Future<void> toggleMicPassthrough() async {
    if (!_micEnabled) {
      final status = await Permission.microphone.request();
      if (!status.isGranted) {
        _emitToast('请授予麦克风权限');
        return;
      }
    }
    _micEnabled = !_micEnabled;
    engine.setVirtualAudio(
      _sessionHandle,
      virtualSpeaker: _virtualSpeaker,
      virtualMic: _micEnabled,
    );
    engine.setMicCaptureEnabled(_sessionHandle, _micEnabled);
    notifyListeners();
  }

  Future<void> togglePointerCapture() async {
    final next = !_pointerCaptureActive;
    final ok = await platformBridge.setPointerCapture(next);
    _pointerCaptureActive = ok;
    engine.setMouseMode(
      _sessionHandle,
      mouseMode: ok ? 1 : 0,
      hideHostCursor: false,
    );
    notifyListeners();
  }

  /// Stops (or resumes) sending input while keeping the picture live.
  void toggleViewOnly() {
    if (viewOnlyLocked) return;
    _viewOnly = !_viewOnly;
    notifyListeners();
  }

  Future<void> cycleOrientation() async {
    _orientationMode = (_orientationMode + 1) % 3;
    await platformBridge.setOrientationLock(_orientationMode);
    notifyListeners();
  }

  void sendSystemAction(int action) {
    engine.sendSystemAction(_sessionHandle, action);
  }

  void requestIdr() {
    engine.requestIdr(_sessionHandle);
  }

  Future<void> pushLocalClipboard() async {
    final data = await Clipboard.getData(Clipboard.kTextPlain);
    final text = data?.text ?? '';
    if (text.isEmpty) {
      _emitToast('剪贴板为空');
      return;
    }
    _auxChannel?.sendClipboardText(text);
    _emitToast('已发送到远端剪贴板');
  }

  Future<void> sendFile(File file) async {
    if (_auxChannel == null) return;
    await _auxChannel!.sendFile(file);
  }

  Future<void> disconnect({
    bool notifySignaling = true,
    bool resetState = true,
  }) async {
    _cancelling = true;
    await _cleanupNative(notifySignaling: notifySignaling);
    if (resetState && !_disposed) {
      _phase = SessionPhase.idle;
      _statusText = '';
      _errorMessage = null;
      notifyListeners();
    }
  }

  Future<void> _cleanupNative({required bool notifySignaling}) async {
    _eventPollTimer?.cancel();
    _eventPollTimer = null;
    _textFocusTimer?.cancel();
    _textFocusTimer = null;
    _textFocusAnswered = false;
    _textFocus = HostTextFocus.none;
    _statsTimer?.cancel();
    _statsTimer = null;
    _relayPumpTimer?.cancel();
    _relayPumpTimer = null;

    await _nativeInputSub?.cancel();
    _nativeInputSub = null;
    await _sessionEndSub?.cancel();
    _sessionEndSub = null;
    await _sessionEstablishedSub?.cancel();
    _sessionEstablishedSub = null;
    await _auxClipSub?.cancel();
    _auxClipSub = null;
    await _auxFilesSub?.cancel();
    _auxFilesSub = null;
    await _auxWallpaperSub?.cancel();
    _auxWallpaperSub = null;
    await _auxProgressSub?.cancel();
    _auxProgressSub = null;

    _auxChannel?.dispose();
    _auxChannel = null;

    if (notifySignaling &&
        !_signalingEndSent &&
        _sessionId.isNotEmpty &&
        _request != null) {
      _signalingEndSent = true;
      signaling.sendSessionEnd(
        sessionId: _sessionId,
        targetDeviceId: _request!.targetDeviceId,
        reason: '用户主动断开',
      );
    }

    if (_pointerCaptureActive) {
      _pointerCaptureActive = false;
      await platformBridge.setPointerCapture(false);
    }

    if (_sessionHandle != null) {
      engine.stopSession(_sessionHandle);
      _sessionHandle = null;
    }

    try {
      await _relayWs?.close();
    } catch (_) {}
    _relayWs = null;

    final transport = _transportHandle;
    _transportHandle = null;
    if (transport != null) {
      // An Isolate.run FFI call may still hold this transport. Defer the
      // destroy until it returns; _runTransportOp performs it from its finally.
      if (_nativeOpsInFlight.contains(transport.address)) {
        _deferredTransportDestroy[transport.address] = transport;
      } else {
        engine.destroyTransport(transport);
      }
    }

    if (_keyPair != null) {
      engine.destroyKeyPair(_keyPair!.handle);
      _keyPair = null;
    }

    if (_textureId != null) {
      _textureId = null;
      _surfaceHandleId = 0;
      await platformBridge.destroySurface();
    }

    await platformBridge.setOrientationLock(0);
    await platformBridge.setImmersiveMode(false);
    try {
      await WakelockPlus.disable();
    } catch (_) {}
  }

  @override
  void dispose() {
    _disposed = true;
    disconnect(notifySignaling: true, resetState: false);
    super.dispose();
  }
}

class _TimeoutSentinel {
  const _TimeoutSentinel();
}
