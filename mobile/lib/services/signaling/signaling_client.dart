import 'dart:async';
import 'dart:convert';
import 'dart:io';

import '../../core/constants/osmium_constants.dart';
import '../../core/models/account_models.dart';
import '../../core/models/device_identity.dart';

enum SignalingConnectionState { disconnected, connecting, connected }

class ConnectChallengeEvent {
  const ConnectChallengeEvent({
    required this.sessionId,
    required this.hostDeviceId,
    required this.hostName,
    required this.hostPublicKeyBase64,
    required this.hostChallengeNonceBase64,
    required this.enableEncryption,
    this.sameAccountAuthSecret,
    required this.stunHost,
    required this.stunPort,
  });

  final String sessionId;
  final String hostDeviceId;
  final String hostName;
  final String hostPublicKeyBase64;
  final String hostChallengeNonceBase64;
  final bool enableEncryption;
  final String? sameAccountAuthSecret;
  final String stunHost;
  final int stunPort;
}

class VersionCheckResultEvent {
  const VersionCheckResultEvent({
    required this.sessionId,
    required this.compatible,
    required this.code,
    required this.clientVersion,
    required this.hostVersion,
    required this.reason,
  });

  final String sessionId;
  final bool compatible;
  final String code;
  final String clientVersion;
  final String hostVersion;
  final String reason;
}

class BridgeReadyEvent {
  const BridgeReadyEvent({
    required this.sessionId,
    required this.peerDeviceId,
    required this.candidatesJson,
    required this.stunHost,
    required this.stunPort,
    required this.relayToken,
    required this.relayPath,
    this.kcpEnabled = false,
  });

  final String sessionId;
  final String peerDeviceId;
  final String candidatesJson;
  final String stunHost;
  final int stunPort;
  final String relayToken;
  final String relayPath;
  final bool kcpEnabled;
}

class RelayFallbackEvent {
  const RelayFallbackEvent({
    required this.sessionId,
    required this.relayToken,
    required this.relayPath,
  });

  final String sessionId;
  final String relayToken;
  final String relayPath;
}

class ConnectRejectEvent {
  const ConnectRejectEvent({
    required this.sessionId,
    required this.code,
    required this.reason,
    this.cooldownMs = 0,
    this.challenge,
  });

  final String sessionId;
  final String code;
  final String reason;
  final int cooldownMs;
  final AccountChallengeInfo? challenge;
}

class QuickLaunchStartResponseEvent {
  const QuickLaunchStartResponseEvent({
    required this.requestId,
    required this.hostDeviceId,
    required this.status,
    required this.ok,
    required this.code,
    required this.reason,
    this.pid = 0,
  });

  final String requestId;
  final String hostDeviceId;
  final String status;
  final bool ok;
  final int code;
  final String reason;
  final int pid;
}

class SignalingClient {
  /// Consecutive failures before the server counts as unreachable; Windows
  /// `SignalingClient.OfflineFailureThreshold`.
  static const int offlineFailureThreshold = 3;

  WebSocket? _ws;
  Timer? _heartbeatTimer;
  Timer? _reconnectTimer;
  Timer? _registerTimer;
  bool _running = false;
  bool _registered = false;
  int _reconnectAttempt = 0;
  int _failures = 0;

  String _serverUrl = OsmiumConstants.defaultSignalingUrl;
  String _deviceId = '';
  String _deviceName = 'Osmium Mobile';
  String _ltoken = '';
  String _registeredLtoken = '';

  String advertisedStunHost = 'osmium.fufu.skin';
  int advertisedStunPort = 3478;
  SignalingConnectionState _state = SignalingConnectionState.disconnected;

  SignalingConnectionState get state => _state;
  String get serverUrl => _serverUrl;

  final _stateCtrl = StreamController<SignalingConnectionState>.broadcast();
  final _devicesCtrl = StreamController<List<AccountDeviceDto>>.broadcast();
  final _challengeCtrl = StreamController<ConnectChallengeEvent>.broadcast();
  final _authVerifiedCtrl = StreamController<String>.broadcast();
  final _versionResultCtrl =
      StreamController<VersionCheckResultEvent>.broadcast();
  final _bridgeReadyCtrl = StreamController<BridgeReadyEvent>.broadcast();
  final _relayFallbackCtrl = StreamController<RelayFallbackEvent>.broadcast();
  final _rejectCtrl = StreamController<ConnectRejectEvent>.broadcast();
  final _sessionEndedCtrl = StreamController<(String, String)>.broadcast();
  final _sessionEstablishedCtrl = StreamController<String>.broadcast();
  final _cooldownCtrl = StreamController<int>.broadcast();
  final _unreachableCtrl = StreamController<void>.broadcast();
  final _quickLaunchSyncCtrl = StreamController<void>.broadcast();
  final _quickLaunchStartResCtrl =
      StreamController<QuickLaunchStartResponseEvent>.broadcast();

