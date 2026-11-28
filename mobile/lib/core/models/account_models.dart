import 'dart:convert';
import 'dart:typed_data';

import '../constants/osmium_constants.dart';

/// Decodes a server-returned `data:image/(jpeg|png|webp);base64,...` URI into
/// raw image bytes. Returns `null` when absent or malformed.
Uint8List? decodeAvatarDataUri(String? dataUri) {
  if (dataUri == null) return null;
  final trimmed = dataUri.trim();
  if (trimmed.isEmpty) return null;
  final lower = trimmed.toLowerCase();
  if (!lower.startsWith('data:image/')) return null;
  final comma = trimmed.indexOf(',');
  if (comma <= 0 || comma >= trimmed.length - 1) return null;
  final header = lower.substring(0, comma);
  if (!header.contains(';base64')) return null;
  try {
    final bytes = base64Decode(trimmed.substring(comma + 1).trim());
    return bytes.isEmpty ? null : bytes;
  } catch (_) {
    return null;
  }
}

class AccountChallengeInfo {
  const AccountChallengeInfo({
    required this.challengeId,
    required this.challengeSecret,
    required this.captchaUrl,
    required this.pollUrl,
    required this.expiresIn,
  });

  final String challengeId;
  final String challengeSecret;
  final String captchaUrl;
  final String pollUrl;
  final int expiresIn;

  factory AccountChallengeInfo.fromJson(Map<String, dynamic> json) {
    return AccountChallengeInfo(
      challengeId: (json['challenge_id'] ?? '').toString(),
      challengeSecret: (json['challenge_secret'] ?? '').toString(),
      captchaUrl: (json['captcha_url'] ?? '').toString(),
      pollUrl: (json['poll_url'] ?? '').toString(),
      expiresIn: (json['expires_in'] as num?)?.toInt() ?? 300,
    );
  }
}

class AccountSessionData {
  const AccountSessionData({
    required this.userId,
    required this.email,
    this.recoveryEmail = '',
    this.hasRecoveryEmail = false,
    required this.username,
    this.avatarData,
    required this.stoken,
    required this.stokenExpiresAtUnix,
    required this.ltoken,
    required this.ltokenExpiresAtUnix,
    this.accountStatus = 'active',
    this.locked = false,
    this.hasEmail = true,
    this.hasPassword = true,
    this.twoFaEnabled = false,
    this.assignedDeviceId,
  });

  final String userId;
  final String email;
  final String recoveryEmail;
  final bool hasRecoveryEmail;
  final String username;
  final String? avatarData;
  final String stoken;
  final int stokenExpiresAtUnix;
  final String ltoken;
  final int ltokenExpiresAtUnix;
  final String accountStatus;
  final bool locked;
  final bool hasEmail;
  final bool hasPassword;
  final bool twoFaEnabled;
  final String? assignedDeviceId;

  bool get isProfileComplete => hasEmail && hasPassword;

  bool get isStokenValid {
    if (stoken.isEmpty) return false;
    if (stokenExpiresAtUnix <= 0) return true;
    final now = DateTime.now().millisecondsSinceEpoch ~/ 1000;
    return now < stokenExpiresAtUnix - 30;
  }

  bool get isLtokenValid {
    if (ltoken.isEmpty) return false;
    if (ltokenExpiresAtUnix <= 0) return true;
    final now = DateTime.now().millisecondsSinceEpoch ~/ 1000;
    return now < ltokenExpiresAtUnix - 30;
  }

  bool get isLtokenExpiringSoon {
    if (ltoken.isEmpty) return true;
    if (ltokenExpiresAtUnix <= 0) return false;
    final now = DateTime.now().millisecondsSinceEpoch ~/ 1000;
    return now >= ltokenExpiresAtUnix - 180;
  }

