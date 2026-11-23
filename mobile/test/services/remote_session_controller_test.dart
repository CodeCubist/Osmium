import 'dart:async';
import 'dart:convert';
import 'dart:ffi';
import 'dart:isolate';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:osmium_mobile/core/models/account_models.dart';
import 'package:osmium_mobile/core/models/device_identity.dart';
import 'package:osmium_mobile/core/models/host_status.dart';
import 'package:osmium_mobile/core/models/osmium_settings.dart';
import 'package:osmium_mobile/services/engine/osmium_engine.dart';
import 'package:osmium_mobile/services/engine/platform_bridge.dart';
import 'package:osmium_mobile/services/session/remote_session_controller.dart';
import 'package:osmium_mobile/services/signaling/signaling_client.dart';

const _hostDeviceId = '987654321';

_FakeEngine? _activeEngine;

/// Top-level so it is sendable exactly like the production `OsmiumEngine.new`.
OsmiumEngine _activeEngineFactory() => _activeEngine!;

/// Rejects ops that `Isolate.run` would reject: sending copies the closure and
/// its whole captured context, so unsendable state (completers, streams)
/// throws here just as it does on the device. The op then runs inline.
Future<T> _isolateCheckedOp<T>(FutureOr<T> Function() op) async {
  final port = ReceivePort();
  try {
    port.sendPort.send(op);
  } finally {
    port.close();
  }
  return await op();
}

class _FakeSignaling implements SignalingClient {
  final challengeCtrl = StreamController<ConnectChallengeEvent>.broadcast();
  final authVerifiedCtrl = StreamController<String>.broadcast();
  final versionCtrl = StreamController<VersionCheckResultEvent>.broadcast();
  final bridgeCtrl = StreamController<BridgeReadyEvent>.broadcast();
  final relayFallbackCtrl = StreamController<RelayFallbackEvent>.broadcast();
  final rejectCtrl = StreamController<ConnectRejectEvent>.broadcast();
  final sessionEndedCtrl = StreamController<(String, String)>.broadcast();
  final establishedCtrl = StreamController<String>.broadcast();

  final List<Map<String, dynamic>> connectRequests = [];
  final List<String> authRequests = [];
  final List<String> versionReports = [];
  final List<String> candidateReports = [];
  final List<String> relayFallbacks = [];
  final List<String> confirms = [];
  final List<Map<String, dynamic>> sessionEnds = [];

  @override
  SignalingConnectionState state = SignalingConnectionState.connected;

  @override
  String serverUrl = 'ws://127.0.0.1:1';

  @override
  String advertisedStunHost = 'stun.local';

  @override
  int advertisedStunPort = 3478;

  String lastSessionId = '';

  @override
  Stream<ConnectChallengeEvent> get challengeStream => challengeCtrl.stream;
  @override
  Stream<String> get authVerifiedStream => authVerifiedCtrl.stream;
  @override
  Stream<VersionCheckResultEvent> get versionResultStream => versionCtrl.stream;
  @override
  Stream<BridgeReadyEvent> get bridgeReadyStream => bridgeCtrl.stream;
  @override
  Stream<RelayFallbackEvent> get relayFallbackStream =>
      relayFallbackCtrl.stream;
  @override
  Stream<ConnectRejectEvent> get rejectStream => rejectCtrl.stream;
  @override
  Stream<(String, String)> get sessionEndedStream => sessionEndedCtrl.stream;
  @override
  Stream<String> get sessionEstablishedStream => establishedCtrl.stream;

  @override
  void sendConnectRequest({
    required String sessionId,
    required String targetDeviceId,
    required String clientPublicKeyBase64,
    required bool enableEncryption,
    required String ltoken,
    required bool sameAccountConnect,
    required bool viewOnly,
  }) {
    lastSessionId = sessionId;
    connectRequests.add({
      'sessionId': sessionId,
      'targetDeviceId': targetDeviceId,
      'clientPublicKey': clientPublicKeyBase64,
      'enableEncryption': enableEncryption,
      'ltoken': ltoken,
      'sameAccountConnect': sameAccountConnect,
      'viewOnly': viewOnly,
    });
  }

  @override
  void sendConnectAuth({
    required String sessionId,
    required String targetDeviceId,
    required String authTagBase64,
  }) {
    authRequests.add(authTagBase64);
  }

  @override
  void sendVersionReport({required String sessionId}) {
    versionReports.add(sessionId);
  }