  Stream<SignalingConnectionState> get stateStream => _stateCtrl.stream;
  Stream<List<AccountDeviceDto>> get accountDevicesStream =>
      _devicesCtrl.stream;
  Stream<ConnectChallengeEvent> get challengeStream => _challengeCtrl.stream;
  Stream<String> get authVerifiedStream => _authVerifiedCtrl.stream;
  Stream<VersionCheckResultEvent> get versionResultStream =>
      _versionResultCtrl.stream;
  Stream<BridgeReadyEvent> get bridgeReadyStream => _bridgeReadyCtrl.stream;
  Stream<RelayFallbackEvent> get relayFallbackStream =>
      _relayFallbackCtrl.stream;
  Stream<ConnectRejectEvent> get rejectStream => _rejectCtrl.stream;
  Stream<(String, String)> get sessionEndedStream => _sessionEndedCtrl.stream;
  Stream<String> get sessionEstablishedStream => _sessionEstablishedCtrl.stream;
  Stream<int> get cooldownStream => _cooldownCtrl.stream;
  Stream<void> get quickLaunchSyncStream => _quickLaunchSyncCtrl.stream;
  Stream<QuickLaunchStartResponseEvent> get quickLaunchStartResponseStream =>
      _quickLaunchStartResCtrl.stream;

  /// Fires once per failure streak, when [offlineFailureThreshold]
  /// consecutive attempts have failed without a `registered` reply.
  Stream<void> get unreachableStream => _unreachableCtrl.stream;

  /// Mirrors Windows `SignalingClient.NormalizeServerUrl`: accepts http(s) or
  /// bare hosts and yields a ws(s) origin without a trailing slash.
  static String normalizeServerUrl(String? raw) {
    if (raw == null || raw.trim().isEmpty) {
      return OsmiumConstants.defaultSignalingUrl;
    }
    var trimmed = raw.trim();
    final lower = trimmed.toLowerCase();
    if (lower.startsWith('https://')) {
      trimmed = 'wss://${trimmed.substring(8)}';
    } else if (lower.startsWith('http://')) {
      trimmed = 'ws://${trimmed.substring(7)}';
    } else if (!lower.startsWith('wss://') && !lower.startsWith('ws://')) {
      trimmed = 'wss://$trimmed';
    }
    while (trimmed.endsWith('/')) {
      trimmed = trimmed.substring(0, trimmed.length - 1);
    }
    return trimmed;
  }

  void start({
    String serverUrl = OsmiumConstants.defaultSignalingUrl,
    required String deviceId,
    required String deviceName,
    String? ltoken,
  }) {
    _serverUrl = normalizeServerUrl(serverUrl);
    _deviceId = DeviceIdentity.normalizeDeviceId(deviceId);
    _deviceName = deviceName;
    _ltoken = ltoken ?? '';
    _running = true;
    _reconnectAttempt = 0;
    _failures = 0;
    _connect();
  }

  /// Drops the backoff and failure streak and dials again right away.
  void retryNow() {
    if (!_running) return;
    _reconnectAttempt = 0;
    _failures = 0;
    if (_state != SignalingConnectionState.disconnected) return;
    _connect();
  }

  void stop() {
    _running = false;
    _registered = false;
    _heartbeatTimer?.cancel();
    _reconnectTimer?.cancel();
    _registerTimer?.cancel();
    _ws?.close();
    _ws = null;
    _setState(SignalingConnectionState.disconnected);
  }

  void updateAuthToken(String? ltoken) {
    _ltoken = ltoken ?? '';
    if (_registered) {
      sendJson({'type': 'auth_update', 'ltoken': _ltoken});
    }
  }

  void updateDeviceName(String deviceName) {
    _deviceName = deviceName;
    if (_registered) {
      sendJson({
        'type': 'update_status',
        'allowRemote': false,
        'deviceName': _deviceName,
      });
    }
  }

  void _setState(SignalingConnectionState next) {
    if (_state != next) {
      _state = next;
      _stateCtrl.add(next);
    }
  }