  AccountSessionData copyWith({
    String? userId,
    String? email,
    String? recoveryEmail,
    bool? hasRecoveryEmail,
    String? username,
    String? avatarData,
    bool clearAvatar = false,
    String? stoken,
    int? stokenExpiresAtUnix,
    String? ltoken,
    int? ltokenExpiresAtUnix,
    String? accountStatus,
    bool? locked,
    bool? hasEmail,
    bool? hasPassword,
    bool? twoFaEnabled,
    String? assignedDeviceId,
  }) {
    return AccountSessionData(
      userId: userId ?? this.userId,
      email: email ?? this.email,
      recoveryEmail: recoveryEmail ?? this.recoveryEmail,
      hasRecoveryEmail: hasRecoveryEmail ?? this.hasRecoveryEmail,
      username: username ?? this.username,
      avatarData: clearAvatar ? null : (avatarData ?? this.avatarData),
      stoken: stoken ?? this.stoken,
      stokenExpiresAtUnix: stokenExpiresAtUnix ?? this.stokenExpiresAtUnix,
      ltoken: ltoken ?? this.ltoken,
      ltokenExpiresAtUnix: ltokenExpiresAtUnix ?? this.ltokenExpiresAtUnix,
      accountStatus: accountStatus ?? this.accountStatus,
      locked: locked ?? this.locked,
      hasEmail: hasEmail ?? this.hasEmail,
      hasPassword: hasPassword ?? this.hasPassword,
      twoFaEnabled: twoFaEnabled ?? this.twoFaEnabled,
      assignedDeviceId: assignedDeviceId ?? this.assignedDeviceId,
    );
  }

  Map<String, dynamic> toJson() => {
    'user_id': userId,
    'email': email,
    'recovery_email': recoveryEmail,
    'has_recovery_email': hasRecoveryEmail,
    'username': username,
    'avatar_data': avatarData,
    'stoken': stoken,
    'stoken_expires_at': stokenExpiresAtUnix,
    'ltoken': ltoken,
    'ltoken_expires_at': ltokenExpiresAtUnix,
    'account_status': accountStatus,
    'locked': locked,
    'has_email': hasEmail,
    'has_password': hasPassword,
    'two_fa_enabled': twoFaEnabled,
    if (assignedDeviceId != null && assignedDeviceId!.isNotEmpty)
      'device_id': assignedDeviceId,
  };

  factory AccountSessionData.fromStoredJson(Map<String, dynamic> json) {
    final hasEmail = json['has_email'] != false;
    final hasRecoveryEmail = json['has_recovery_email'] == true;
    final hasPassword = json['has_password'] != false;
    return AccountSessionData(
      userId: (json['user_id'] ?? '').toString(),
      email: hasEmail ? (json['email'] ?? '').toString() : '',
      recoveryEmail: hasRecoveryEmail
          ? (json['recovery_email'] ?? '').toString()
          : '',
      hasRecoveryEmail: hasRecoveryEmail,
      username: (json['username'] ?? '').toString(),
      avatarData: json['avatar_data'] as String?,
      stoken: (json['stoken'] ?? '').toString(),
      stokenExpiresAtUnix: (json['stoken_expires_at'] as num?)?.toInt() ?? 0,
      ltoken: (json['ltoken'] ?? '').toString(),
      ltokenExpiresAtUnix: (json['ltoken_expires_at'] as num?)?.toInt() ?? 0,
      accountStatus: (json['account_status'] ?? 'active').toString(),
      locked: json['locked'] == true,
      hasEmail: hasEmail,
      hasPassword: hasPassword,
      twoFaEnabled: json['two_fa_enabled'] == true,
      assignedDeviceId: (json['device_id'] as String?)?.trim(),
    );
  }

  factory AccountSessionData.fromLoginPayload(Map<String, dynamic> json) {
    final now = DateTime.now().millisecondsSinceEpoch ~/ 1000;
    final stExp = (json['stoken_expires_in'] as num?)?.toInt() ?? 0;
    final ltExp = (json['ltoken_expires_in'] as num?)?.toInt() ?? 0;
    final status = (json['account_status'] ?? 'active').toString();
    final locked = json['locked'] == true || status.toLowerCase() == 'locked';
    final hasEmail = json['has_email'] != false;
    final hasRecoveryEmail = json['has_recovery_email'] == true;
    final hasPassword = json['has_password'] != false;
    return AccountSessionData(
      userId: (json['user_id'] ?? '').toString(),
      email: hasEmail ? (json['email'] ?? '').toString() : '',
      recoveryEmail: hasRecoveryEmail
          ? (json['recovery_email'] ?? '').toString()
          : '',
      hasRecoveryEmail: hasRecoveryEmail,
      username: (json['username'] ?? '').toString(),
      avatarData: json['avatar_data'] as String?,
      stoken: (json['stoken'] ?? '').toString(),
      stokenExpiresAtUnix: stExp > 0 ? now + stExp : 0,
      ltoken: (json['ltoken'] ?? '').toString(),
      ltokenExpiresAtUnix: ltExp > 0 ? now + ltExp : 0,
      accountStatus: status,
      locked: locked,
      hasEmail: hasEmail,
      hasPassword: hasPassword,
      twoFaEnabled: json['two_fa_enabled'] == true,
      assignedDeviceId: (json['device_id'] as String?)?.trim(),
    );
  }
}

