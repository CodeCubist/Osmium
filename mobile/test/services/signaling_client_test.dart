import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:osmium_mobile/services/signaling/signaling_client.dart';

/// End-to-end wiring test: a real [SignalingClient] against a loopback
/// WebSocket server. Message shapes are the ones the Windows
/// `SignalingClient.Protocol.cs` sends/parses.
class _LoopbackSignalingServer {
  _LoopbackSignalingServer._(this._server);

  final HttpServer _server;
  final List<Map<String, dynamic>> received = [];
  final _messages = StreamController<Map<String, dynamic>>.broadcast();
  final _sockets = <WebSocket>[];

  static Future<_LoopbackSignalingServer> start() async {
    final http = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    final server = _LoopbackSignalingServer._(http);
    http.listen((request) async {
      final ws = await WebSocketTransformer.upgrade(request);
      server._sockets.add(ws);
      ws.listen((data) {
        if (data is! String) return;
        final map = jsonDecode(data) as Map<String, dynamic>;
        server.received.add(map);
        server._messages.add(map);
      }, onDone: () => server._sockets.remove(ws));
    });
    return server;
  }

  int get port => _server.port;
  String get url => 'ws://127.0.0.1:$port';

  Future<Map<String, dynamic>> nextMessageWhere(
    bool Function(Map<String, dynamic>) test, {
    Duration timeout = const Duration(seconds: 5),
  }) {
    return _messages.stream.firstWhere(test).timeout(timeout);
  }

  void send(Map<String, dynamic> payload) {
    _sockets.last.add(jsonEncode(payload));
  }

  Future<void> close() async {
    for (final ws in List<WebSocket>.from(_sockets)) {
      await ws.close();
    }
    await _server.close(force: true);
    await _messages.close();
  }
}