  Future<void> _connect() async {
    if (!_running) return;
    _heartbeatTimer?.cancel();
    _reconnectTimer?.cancel();
    _registered = false;
    _setState(SignalingConnectionState.connecting);

    try {
      final ws = await WebSocket.connect(_serverUrl)
          .timeout(const Duration(seconds: 5));
      if (!_running) {
        await ws.close();
        return;
      }
      _ws = ws;
      _reconnectAttempt = 0;

      // Register as mobile client (allowRemote=false in v1). Field set matches
      // Windows `SignalingClient.Protocol.cs` register payload.
      _registeredLtoken = _ltoken;
      sendJson({
        'type': 'register',
        'deviceId': _deviceId,
        'deviceName': _deviceName,
        'allowRemote': false,
        'clientVersion': OsmiumConstants.protocolVersion,
        'osVersion': Platform.isAndroid ? 'Android' : Platform.operatingSystem,
        'ltoken': _ltoken,
      });

      // A socket that never registers counts as a failure, as on Windows.
      _registerTimer = Timer(const Duration(seconds: 5), () {
        if (!_registered && identical(_ws, ws)) ws.close();
      });

      _heartbeatTimer = Timer.periodic(const Duration(seconds: 15), (_) {
        if (_ws != null && _ws!.readyState == WebSocket.open) {
          sendJson({'type': 'ping'});
        }
      });

      ws.listen(
        _onMessage,
        onError: (_) => _scheduleReconnect(),
        onDone: _scheduleReconnect,
        cancelOnError: true,
      );
    } catch (_) {
      _scheduleReconnect();
    }
  }

  void _scheduleReconnect() {
    _heartbeatTimer?.cancel();
    _registerTimer?.cancel();
    _registered = false;
    _ws = null;
    if (!_running) {
      _setState(SignalingConnectionState.disconnected);
      return;
    }
    _setState(SignalingConnectionState.disconnected);
    _failures++;
    if (_failures == offlineFailureThreshold) _unreachableCtrl.add(null);
    _reconnectAttempt++;
    final delayMs = (1500 * _reconnectAttempt).clamp(1500, 12000);
    _reconnectTimer?.cancel();
    _reconnectTimer = Timer(Duration(milliseconds: delayMs), _connect);
  }

  bool sendJson(Map<String, dynamic> payload) {
    final ws = _ws;
    if (ws == null || ws.readyState != WebSocket.open) return false;
    try {
      ws.add(jsonEncode(payload));
      return true;
    } catch (_) {
      return false;
    }
  }