class OAuthProvidersData {
  const OAuthProvidersData({
    this.google = false,
    this.qq = false,
    this.wechat = false,
  });

  final bool google;
  final bool qq;
  final bool wechat;

  bool get hasAny => google || qq || wechat;

  bool isAvailable(String provider) {
    switch (provider) {
      case 'google':
        return google;
      case 'qq':
        return qq;
      case 'wechat':
        return wechat;
      default:
        return false;
    }
  }

  factory OAuthProvidersData.fromJson(Map<String, dynamic> json) {
    return OAuthProvidersData(
      google: json['google'] == true,
      qq: json['qq'] == true,
      wechat: json['wechat'] == true,
    );
  }
}

class OAuthStartData {
  const OAuthStartData({
    required this.provider,
    required this.state,
    required this.pollSecret,
    required this.authorizeUrl,
    required this.pollUrl,
    required this.expiresIn,
    required this.pollIntervalMs,
  });

  final String provider;
  final String state;
  final String pollSecret;
  final String authorizeUrl;
  final String pollUrl;
  final int expiresIn;
  final int pollIntervalMs;

  factory OAuthStartData.fromJson(Map<String, dynamic> json) {
    return OAuthStartData(
      provider: (json['provider'] ?? '').toString(),
      state: (json['state'] ?? '').toString(),
      pollSecret: (json['poll_secret'] ?? '').toString(),
      authorizeUrl: (json['authorize_url'] ?? '').toString(),
      pollUrl: (json['poll_url'] ?? '').toString(),
      expiresIn: (json['expires_in'] as num?)?.toInt() ?? 300,
      pollIntervalMs: (json['poll_interval_ms'] as num?)?.toInt() ?? 1200,
    );
  }
}

class OAuthStatusData {
  const OAuthStatusData({
    required this.state,
    this.intent = 'login',
    this.provider,
    this.bound = false,
    this.expiresIn = 0,
    this.twoFaToken = '',
    this.session,
  });

  final String state;
  final String intent;
  final String? provider;
  final bool bound;
  final int expiresIn;
  final String twoFaToken;
  final AccountSessionData? session;

  factory OAuthStatusData.fromJson(Map<String, dynamic> json) {
    final rawSess = json['session'];
    return OAuthStatusData(
      state: (json['state'] ?? '').toString(),
      intent: (json['intent'] ?? 'login').toString(),
      provider: json['provider'] as String?,
      bound: json['bound'] == true,
      expiresIn: (json['expires_in'] as num?)?.toInt() ?? 0,
      twoFaToken: (json['two_fa_token'] ?? '').toString().trim(),
      session: rawSess is Map<String, dynamic>
          ? AccountSessionData.fromLoginPayload(rawSess)
          : null,
    );
  }
}

class OAuthBindingsData {
  const OAuthBindingsData({
    this.available = const OAuthProvidersData(),
    this.bound = const OAuthProvidersData(),
  });

  final OAuthProvidersData available;
  final OAuthProvidersData bound;

  bool get hasAnyVisible => available.hasAny || bound.hasAny;

  bool isVisible(String provider) =>
      available.isAvailable(provider) || bound.isAvailable(provider);

  bool isAvailable(String provider) => available.isAvailable(provider);

  bool isBound(String provider) => bound.isAvailable(provider);

  factory OAuthBindingsData.fromJson(Map<String, dynamic> json) {
    final rawAvail = json['available'];
    final rawBound = json['bound'];
    return OAuthBindingsData(
      available: rawAvail is Map<String, dynamic>
          ? OAuthProvidersData.fromJson(rawAvail)
          : const OAuthProvidersData(),
      bound: rawBound is Map<String, dynamic>
          ? OAuthProvidersData.fromJson(rawBound)
          : const OAuthProvidersData(),
    );
  }
}

class AccountDeviceDto {
  const AccountDeviceDto({
    required this.deviceId,
    required this.deviceName,
    this.rawDeviceName,
    this.customName,
    this.osVersion = 'Windows',
    this.lastIp = '',
    this.isOnline = false,
    this.allowRemote = true,
    this.allowRemotePower = false,
    this.allowQuickLaunch = false,
    this.wolMacs = const [],
    this.wolBroadcasts = const [],
    this.wolReady = false,
    this.isBeingControlled = false,
    this.activeSessionId,
    this.controllerUserName,
    this.controllerDeviceId,
    this.lastActiveAt = 0,
  });