  @override
  void sendBridgeCandidates({
    required String sessionId,
    required String candidatesJson,
  }) {
    candidateReports.add(candidatesJson);
  }

  @override
  void sendRelayFallback({required String sessionId}) {
    relayFallbacks.add(sessionId);
  }

  @override
  void sendSessionConfirm({required String sessionId}) {
    confirms.add(sessionId);
  }

  @override
  void sendSessionEnd({
    required String sessionId,
    required String targetDeviceId,
    required String reason,
    bool failed = false,
  }) {
    sessionEnds.add({
      'sessionId': sessionId,
      'targetDeviceId': targetDeviceId,
      'reason': reason,
      'failed': failed,
    });
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

class _FakeEngine implements OsmiumEngine {
  bool p2pSuccess = true;
  bool firstFrameRendered = false;
  final List<(int, String)> authResults = [(1, '')];
  int authCalls = 0;
  int deriveCalls = 0;
  final List<int> destroyedTransports = [];

  static final Pointer<Void> _keyHandle = Pointer<Void>.fromAddress(0x1000);
  static final Pointer<Void> _transport = Pointer<Void>.fromAddress(0x2000);
  static final Pointer<Void> _session = Pointer<Void>.fromAddress(0x3000);

  @override
  bool get isAvailable => true;

  @override
  NativeKeyPair? generateKeyPair() => NativeKeyPair(
    handle: _keyHandle,
    publicKeySpkiDer: Uint8List.fromList(List<int>.filled(65, 7)),
  );

  @override
  DerivedSessionMaterial? deriveSessionMaterial({
    required Pointer<Void> keyHandle,
    required Uint8List peerPubSpkiDer,
    required Uint8List sessionToken16,
    required Uint8List challengeNonce,
    required String accessCode,
  }) {
    deriveCalls++;
    return DerivedSessionMaterial(
      aesKey32: Uint8List.fromList(List<int>.generate(32, (i) => i)),
      authTag32: Uint8List.fromList(List<int>.generate(32, (i) => 255 - i)),
    );
  }

  @override
  void destroyKeyPair(Pointer<Void>? handle) {}

  @override
  TransportCreationResult? createTransport({
    required String stunHost,
    required int stunPort,
  }) => TransportCreationResult(
    handle: _transport,
    candidatesJson: '[{"ip":"10.0.0.9","port":40000,"type":"lan4"}]',
  );

  @override
  P2PConnectResult connectPeerP2P({
    required Pointer<Void> transport,
    required Uint8List sessionToken16,
    required String peerCandidatesJson,
    int timeoutMs = 4500,
  }) => P2PConnectResult(
    success: p2pSuccess,
    route: p2pSuccess ? 1 : 0,
    endpoint: p2pSuccess ? '10.0.0.2:5000' : '',
  );

  @override
  bool enableRelay({
    required Pointer<Void> transport,
    required Uint8List sessionToken16,
    String label = 'wss-relay',
  }) => true;

  @override
  bool pushRelayBatch(Pointer<Void> transport, Uint8List bytes) => true;

  @override
  Uint8List? pollRelayOutgoing(Pointer<Void> transport) => null;

  @override
  void configureTransportCrypto({
    required Pointer<Void> transport,
    required Uint8List aesKey32,
    required bool enabled,
  }) {}

  @override
  (int, String) authenticateClient({
    required Pointer<Void> transport,
    required Uint8List authTag32,
    required OsmiumSettings settings,
    int timeoutMs = 6000,
  }) {
    final index = authCalls < authResults.length
        ? authCalls
        : authResults.length - 1;
    authCalls++;
    return authResults[index];
  }

  @override
  void destroyTransport(Pointer<Void>? transport) {
    if (transport != null) destroyedTransports.add(transport.address);
  }

  @override
  Pointer<Void>? startSession({
    required Pointer<Void> transport,
    required int surfaceHandleId,
    required OsmiumSettings settings,
  }) => _session;

  @override
  void stopSession(Pointer<Void>? session) {}

  @override
  void updateSurface(Pointer<Void>? session, int surfaceHandleId) {}

  @override
  SessionStatsSnapshot getStats(Pointer<Void>? session) => SessionStatsSnapshot(
    firstFrameRendered: firstFrameRendered,
    sessionActive: true,
  );

  @override
  NativeEngineEvent? pollEvent(Pointer<Void>? session) => null;

  @override
  bool sendAuxData(Pointer<Void>? session, Uint8List data) => true;

  final List<bool> textFocusRequests = [];

  @override
  void setTextFocusTracking(Pointer<Void>? session, bool enabled) =>
      textFocusRequests.add(enabled);

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

class _FakePlatformBridge implements PlatformBridge {
  bool surfaceCreated = false;
  bool surfaceDestroyed = false;

  @override
  Stream<NativeInputEvent> get events => const Stream<NativeInputEvent>.empty();

  @override
  Future<NativeSurfaceInfo?> createSurface({
    int width = 1920,
    int height = 1080,
  }) async {
    surfaceCreated = true;
    return const NativeSurfaceInfo(textureId: 42, surfaceHandleId: 7);
  }

  @override
  Future<void> resizeSurface({required int width, required int height}) async {}

  @override
  Future<void> destroySurface() async {
    surfaceDestroyed = true;
  }

  @override
  Future<bool> setPointerCapture(bool enabled) async => enabled;

  @override
  Future<void> setImmersiveMode(bool enabled) async {}

  @override
  Future<void> setOrientationLock(int mode) async {}

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

class _Harness {
  _Harness({ConnectCaptchaSolver? solveCaptcha}) {
    _activeEngine = engine;
    controller = RemoteSessionController(
      engine: engine,
      platformBridge: platform,
      signaling: signaling,
      initialSettings: const OsmiumSettings(),
      solveCaptcha: solveCaptcha,
      isolateEngineFactory: _activeEngineFactory,
      transportOpRunner: <T>(int addr, FutureOr<T> Function() op) =>
          _isolateCheckedOp(op),
      engineOpRunner: _isolateCheckedOp,
    );
  }

  final signaling = _FakeSignaling();
  final engine = _FakeEngine();
  final platform = _FakePlatformBridge();
  late final RemoteSessionController controller;

  String get sid => signaling.lastSessionId;

  Future<void> waitFor(bool Function() condition, {String? reason}) async {
    for (var i = 0; i < 400; i++) {
      if (condition()) return;
      await Future<void>.delayed(const Duration(milliseconds: 5));
    }
    fail(reason ?? 'condition not met in time');
  }

  Future<void> start({
    String accessCode = 'ABC123',
    bool sameAccount = false,
    String relayToken = 'relay-token',
  }) async {
    unawaited(
      controller.connect(
        SessionConnectRequest(
          targetDeviceId: _hostDeviceId,
          targetDeviceName: 'DESKTOP-OSMIUM',
          accessCode: accessCode,
          sameAccount: sameAccount,
        ),
        ltoken: 'ltoken',
      ),
    );
    await waitFor(
      () => signaling.connectRequests.isNotEmpty,
      reason: 'connect_request not sent',
    );
  }

  ConnectRejectEvent captchaReject() => ConnectRejectEvent(
    sessionId: sid,
    code: '100',
    reason: 'captcha required',
    challenge: const AccountChallengeInfo(
      challengeId: 'connect-cid',
      challengeSecret: 'connect-secret',
      captchaUrl: '/api/captcha/connect-cid',
      pollUrl: '/api/challenge/connect-cid',
      expiresIn: 300,
    ),
  );

  ConnectChallengeEvent challenge({
    bool enableEncryption = true,
    String? secret,
  }) => ConnectChallengeEvent(
    sessionId: sid,
    hostDeviceId: _hostDeviceId,
    hostName: 'DESKTOP-OSMIUM',
    hostPublicKeyBase64: base64Encode(
      Uint8List.fromList(List<int>.filled(65, 3)),
    ),
    hostChallengeNonceBase64: base64Encode(
      Uint8List.fromList(List<int>.filled(32, 5)),
    ),
    enableEncryption: enableEncryption,
    sameAccountAuthSecret: secret,
    stunHost: 'stun.test',
    stunPort: 3478,
  );

  BridgeReadyEvent bridge({String relayToken = 'relay-token'}) =>
      BridgeReadyEvent(
        sessionId: sid,
        peerDeviceId: _hostDeviceId,
        candidatesJson: '[{"ip":"10.0.0.2","port":5000,"type":"lan4"}]',
        stunHost: 'stun.test',
        stunPort: 3478,
        relayToken: relayToken,
        relayPath: '/relay',
      );

  /// Runs steps 2-8 up to and including bridge_ready.
  Future<void> handshake({
    bool enableEncryption = true,
    String relayToken = 'relay-token',
  }) async {
    signaling.challengeCtrl.add(challenge(enableEncryption: enableEncryption));
    await waitFor(
      () => signaling.authRequests.isNotEmpty,
      reason: 'connect_auth not sent',
    );
    signaling.authVerifiedCtrl.add(sid);
    await waitFor(
      () => signaling.versionReports.isNotEmpty,
      reason: 'version report not sent',
    );
    signaling.versionCtrl.add(
      VersionCheckResultEvent(
        sessionId: sid,
        compatible: true,
        code: 'OK',
        clientVersion: '1.0.0',
        hostVersion: '1.0.0',
        reason: '',
      ),
    );
    await waitFor(
      () => signaling.candidateReports.isNotEmpty,
      reason: 'bridge candidates not sent',
    );
    signaling.bridgeCtrl.add(bridge(relayToken: relayToken));
  }

  Future<void> tearDown() async {
    await controller.disconnect(notifySignaling: false);
    controller.dispose();
  }
}

void main() {
  test('direct P2P session confirms only after the first frame', () async {
    final h = _Harness();
    await h.start();
    await h.handshake();
    await h.waitFor(
      () => h.controller.phase == SessionPhase.streaming,
      reason: 'controller never reached streaming',
    );

    expect(h.platform.surfaceCreated, isTrue);
    expect(
      h.signaling.confirms,
      isEmpty,
      reason: 'session_confirm must wait for the first decoded frame',
    );
    expect(h.controller.hasReceivedFirstFrame, isFalse);
    expect(h.engine.textFocusRequests, isEmpty);

    h.engine.firstFrameRendered = true;
    await h.waitFor(
      () => h.signaling.confirms.isNotEmpty,
      reason: 'session_confirm not sent after first frame',
    );
    expect(h.controller.hasReceivedFirstFrame, isTrue);
    expect(h.signaling.confirms, [h.sid]);
    // The host is asked to report text focus once the picture is up.
    expect(h.engine.textFocusRequests, [true]);

    // Only one confirm per first-frame transition.
    await Future<void>.delayed(const Duration(milliseconds: 200));
    expect(h.signaling.confirms, [h.sid]);

    await h.tearDown();
  });

  test(
    'connect_reject surfaces the server reason and sends no session_end',
    () async {
      final h = _Harness();
      await h.start();
      h.signaling.rejectCtrl.add(
        ConnectRejectEvent(
          sessionId: h.sid,
          code: 'DEVICE_OFFLINE',
          reason: '目标设备离线',
        ),
      );
      await h.waitFor(() => h.controller.phase == SessionPhase.failed);

      expect(h.controller.errorMessage, '目标设备离线');
      expect(h.signaling.sessionEnds, isEmpty);
      expect(h.engine.authCalls, 0);

      await h.tearDown();
    },
  );

  group('connect_reject formatting mirrors Windows FormatReject', () {
    ConnectRejectEvent rej(
      String code, {
      String reason = '',
      int cooldownMs = 0,
    }) => ConnectRejectEvent(
      sessionId: 'S',
      code: code,
      reason: reason,
      cooldownMs: cooldownMs,
    );

    test('numeric server codes use the Windows codes and zh-CN text', () {
      expect(
        formatConnectReject(rej('2010', reason: 'auth required')),
        '仅已登录且状态正常的账户可发起远程控制，被控无需登录',
      );
      expect(formatConnectReject(rej('2005')), '该账户当前处于安全锁定状态，请先解锁');
      expect(formatConnectReject(rej('2011')), '被控端账户已将您拉黑，无法发起远程控制');
      expect(formatConnectReject(rej('100')), '当日远控次数已达安全验证阈值，请先完成人机验证');
      expect(formatConnectReject(rej('4006')), '目标设备当前不在线');
      expect(formatConnectReject(rej('4007')), '目标设备已关闭允许远程控制');
      expect(formatConnectReject(rej('4008')), '不能连接本机自身');
      expect(formatConnectReject(rej('4001')), '请求过于频繁，请稍后再试');
    });

    test('cooldown rounds up and never shows less than 1s', () {
      expect(
        formatConnectReject(rej('4005', cooldownMs: 2500)),
        '操作过于频繁，请等待 3 秒后再试',
      );
      expect(formatConnectReject(rej('4005')), '操作过于频繁，请等待 1 秒后再试');
    });

    test(
      'host string codes are localized instead of echoing English reasons',
      () {
        expect(
          formatConnectReject(
            rej('INVALID_ACCESS_CODE', reason: 'Invalid remote access code'),
          ),
          '远控代码不正确，对方拒绝接入',
        );
        expect(
          formatConnectReject(rej('encryption_mismatch')),
          '加密传输设置不一致，需被控端与主控端同时开启或关闭',
        );
        expect(formatConnectReject(rej('REMOTE_DISABLED')), '目标设备已关闭允许远程控制');
        expect(
          formatConnectReject(
            rej(
              'HOST_BUSY',
              reason: 'Another remote control session is already active',
            ),
          ),
          '目标设备正在被其他设备远程控制',
        );
      },
    );

    test('unknown codes fall back to the reason, then the code', () {
      expect(formatConnectReject(rej('SOMETHING', reason: '自定义原因')), '自定义原因');
      expect(formatConnectReject(rej('SOMETHING')), '连接被拒绝 - SOMETHING');
      expect(formatConnectReject(rej('')), '目标主机拒绝连接');
    });
  });

  test('signed-out controller fails before sending connect_request', () async {
    final h = _Harness();
    await h.controller.connect(
      const SessionConnectRequest(
        targetDeviceId: _hostDeviceId,
        targetDeviceName: 'DESKTOP-OSMIUM',
        accessCode: 'ABC123',
      ),
      ltoken: '',
    );

    expect(h.controller.phase, SessionPhase.failed);
    expect(h.controller.errorMessage, '仅已登录且状态正常的账户可发起远程控制，被控无需登录');
    expect(h.signaling.connectRequests, isEmpty);

    await h.tearDown();
  });

  test('server-side 2010 rejection shows the sign-in requirement', () async {
    final h = _Harness();
    await h.start();
    h.signaling.rejectCtrl.add(
      ConnectRejectEvent(
        sessionId: h.sid,
        code: '2010',
        reason: 'Account authentication required',
      ),
    );
    await h.waitFor(() => h.controller.phase == SessionPhase.failed);

    expect(h.controller.errorMessage, '仅已登录且状态正常的账户可发起远程控制，被控无需登录');
    await h.tearDown();
  });

  test(
    'captcha reject solves once, resends connect_request and completes',
    () async {
      final solved = <AccountChallengeInfo>[];
      final h = _Harness(
        solveCaptcha: (challenge) async {
          solved.add(challenge);
          return true;
        },
      );
      await h.start();
      final firstSid = h.sid;
      h.signaling.rejectCtrl.add(h.captchaReject());

      await h.waitFor(
        () => h.signaling.connectRequests.length == 2,
        reason: 'connect_request was not resent after the captcha',
      );
      expect(solved.single.challengeId, 'connect-cid');
      expect(
        h.sid,
        isNot(firstSid),
        reason: 'the retry uses a fresh session id',
      );
      expect(h.controller.phase, isNot(SessionPhase.failed));

      await h.handshake();
      await h.waitFor(
        () => h.controller.phase == SessionPhase.streaming,
        reason: 'retry never reached streaming',
      );
      expect(solved, hasLength(1));

      await h.tearDown();
    },
  );

  test('cancelled captcha fails without resending', () async {
    final h = _Harness(solveCaptcha: (_) async => false);
    await h.start();
    h.signaling.rejectCtrl.add(h.captchaReject());
    await h.waitFor(() => h.controller.phase == SessionPhase.failed);

    expect(h.controller.errorMessage, '已取消安全人机验证');
    expect(h.signaling.connectRequests, hasLength(1));
    await h.tearDown();
  });

  test(
    'a second captcha reject after the retry fails instead of looping',
    () async {
      var solves = 0;
      final h = _Harness(
        solveCaptcha: (_) async {
          solves++;
          return true;
        },
      );
      await h.start();
      h.signaling.rejectCtrl.add(h.captchaReject());
      await h.waitFor(() => h.signaling.connectRequests.length == 2);
      h.signaling.rejectCtrl.add(h.captchaReject());
      await h.waitFor(() => h.controller.phase == SessionPhase.failed);

      expect(solves, 1);
      expect(h.signaling.connectRequests, hasLength(2));
      expect(h.controller.errorMessage, '当日远控次数已达安全验证阈值，请先完成人机验证');
      expect(h.controller.pendingCaptcha?.challengeId, 'connect-cid');
      await h.tearDown();
    },
  );

  test('captcha reject without a solver surfaces the challenge', () async {
    final h = _Harness();
    await h.start();
    h.signaling.rejectCtrl.add(h.captchaReject());
    await h.waitFor(() => h.controller.phase == SessionPhase.failed);

    expect(h.controller.errorMessage, '当日远控次数已达安全验证阈值，请先完成人机验证');
    expect(h.controller.pendingCaptcha?.challengeId, 'connect-cid');
    expect(h.signaling.connectRequests, hasLength(1));
    await h.tearDown();
  });

  test('encryption mismatch fails before ECDH or any transport work', () async {
    final h = _Harness();
    await h.start();
    h.signaling.challengeCtrl.add(h.challenge(enableEncryption: false));
    await h.waitFor(() => h.controller.phase == SessionPhase.failed);

    expect(h.controller.errorMessage, contains('加密设置不一致'));
    expect(h.engine.deriveCalls, 0);
    expect(h.signaling.authRequests, isEmpty);

    await h.tearDown();
  });

  test(
    'failed punch without a reachable relay reports the combined failure',
    () async {
      final h = _Harness();
      h.engine.p2pSuccess = false;
      await h.start();
      await h.handshake();
      await h.waitFor(() => h.controller.phase == SessionPhase.failed);

      expect(h.signaling.relayFallbacks, [h.sid]);
      expect(h.controller.errorMessage, isNotNull);
      expect(h.engine.authCalls, 0);
      // bridge_ready was reached, so the host slot must be released.
      expect(h.signaling.sessionEnds.single['failed'], isTrue);

      await h.tearDown();
    },
  );

  test(
    'direct auth timeout retries over the cloud relay like Windows',
    () async {
      final h = _Harness();
      h.engine.authResults
        ..clear()
        ..add((0, 'Timed out waiting for host authentication response'));
      await h.start();
      await h.handshake();
      await h.waitFor(() => h.controller.phase == SessionPhase.failed);

      // The relay switch is attempted even though the punch "succeeded".
      expect(h.signaling.relayFallbacks, [h.sid]);
      expect(h.engine.authCalls, 1);
      expect(h.controller.errorMessage, contains('Timed out'));

      await h.tearDown();
    },
  );

  test(
    'host rejection is reported verbatim and releases the host session',
    () async {
      final h = _Harness();
      h.engine.authResults
        ..clear()
        ..add((-1, '访问码错误'));
      await h.start();
      await h.handshake();
      await h.waitFor(() => h.controller.phase == SessionPhase.failed);

      expect(h.controller.errorMessage, '访问码错误');
      expect(
        h.signaling.relayFallbacks,
        isEmpty,
        reason: 'an explicit rejection must not retry over the relay',
      );
      expect(h.signaling.sessionEnds.single['failed'], isTrue);

      await h.tearDown();
    },
  );

  test('cancel during authentication leaves the controller idle', () async {
    final h = _Harness();
    await h.start();
    h.signaling.challengeCtrl.add(h.challenge());
    await h.waitFor(() => h.signaling.authRequests.isNotEmpty);

    await h.controller.disconnect();
    expect(h.controller.phase, SessionPhase.idle);

    // A late auth_verified for the cancelled run must not revive the session.
    h.signaling.authVerifiedCtrl.add(h.sid);
    await Future<void>.delayed(const Duration(milliseconds: 100));
    expect(h.controller.phase, SessionPhase.idle);
    expect(h.controller.errorMessage, isNull);
    expect(h.engine.authCalls, 0);

    h.controller.dispose();
  });

  test('same-account secret replaces the typed access code', () async {
    final h = _Harness();
    await h.start(accessCode: '', sameAccount: true);
    h.signaling.challengeCtrl.add(h.challenge(secret: 'SERVER-SECRET'));
    await h.waitFor(() => h.engine.deriveCalls == 1);
    expect(h.signaling.authRequests, hasLength(1));

    await h.controller.disconnect();
    h.controller.dispose();
  });

  test('access code normalization matches Windows (trim + uppercase only)', () {
    expect(DeviceIdentity.normalizeAccessCode('  ab-12  '), 'AB-12');
    expect(
      DeviceIdentity.normalizeAccessCode('a b'),
      'A B',
      reason: 'inner whitespace is part of the access code',
    );
  });

  test('AccountChallengeInfo parses the snake_case challenge payload', () {
    final info = AccountChallengeInfo.fromJson({
      'challenge_id': 'ch-1',
      'challenge_secret': 'sec',
      'captcha_url': 'https://x/c',
      'poll_url': 'https://x/p',
      'expires_in': 120,
    });
    expect(info.challengeId, 'ch-1');
    expect(info.challengeSecret, 'sec');
    expect(info.expiresIn, 120);
  });
}