  void _onMessage(dynamic raw) {
    if (raw is! String) return;
    Map<String, dynamic> root;
    try {
      root = jsonDecode(raw) as Map<String, dynamic>;
    } catch (_) {
      return;
    }

    final type = (root['type'] ?? '').toString();
    switch (type) {
      case 'registered':
        _registered = true;
        _failures = 0;
        _registerTimer?.cancel();
        if (root['stunHost'] is String &&
            (root['stunHost'] as String).isNotEmpty) {
          advertisedStunHost = root['stunHost'] as String;
        }
        if (root['stunPort'] is num) {
          advertisedStunPort = (root['stunPort'] as num).toInt();
        }
        _cooldownCtrl.add((root['cooldownRemainingMs'] as num?)?.toInt() ?? 0);
        // The ltoken rotated while this register was in flight.
        if (_ltoken != _registeredLtoken) {
          sendJson({'type': 'auth_update', 'ltoken': _ltoken});
        }
        _setState(SignalingConnectionState.connected);
        break;

      case 'account_devices_sync':
        final rawDevices = root['devices'];
        if (rawDevices is List) {
          final list = rawDevices
              .whereType<Map<String, dynamic>>()
              .map(AccountDeviceDto.fromJson)
              .toList();
          _devicesCtrl.add(list);
        }
        break;

      case 'connect_challenge':
        _challengeCtrl.add(
          ConnectChallengeEvent(
            sessionId: (root['sessionId'] ?? '').toString(),
            hostDeviceId: (root['hostDeviceId'] ?? '').toString(),
            hostName: (root['hostName'] ?? 'Osmium Host').toString(),
            hostPublicKeyBase64: (root['hostPublicKey'] ?? '').toString(),
            hostChallengeNonceBase64: (root['hostChallengeNonce'] ?? '')
                .toString(),
            enableEncryption: root['enableEncryption'] != false,
            sameAccountAuthSecret: root['sameAccountAuthSecret'] as String?,
            stunHost: (root['stunHost'] as String?)?.isNotEmpty == true
                ? root['stunHost'] as String
                : advertisedStunHost,
            stunPort: (root['stunPort'] as num?)?.toInt() ?? advertisedStunPort,
          ),
        );
        break;

      case 'connect_auth_verified':
        _authVerifiedCtrl.add((root['sessionId'] ?? '').toString());
        break;

      case 'version_check_result':
        _versionResultCtrl.add(
          VersionCheckResultEvent(
            sessionId: (root['sessionId'] ?? '').toString(),
            compatible: root['compatible'] == true,
            code: (root['code'] ?? '').toString(),
            clientVersion: (root['clientVersion'] ?? '').toString(),
            hostVersion: (root['hostVersion'] ?? '').toString(),
            reason: (root['reason'] ?? '').toString(),
          ),
        );
        break;

      case 'bridge_ready':
        final cands = root['candidates'];
        _bridgeReadyCtrl.add(
          BridgeReadyEvent(
            sessionId: (root['sessionId'] ?? '').toString(),
            peerDeviceId: (root['peerDeviceId'] ?? '').toString(),
            candidatesJson: cands != null ? jsonEncode(cands) : '[]',
            stunHost: (root['stunHost'] as String?)?.isNotEmpty == true
                ? root['stunHost'] as String
                : advertisedStunHost,
            stunPort: (root['stunPort'] as num?)?.toInt() ?? advertisedStunPort,
            relayToken: (root['relayToken'] ?? '').toString(),
            relayPath: (root['relayPath'] as String?)?.isNotEmpty == true
                ? root['relayPath'] as String
                : '/relay',
            kcpEnabled: root['kcpEnabled'] == true,
          ),
        );
        break;

      case 'relay_fallback':
        _relayFallbackCtrl.add(
          RelayFallbackEvent(
            sessionId: (root['sessionId'] ?? '').toString(),
            relayToken: (root['relayToken'] ?? '').toString(),
            relayPath: (root['relayPath'] as String?)?.isNotEmpty == true
                ? root['relayPath'] as String
                : '/relay',
          ),
        );
        break;

      case 'connect_reject':
        AccountChallengeInfo? challenge;
        if (root['challenge'] is Map<String, dynamic>) {
          challenge = AccountChallengeInfo.fromJson(
            root['challenge'] as Map<String, dynamic>,
          );
        }
        _rejectCtrl.add(
          ConnectRejectEvent(
            sessionId: (root['sessionId'] ?? '').toString(),
            code: (root['code'] ?? '').toString(),
            reason: (root['reason'] ?? '').toString(),
            cooldownMs:
                (root['remainingMs'] as num?)?.toInt() ??
                (root['cooldownMs'] as num?)?.toInt() ??
                0,
            challenge: challenge,
          ),
        );
        break;

      case 'connect_cooldown':
        final ms =
            (root['remainingMs'] as num?)?.toInt() ??
            (root['cooldownMs'] as num?)?.toInt() ??
            0;
        _cooldownCtrl.add(ms);
        break;

      case 'error':
        // Windows maps session-scoped `error` messages onto the connect
        // rejection path, which unblocks the in-flight handshake immediately.
        final sid = (root['sessionId'] ?? '').toString();
        if (sid.isNotEmpty) {
          _rejectCtrl.add(
            ConnectRejectEvent(
              sessionId: sid,
              code: (root['code'] ?? 'INTERNAL_ERROR').toString(),
              reason: (root['message'] ?? root['reason'] ?? '').toString(),
            ),
          );
        }
        break;

      case 'session_end':
        _sessionEndedCtrl.add((
          (root['sessionId'] ?? '').toString(),
          (root['reason'] ?? '会话已结束').toString(),
        ));
        break;

      case 'session_established':
        _sessionEstablishedCtrl.add((root['sessionId'] ?? '').toString());
        break;

      case 'quick_launch_apps_sync':
        _quickLaunchSyncCtrl.add(null);
        break;

      case 'quick_launch_start_res':
        _quickLaunchStartResCtrl.add(
          QuickLaunchStartResponseEvent(
            requestId: (root['requestId'] ?? '').toString(),
            hostDeviceId: (root['hostDeviceId'] ?? '').toString(),
            status: (root['status'] ?? '').toString(),
            ok: root['ok'] == true,
            code: (root['code'] as num?)?.toInt() ?? 0,
            reason: (root['reason'] ?? '').toString(),
            pid: (root['pid'] as num?)?.toInt() ?? 0,
          ),
        );
        break;
    }
  }