  final String deviceId;
  final String deviceName;
  final String? rawDeviceName;
  final String? customName;
  final String osVersion;
  final String lastIp;
  final bool isOnline;
  final bool allowRemote;
  final bool allowRemotePower;
  final bool allowQuickLaunch;
  final List<String> wolMacs;
  final List<String> wolBroadcasts;
  final bool wolReady;
  final bool isBeingControlled;
  final String? activeSessionId;
  final String? controllerUserName;
  final String? controllerDeviceId;
  final int lastActiveAt;

  bool get isComputerDevice {
    final lower = osVersion.toLowerCase();
    return !lower.contains('android') &&
        !lower.contains('ios') &&
        !lower.contains('iphone') &&
        !lower.contains('ipad') &&
        !lower.contains('harmonyos');
  }

  String get displayName {
    if (customName != null && customName!.trim().isNotEmpty) {
      return customName!.trim();
    }
    if (deviceName.trim().isNotEmpty) return deviceName.trim();
    if (rawDeviceName != null && rawDeviceName!.trim().isNotEmpty) {
      return rawDeviceName!.trim();
    }
    return deviceId;
  }

  factory AccountDeviceDto.fromJson(Map<String, dynamic> json) {
    int parseTimestamp(dynamic raw) {
      if (raw is num) return raw.toInt();
      if (raw is String) {
        final parsedNum = int.tryParse(raw);
        if (parsedNum != null) return parsedNum;
        final dt = DateTime.tryParse(raw);
        if (dt != null) return dt.millisecondsSinceEpoch ~/ 1000;
      }
      return 0;
    }

    List<String> parseStringList(dynamic raw) {
      if (raw is! List) return const [];
      return raw
          .whereType<String>()
          .map((s) => s.trim())
          .where((s) => s.isNotEmpty)
          .toList(growable: false);
    }

    return AccountDeviceDto(
      deviceId: (json['device_id'] ?? json['deviceId'] ?? '').toString(),
      deviceName: (json['device_name'] ?? json['deviceName'] ?? '').toString(),
      rawDeviceName:
          (json['raw_device_name'] ?? json['rawDeviceName']) as String?,
      customName: (json['custom_name'] ?? json['customName']) as String?,
      osVersion: (json['os_version'] ?? json['osVersion'] ?? 'Windows')
          .toString(),
      lastIp: (json['last_ip'] ?? json['lastIp'] ?? '').toString(),
      isOnline: json['is_online'] == true || json['isOnline'] == true,
      allowRemote:
          json['allow_remote'] != false && json['allowRemote'] != false,
      allowRemotePower:
          json['allow_remote_power'] == true ||
          json['allowRemotePower'] == true,
      allowQuickLaunch:
          json['allow_quick_launch'] == true ||
          json['allowQuickLaunch'] == true,
      wolMacs: parseStringList(json['wol_macs'] ?? json['wolMacs']),
      wolBroadcasts: parseStringList(
        json['wol_broadcasts'] ?? json['wolBroadcasts'],
      ),
      wolReady: json['wol_ready'] == true || json['wolReady'] == true,
      isBeingControlled:
          json['is_being_controlled'] == true ||
          json['isBeingControlled'] == true,
      activeSessionId:
          (json['active_session_id'] ?? json['activeSessionId']) as String?,
      controllerUserName:
          (json['controller_user_name'] ?? json['controllerUserName'])
              as String?,
      controllerDeviceId:
          (json['controller_device_id'] ?? json['controllerDeviceId'])
              as String?,
      lastActiveAt: parseTimestamp(
        json['last_active_at'] ?? json['lastActiveAt'],
      ),
    );
  }
}

class QuickLaunchAppDto {
  const QuickLaunchAppDto({
    required this.id,
    required this.deviceId,
    required this.deviceName,
    required this.name,
    required this.exePath,
    this.iconData,
    this.deviceCached = true,
    this.deviceOnline = false,
    this.allowRemote = false,
    this.allowQuickLaunch = false,
    this.createdAt = '',
  });

  final String id;
  final String deviceId;
  final String deviceName;
  final String name;
  final String exePath;
  final String? iconData;
  final bool deviceCached;
  final bool deviceOnline;
  final bool allowRemote;
  final bool allowQuickLaunch;
  final String createdAt;