void main() {
  group('SignalingClient payloads', () {
    late _LoopbackSignalingServer server;
    late SignalingClient client;

    setUp(() async {
      server = await _LoopbackSignalingServer.start();
      client = SignalingClient();
    });

    tearDown(() async {
      client.dispose();
      await server.close();
    });

    Future<void> connectAndRegister() async {
      final register = server.nextMessageWhere((m) => m['type'] == 'register');
      client.start(
        serverUrl: server.url,
        deviceId: '123-456-789',
        deviceName: 'Test Phone',
        ltoken: 'ltoken-abc',
      );
      final payload = await register;
      expect(payload, {
        'type': 'register',
        'deviceId': '123456789',
        'deviceName': 'Test Phone',
        'allowRemote': false,
        'osVersion': Platform.isAndroid ? 'Android' : Platform.operatingSystem,
        'ltoken': 'ltoken-abc',
      });
      server.send({
        'type': 'registered',
        'stunHost': 'stun.example.test',
        'stunPort': 3479,
        'cooldownRemainingMs': 0,
      });
      await client.stateStream
          .firstWhere((s) => s == SignalingConnectionState.connected)
          .timeout(const Duration(seconds: 5));
      expect(client.advertisedStunHost, 'stun.example.test');
      expect(client.advertisedStunPort, 3479);
    }

    test('register + connect_request match the Windows client', () async {
      await connectAndRegister();

      final request = server.nextMessageWhere(
        (m) => m['type'] == 'connect_request',
      );
      client.sendConnectRequest(
        sessionId: 'AABB',
        targetDeviceId: '987654321',
        clientPublicKeyBase64: 'Y2xpZW50LXB1Yg==',
        enableEncryption: true,
        ltoken: 'ltoken-xyz',
        sameAccountConnect: true,
        viewOnly: false,
      );
      expect(await request, {
        'type': 'connect_request',
        'sessionId': 'AABB',
        'targetDeviceId': '987654321',
        'clientName': 'Test Phone',
        'clientPublicKey': 'Y2xpZW50LXB1Yg==',
        'enableEncryption': true,
        'ltoken': 'ltoken-xyz',
        'sameAccountConnect': true,
        'viewOnly': false,
        'candidates': <dynamic>[],
      });
    });

    test('handshake follow-ups match the Windows client', () async {
      await connectAndRegister();

      final auth = server.nextMessageWhere((m) => m['type'] == 'connect_auth');
      client.sendConnectAuth(
        sessionId: 'AABB',
        targetDeviceId: '987 654 321',
        authTagBase64: 'dGFn',
      );
      expect(await auth, {
        'type': 'connect_auth',
        'sessionId': 'AABB',
        'targetDeviceId': '987654321',
        'authTag': 'dGFn',
      });

      final version = server.nextMessageWhere(
        (m) => m['type'] == 'session_version_report',
      );
      client.sendVersionReport(sessionId: 'AABB');
      expect(await version, {
        'type': 'session_version_report',
        'sessionId': 'AABB',
        'version': '1.0.0',
      });

      final candidates = server.nextMessageWhere(
        (m) => m['type'] == 'bridge_candidates',
      );
      client.sendBridgeCandidates(
        sessionId: 'AABB',
        candidatesJson: '[{"ip":"10.0.0.2","port":5555,"type":"lan4"}]',
      );
      expect(await candidates, {
        'type': 'bridge_candidates',
        'sessionId': 'AABB',
        'candidates': [
          {'ip': '10.0.0.2', 'port': 5555, 'type': 'lan4'},
        ],
      });

      final relay = server.nextMessageWhere(
        (m) => m['type'] == 'relay_fallback',
      );
      client.sendRelayFallback(sessionId: 'AABB');
      expect(await relay, {'type': 'relay_fallback', 'sessionId': 'AABB'});

      final confirm = server.nextMessageWhere(
        (m) => m['type'] == 'session_confirm',
      );
      client.sendSessionConfirm(sessionId: 'AABB');
      expect(await confirm, {'type': 'session_confirm', 'sessionId': 'AABB'});

      final end = server.nextMessageWhere((m) => m['type'] == 'session_end');
      client.sendSessionEnd(
        sessionId: 'AABB',
        targetDeviceId: '987654321',
        reason: '流媒体鉴权失败',
        failed: true,
      );
      expect(await end, {
        'type': 'session_end',
        'sessionId': 'AABB',
        'targetDeviceId': '987654321',
        'reason': '流媒体鉴权失败',
        'role': 'client',
        'failed': true,
      });
    });

    test('parses connect_challenge like the Windows host sends it', () async {
      await connectAndRegister();

      final event = client.challengeStream.first.timeout(
        const Duration(seconds: 5),
      );
      server.send({
        'type': 'connect_challenge',
        'sessionId': 'AABB',
        'hostDeviceId': '987654321',
        'hostName': 'DESKTOP-OSMIUM',
        'hostPublicKey': 'aG9zdC1wdWI=',
        'hostChallengeNonce': 'bm9uY2U=',
        'enableEncryption': true,
        'sameAccountAuthSecret': 'secret-123',
        'stunHost': 'stun.host.test',
        'stunPort': 3480,
      });
      final challenge = await event;
      expect(challenge.sessionId, 'AABB');
      expect(challenge.hostDeviceId, '987654321');
      expect(challenge.hostName, 'DESKTOP-OSMIUM');
      expect(challenge.hostPublicKeyBase64, 'aG9zdC1wdWI=');
      expect(challenge.hostChallengeNonceBase64, 'bm9uY2U=');
      expect(challenge.enableEncryption, isTrue);
      expect(challenge.sameAccountAuthSecret, 'secret-123');
      expect(challenge.stunHost, 'stun.host.test');
      expect(challenge.stunPort, 3480);

      final fallback = client.challengeStream.first.timeout(
        const Duration(seconds: 5),
      );
      server.send({
        'type': 'connect_challenge',
        'sessionId': 'CCDD',
        'hostDeviceId': '987654321',
        'hostPublicKey': 'aG9zdC1wdWI=',
        'hostChallengeNonce': 'bm9uY2U=',
      });
      final second = await fallback;
      expect(second.sessionId, 'CCDD');
      expect(
        second.enableEncryption,
        isTrue,
        reason: 'missing flag defaults to encrypted',
      );
      expect(second.stunHost, 'stun.example.test');
      expect(second.stunPort, 3479);
    });

    test('parses version / bridge / relay results', () async {
      await connectAndRegister();

      final versionFuture = client.versionResultStream.first.timeout(
        const Duration(seconds: 5),
      );
      server.send({
        'type': 'version_check_result',
        'sessionId': 'AABB',
        'compatible': false,
        'code': 'VERSION_MISMATCH',
        'clientVersion': '1.0.0',
        'hostVersion': '0.9.0',
        'reason': 'Host is too old',
      });
      final version = await versionFuture;
      expect(version.compatible, isFalse);
      expect(version.code, 'VERSION_MISMATCH');
      expect(version.hostVersion, '0.9.0');
      expect(version.reason, 'Host is too old');

      final bridgeFuture = client.bridgeReadyStream.first.timeout(
        const Duration(seconds: 5),
      );
      server.send({
        'type': 'bridge_ready',
        'sessionId': 'AABB',
        'peerDeviceId': '987654321',
        'candidates': [
          {'ip': '192.168.1.5', 'port': 40000, 'type': 'lan4'},
          {'ip': '127.0.0.1', 'port': 40000, 'type': 'loopback'},
        ],
        'relayToken': 'relay-token',
      });
      final bridge = await bridgeFuture;
      expect(bridge.peerDeviceId, '987654321');
      expect(jsonDecode(bridge.candidatesJson), [
        {'ip': '192.168.1.5', 'port': 40000, 'type': 'lan4'},
        {'ip': '127.0.0.1', 'port': 40000, 'type': 'loopback'},
      ]);
      expect(bridge.relayToken, 'relay-token');
      expect(bridge.relayPath, '/relay');
      expect(bridge.stunHost, 'stun.example.test');

      final relayFuture = client.relayFallbackStream.first.timeout(
        const Duration(seconds: 5),
      );
      server.send({
        'type': 'relay_fallback',
        'sessionId': 'AABB',
        'relayToken': 'issued-token',
        'relayPath': '/relay2',
      });
      final relay = await relayFuture;
      expect(relay.relayToken, 'issued-token');
      expect(relay.relayPath, '/relay2');
    });

    test('parses reject, session error, established and end', () async {
      await connectAndRegister();

      final rejectFuture = client.rejectStream.first.timeout(
        const Duration(seconds: 5),
      );
      server.send({
        'type': 'connect_reject',
        'sessionId': 'AABB',
        'code': 'DEVICE_OFFLINE',
        'reason': '目标设备离线',
        'remainingMs': 1500,
        'challenge': {'challenge_id': 'c1'},
      });
      final reject = await rejectFuture;
      expect(reject.sessionId, 'AABB');
      expect(reject.code, 'DEVICE_OFFLINE');
      expect(reject.reason, '目标设备离线');
      expect(reject.cooldownMs, 1500);
      expect(reject.challenge?.challengeId, 'c1');

      final errorFuture = client.rejectStream.first.timeout(
        const Duration(seconds: 5),
      );
      server.send({
        'type': 'error',
        'sessionId': 'AABB',
        'code': 'ENCRYPTION_MISMATCH',
        'message': 'Encryption settings differ',
      });
      final error = await errorFuture;
      expect(error.code, 'ENCRYPTION_MISMATCH');
      expect(error.reason, 'Encryption settings differ');

      final establishedFuture = client.sessionEstablishedStream.first.timeout(
        const Duration(seconds: 5),
      );
      server.send({'type': 'session_established', 'sessionId': 'AABB'});
      expect(await establishedFuture, 'AABB');

      final endedFuture = client.sessionEndedStream.first.timeout(
        const Duration(seconds: 5),
      );
      server.send({
        'type': 'session_end',
        'sessionId': 'AABB',
        'reason': 'Host closed',
      });
      final ended = await endedFuture;
      expect(ended.$1, 'AABB');
      expect(ended.$2, 'Host closed');
    });
  });

  test('three failed tries in a row report the server unreachable', () async {
    // A port that was just free: every dial is refused at once.
    final probe = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
    final port = probe.port;
    await probe.close();

    final client = SignalingClient();
    addTearDown(client.dispose);
    var failures = 0;
    client.stateStream.listen((s) {
      if (s == SignalingConnectionState.disconnected) failures++;
    });
    client.start(
      serverUrl: 'ws://127.0.0.1:$port',
      deviceId: '123456789',
      deviceName: 'Test Phone',
    );
    await client.unreachableStream.first.timeout(const Duration(seconds: 20));
    expect(failures, SignalingClient.offlineFailureThreshold);
  });

  group('NormalizeServerUrl', () {
    test('matches Windows normalization', () {
      expect(
        SignalingClient.normalizeServerUrl(null),
        'wss://osmium.fufu.skin',
      );
      expect(
        SignalingClient.normalizeServerUrl('  '),
        'wss://osmium.fufu.skin',
      );
      expect(
        SignalingClient.normalizeServerUrl('https://a.test/'),
        'wss://a.test',
      );
      expect(
        SignalingClient.normalizeServerUrl('http://a.test///'),
        'ws://a.test',
      );
      expect(SignalingClient.normalizeServerUrl('a.test'), 'wss://a.test');
      expect(SignalingClient.normalizeServerUrl('ws://a.test'), 'ws://a.test');
    });
  });
}