  void sendConnectRequest({
    required String sessionId,
    required String targetDeviceId,
    required String clientPublicKeyBase64,
    required bool enableEncryption,
    required String ltoken,
    required bool sameAccountConnect,
    required bool viewOnly,
  }) {
    sendJson({
      'type': 'connect_request',
      'sessionId': sessionId,
      'targetDeviceId': DeviceIdentity.normalizeDeviceId(targetDeviceId),
      'clientName': _deviceName,
      'clientPublicKey': clientPublicKeyBase64,
      'enableEncryption': enableEncryption,
      'ltoken': ltoken.isNotEmpty ? ltoken : _ltoken,
      'sameAccountConnect': sameAccountConnect,
      'viewOnly': viewOnly,
      'candidates': const <dynamic>[],
    });
  }

  void sendConnectAuth({
    required String sessionId,
    required String targetDeviceId,
    required String authTagBase64,
  }) {
    sendJson({
      'type': 'connect_auth',
      'sessionId': sessionId,
      'targetDeviceId': DeviceIdentity.normalizeDeviceId(targetDeviceId),
      'authTag': authTagBase64,
    });
  }

  void sendVersionReport({required String sessionId}) {
    sendJson({
      'type': 'session_version_report',
      'sessionId': sessionId,
      'version': OsmiumConstants.protocolVersion,
    });
  }

  void sendBridgeCandidates({
    required String sessionId,
    required String candidatesJson,
  }) {
    dynamic parsed = const <dynamic>[];
    try {
      parsed = jsonDecode(candidatesJson);
    } catch (_) {}
    sendJson({
      'type': 'bridge_candidates',
      'sessionId': sessionId,
      'candidates': parsed,
      'transportCaps': const ['kcp-v1'],
    });
  }

  void sendRelayFallback({required String sessionId}) {
    sendJson({'type': 'relay_fallback', 'sessionId': sessionId});
  }

  void sendSessionConfirm({required String sessionId}) {
    if (sessionId.isEmpty) return;
    sendJson({'type': 'session_confirm', 'sessionId': sessionId});
  }

  void sendSessionEnd({
    required String sessionId,
    required String targetDeviceId,
    required String reason,
    bool failed = false,
  }) {
    sendJson({
      'type': 'session_end',
      'sessionId': sessionId,
      'targetDeviceId': DeviceIdentity.normalizeDeviceId(targetDeviceId),
      'reason': reason,
      'role': 'client',
      'failed': failed,
    });
  }

  void sendForceDisconnect(String targetHostDeviceId) {
    sendJson({
      'type': 'account_force_disconnect',
      'targetHostDeviceId': DeviceIdentity.normalizeDeviceId(
        targetHostDeviceId,
      ),
    });
  }

  void sendBlacklistUser(String targetHostDeviceId) {
    sendJson({
      'type': 'account_blacklist_user',
      'targetHostDeviceId': DeviceIdentity.normalizeDeviceId(
        targetHostDeviceId,
      ),
    });
  }

  bool sendQuickLaunchStart({
    required String requestId,
    required String targetDeviceId,
    required String appId,
    required String exePath,
    required String appName,
  }) {
    return sendJson({
      'type': 'quick_launch_start_req',
      'requestId': requestId,
      'targetDeviceId': DeviceIdentity.normalizeDeviceId(targetDeviceId),
      'appId': appId,
      'exePath': exePath,
      'appName': appName,
    });
  }

  bool sendQuickLaunchCancel({
    required String requestId,
    required String targetDeviceId,
  }) {
    return sendJson({
      'type': 'quick_launch_cancel',
      'requestId': requestId,
      'targetDeviceId': DeviceIdentity.normalizeDeviceId(targetDeviceId),
    });
  }

  void dispose() {
    stop();
    _stateCtrl.close();
    _devicesCtrl.close();
    _challengeCtrl.close();
    _authVerifiedCtrl.close();
    _versionResultCtrl.close();
    _bridgeReadyCtrl.close();
    _relayFallbackCtrl.close();
    _rejectCtrl.close();
    _sessionEndedCtrl.close();
    _sessionEstablishedCtrl.close();
    _cooldownCtrl.close();
    _unreachableCtrl.close();
    _quickLaunchSyncCtrl.close();
    _quickLaunchStartResCtrl.close();
  }
}