  factory QuickLaunchAppDto.fromJson(Map<String, dynamic> json) {
    final hasCached =
        json.containsKey('device_cached') || json.containsKey('deviceCached');
    return QuickLaunchAppDto(
      id: (json['id'] ?? '').toString(),
      deviceId: (json['device_id'] ?? json['deviceId'] ?? '').toString(),
      deviceName: (json['device_name'] ?? json['deviceName'] ?? '').toString(),
      name: (json['name'] ?? 'App').toString(),
      exePath: (json['exe_path'] ?? json['exePath'] ?? '').toString(),
      iconData: (json['icon_data'] ?? json['iconData']) as String?,
      deviceCached: hasCached
          ? (json['device_cached'] == true || json['deviceCached'] == true)
          : true,
      deviceOnline:
          json['device_online'] == true || json['deviceOnline'] == true,
      allowRemote: json['allow_remote'] == true || json['allowRemote'] == true,
      allowQuickLaunch:
          json['allow_quick_launch'] == true ||
          json['allowQuickLaunch'] == true,
      createdAt: (json['created_at'] ?? json['createdAt'] ?? '').toString(),
    );
  }
}

class DevicePowerResultData {
  const DevicePowerResultData({
    required this.action,
    required this.targetDeviceId,
    required this.delivered,
    this.alreadyOnline = false,
    this.targetOnline = false,
    this.delaySeconds = 3,
    this.relayedCount = 0,
    this.sameSubnetRelayedCount = 0,
    this.wolMacs = const [],
    this.wolBroadcasts = const [],
  });

  final String action;
  final String targetDeviceId;
  final bool delivered;
  final bool alreadyOnline;
  final bool targetOnline;
  final int delaySeconds;
  final int relayedCount;
  final int sameSubnetRelayedCount;
  final List<String> wolMacs;
  final List<String> wolBroadcasts;

  factory DevicePowerResultData.fromJson(Map<String, dynamic> json) {
    List<String> parseList(dynamic raw) {
      if (raw is! List) return const [];
      return raw
          .whereType<String>()
          .map((s) => s.trim())
          .where((s) => s.isNotEmpty)
          .toList(growable: false);
    }

    return DevicePowerResultData(
      action: (json['action'] ?? '').toString(),
      targetDeviceId: (json['target_device_id'] ?? '').toString(),
      delivered: json['delivered'] == true,
      alreadyOnline: json['already_online'] == true,
      targetOnline: json['target_online'] == true,
      delaySeconds: (json['delay_seconds'] as num?)?.toInt() ?? 3,
      relayedCount: (json['relayed_count'] as num?)?.toInt() ?? 0,
      sameSubnetRelayedCount:
          (json['same_subnet_relayed_count'] as num?)?.toInt() ?? 0,
      wolMacs: parseList(json['wol_macs']),
      wolBroadcasts: parseList(json['wol_broadcasts']),
    );
  }
}

class AccountApiResult<T> {
  const AccountApiResult({
    required this.ok,
    required this.httpStatusCode,
    this.code = 0,
    this.message = '',
    this.data,
    this.challenge,
    this.twoFaToken = '',
    this.retryAfterSec,
  });

  final bool ok;
  final int httpStatusCode;
  final int code;
  final String message;
  final T? data;
  final AccountChallengeInfo? challenge;
  final String twoFaToken;
  final int? retryAfterSec;

  bool get needsCaptcha =>
      challenge != null && code == OsmiumErrorCode.accountCaptchaRequired;

  bool get requiresTwoFa =>
      twoFaToken.isNotEmpty && code == OsmiumErrorCode.twoFaRequired;

  bool get isPending => code == OsmiumErrorCode.challengePending;
}

class QrParsedRequest {
  const QrParsedRequest({
    required this.qrId,
    required this.qrUrl,
    required this.channel,
    this.extTs,
    this.extNonce,
    this.extSig,
  });

  final String qrId;
  final String qrUrl;
  final String channel; // 'in_app' | 'external'
  final String? extTs;
  final String? extNonce;
  final String? extSig;

  bool get isExternal => channel == 'external';

  static final RegExp _opaqueIdRe = RegExp(r'^[A-Za-z0-9_-]{8,128}$');
  static final RegExp _digitsTsRe = RegExp(r'^\d{10,13}$');

  static String _sanitizeRaw(String raw) {
    return raw
        .replaceAll(RegExp('[\u0000-\u001F\u007F​-‍﻿]'), '')
        .replaceAll('&amp;', '&')
        .trim();
  }

  /// Tries in-app QR URL first, then external deep link (`osmium://` or
  /// Android `intent://qr-login?...#Intent;...`).
  static QrParsedRequest? tryParseAny(
    String raw, {
    String? expectedBaseUrl,
  }) {
    return tryParseInApp(raw, expectedBaseUrl: expectedBaseUrl) ??
        tryParseExternalDeepLink(raw, expectedBaseUrl: expectedBaseUrl);
  }

  /// Validates an in-app scanned QR code URL: strict HTTPS scheme, official
  /// Osmium API host/port, `/qr/login` path, and required signed parameters.
  static QrParsedRequest? tryParseInApp(
    String raw, {
    String? expectedBaseUrl,
  }) {
    final trimmed = _sanitizeRaw(raw);
    if (trimmed.isEmpty || trimmed.length > 2048) return null;
    final uri = Uri.tryParse(trimmed);
    final base = Uri.parse(
      expectedBaseUrl ?? OsmiumConstants.defaultApiBaseUrl,
    );
    if (uri == null || !uri.hasScheme || !uri.hasAuthority) return null;
    if (uri.scheme.toLowerCase() != base.scheme.toLowerCase()) return null;
    if (uri.host.toLowerCase() != base.host.toLowerCase()) return null;
    if (_effectivePort(uri) != _effectivePort(base)) return null;
    final normalizedPath = uri.path.replaceFirst(RegExp(r'/+$'), '');
    if (normalizedPath != '/qr/login') return null;

    final baseParams = _validateBaseParams(uri.queryParameters);
    if (baseParams == null) return null;

    final canonicalUrl = _buildCanonicalQrUrl(base, baseParams);
    return QrParsedRequest(
      qrId: baseParams['qid']!,
      qrUrl: canonicalUrl,
      channel: 'in_app',
    );
  }

  /// Validates an external deep link (`osmium://qr-login?...` or Android
  /// `intent://qr-login?...#Intent;scheme=osmium;...`) issued by the
  /// server landing page, requiring the independent external signature params.
  static QrParsedRequest? tryParseExternalDeepLink(
    String raw, {
    String? expectedBaseUrl,
  }) {
    var trimmed = _sanitizeRaw(raw);
    if (trimmed.isEmpty || trimmed.length > 2048) return null;
    if (trimmed.toLowerCase().startsWith('intent://qr-login')) {
      final hashIdx = trimmed.indexOf('#Intent;');
      final body = hashIdx > 0 ? trimmed.substring(0, hashIdx) : trimmed;
      trimmed = 'osmium://${body.substring('intent://'.length)}';
    }
    final uri = Uri.tryParse(trimmed);
    if (uri == null) return null;
    if (uri.scheme.toLowerCase() != 'osmium') return null;
    if (uri.host.toLowerCase() != 'qr-login') return null;

    final q = uri.queryParameters;
    final baseParams = _validateBaseParams(q);
    if (baseParams == null) return null;

    final extTs = (q['ext_ts'] ?? '').trim();
    final extNonce = (q['ext_nonce'] ?? '').trim();
    final extSig = (q['ext_sig'] ?? '').trim();
    if (!_digitsTsRe.hasMatch(extTs) ||
        !_opaqueIdRe.hasMatch(extNonce) ||
        extSig.isEmpty ||
        extSig.length > 128) {
      return null;
    }

    final base = Uri.parse(
      expectedBaseUrl ?? OsmiumConstants.defaultApiBaseUrl,
    );
    final canonicalUrl = _buildCanonicalQrUrl(base, baseParams);
    return QrParsedRequest(
      qrId: baseParams['qid']!,
      qrUrl: canonicalUrl,
      channel: 'external',
      extTs: extTs,
      extNonce: extNonce,
      extSig: extSig,
    );
  }

  static int _effectivePort(Uri uri) {
    if (uri.hasPort) return uri.port;
    return uri.scheme.toLowerCase() == 'https' ? 443 : 80;
  }

  static Map<String, String>? _validateBaseParams(Map<String, String> q) {
    final v = (q['v'] ?? '').trim();
    final qid = (q['qid'] ?? '').trim();
    final ts = (q['ts'] ?? '').trim();
    final exp = (q['exp'] ?? '').trim();
    final nonce = (q['nonce'] ?? '').trim();
    final sig = (q['sig'] ?? '').trim();

    if (v != '1') return null;
    if (!_opaqueIdRe.hasMatch(qid) || !_opaqueIdRe.hasMatch(nonce)) return null;
    if (!_digitsTsRe.hasMatch(ts) || !_digitsTsRe.hasMatch(exp)) return null;
    if (sig.isEmpty || sig.length > 128) return null;

    final tsNum = int.tryParse(ts);
    final expNum = int.tryParse(exp);
    if (tsNum == null || expNum == null || expNum <= tsNum) return null;

    return {
      'v': '1',
      'qid': qid,
      'ts': ts,
      'exp': exp,
      'nonce': nonce,
      'sig': sig,
    };
  }

  static String _buildCanonicalQrUrl(Uri base, Map<String, String> p) {
    return base
        .replace(
          path: '/qr/login',
          queryParameters: {
            'v': p['v']!,
            'qid': p['qid']!,
            'ts': p['ts']!,
            'exp': p['exp']!,
            'nonce': p['nonce']!,
            'sig': p['sig']!,
          },
        )
        .toString();
  }
}

class QrMobileRegisterData {
  const QrMobileRegisterData({
    required this.qrId,
    required this.channel,
    required this.scanToken,
    required this.scanNonce,
    required this.verifyUrl,
    required this.requiredHeaders,
    required this.expiresIn,
  });

  final String qrId;
  final String channel;
  final String scanToken;
  final String scanNonce;
  final String verifyUrl;
  final Map<String, String> requiredHeaders;
  final int expiresIn;

  factory QrMobileRegisterData.fromJson(Map<String, dynamic> json) {
    final rawHeaders = json['required_headers'];
    final headers = <String, String>{};
    if (rawHeaders is Map) {
      for (final entry in rawHeaders.entries) {
        final k = entry.key.toString().trim();
        final v = entry.value?.toString().trim() ?? '';
        if (k.isNotEmpty && v.isNotEmpty) {
          headers[k] = v;
        }
      }
    }
    return QrMobileRegisterData(
      qrId: (json['qr_id'] ?? '').toString(),
      channel: (json['channel'] ?? 'in_app').toString(),
      scanToken: (json['scan_token'] ?? '').toString(),
      scanNonce: (json['scan_nonce'] ?? '').toString(),
      verifyUrl: (json['verify_url'] ?? '').toString(),
      requiredHeaders: headers,
      expiresIn: (json['expires_in'] as num?)?.toInt() ?? 180,
    );
  }
}

class QrPcLoginInfo {
  const QrPcLoginInfo({
    required this.deviceId,
    required this.deviceName,
    required this.osVersion,
    required this.ipMasked,
    required this.ipLocation,
    required this.loginTimeMs,
    required this.loginTimeIso,
  });

  final String deviceId;
  final String deviceName;
  final String osVersion;
  final String ipMasked;
  final String ipLocation;
  final int loginTimeMs;
  final String loginTimeIso;

  DateTime? get loginDateTime {
    if (loginTimeMs > 0) {
      return DateTime.fromMillisecondsSinceEpoch(loginTimeMs).toLocal();
    }
    if (loginTimeIso.isNotEmpty) {
      return DateTime.tryParse(loginTimeIso)?.toLocal();
    }
    return null;
  }

  factory QrPcLoginInfo.fromJson(Map<String, dynamic> json) {
    return QrPcLoginInfo(
      deviceId: (json['device_id'] ?? '').toString(),
      deviceName: (json['device_name'] ?? 'Osmium PC').toString(),
      osVersion: (json['os_version'] ?? 'Windows').toString(),
      ipMasked: (json['ip_masked'] ?? '').toString(),
      ipLocation: (json['ip_location'] ?? '未知位置').toString(),
      loginTimeMs: (json['login_time'] as num?)?.toInt() ?? 0,
      loginTimeIso: (json['login_time_iso'] ?? '').toString(),
    );
  }
}

class QrConfirmTicket {
  const QrConfirmTicket({
    required this.qrId,
    required this.confirmToken,
    required this.channel,
    required this.confirmDelaySec,
    required this.confirmUnlockAtMs,
    required this.expiresIn,
    required this.pcInfo,
  });

  final String qrId;
  final String confirmToken;
  final String channel;
  final int confirmDelaySec;
  final int confirmUnlockAtMs;
  final int expiresIn;
  final QrPcLoginInfo pcInfo;

  bool get isExternal => channel == 'external';

  factory QrConfirmTicket.fromJson(Map<String, dynamic> json) {
    final pcRaw = json['pc_info'];
    final pcMap = pcRaw is Map<String, dynamic>
        ? pcRaw
        : const <String, dynamic>{};
    return QrConfirmTicket(
      qrId: (json['qr_id'] ?? '').toString(),
      confirmToken: (json['confirm_token'] ?? '').toString(),
      channel: (json['channel'] ?? 'in_app').toString(),
      confirmDelaySec: (json['confirm_delay_sec'] as num?)?.toInt() ?? 3,
      confirmUnlockAtMs: (json['confirm_unlock_at'] as num?)?.toInt() ?? 0,
      expiresIn: (json['expires_in'] as num?)?.toInt() ?? 180,
      pcInfo: QrPcLoginInfo.fromJson(pcMap),
    );
  }
}

class LoginKeyItemDto {
  const LoginKeyItemDto({
    required this.id,
    required this.name,
    required this.keyPrefix,
    this.createdAt = 0,
    this.lastUsedAt = 0,
  });

  final String id;
  final String name;
  final String keyPrefix;
  final int createdAt;
  final int lastUsedAt;

  factory LoginKeyItemDto.fromJson(Map<String, dynamic> json) {
    return LoginKeyItemDto(
      id: (json['id'] ?? '').toString(),
      name: (json['name'] ?? '登录密钥').toString(),
      keyPrefix: (json['key_prefix'] ?? '').toString(),
      createdAt: (json['created_at'] as num?)?.toInt() ?? 0,
      lastUsedAt: (json['last_used_at'] as num?)?.toInt() ?? 0,
    );
  }
}

class LoginKeysListData {
  const LoginKeysListData({
    required this.keys,
    this.maxKeys = 10,
  });

  final List<LoginKeyItemDto> keys;
  final int maxKeys;

  factory LoginKeysListData.fromJson(Map<String, dynamic> json) {
    final raw = json['keys'];
    final items = raw is List
        ? raw
              .whereType<Map<String, dynamic>>()
              .map(LoginKeyItemDto.fromJson)
              .toList(growable: false)
        : const <LoginKeyItemDto>[];
    return LoginKeysListData(
      keys: items,
      maxKeys: (json['max_keys'] as num?)?.toInt() ?? 10,
    );
  }
}

class LoginKeyCreateData {
  const LoginKeyCreateData({
    required this.id,
    required this.name,
    required this.keyPrefix,
    required this.loginKey,
    this.createdAt = 0,
  });

  final String id;
  final String name;
  final String keyPrefix;
  final String loginKey;
  final int createdAt;

  factory LoginKeyCreateData.fromJson(Map<String, dynamic> json) {
    return LoginKeyCreateData(
      id: (json['id'] ?? '').toString(),
      name: (json['name'] ?? '登录密钥').toString(),
      keyPrefix: (json['key_prefix'] ?? '').toString(),
      loginKey: (json['login_key'] ?? '').toString(),
      createdAt: (json['created_at'] as num?)?.toInt() ?? 0,
    );
  }
}

class RecoveryChannelDto {
  const RecoveryChannelDto({
    required this.channel,
    required this.masked,
  });

  final String channel;
  final String masked;

  bool get isRecovery => channel.toLowerCase() == 'recovery';

  factory RecoveryChannelDto.fromJson(Map<String, dynamic> json) {
    return RecoveryChannelDto(
      channel: (json['channel'] ?? 'primary').toString(),
      masked: (json['masked'] ?? '').toString(),
    );
  }
}

class RecoveryLookupData {
  const RecoveryLookupData({
    required this.recoveryToken,
    required this.expiresIn,
    required this.channels,
  });

  final String recoveryToken;
  final int expiresIn;
  final List<RecoveryChannelDto> channels;

  factory RecoveryLookupData.fromJson(Map<String, dynamic> json) {
    final raw = json['channels'];
    final items = raw is List
        ? raw
              .whereType<Map<String, dynamic>>()
              .map(RecoveryChannelDto.fromJson)
              .toList(growable: false)
        : const <RecoveryChannelDto>[];
    return RecoveryLookupData(
      recoveryToken: (json['recovery_token'] ?? '').toString(),
      expiresIn: (json['expires_in'] as num?)?.toInt() ?? 600,
      channels: items,
    );
  }
}
