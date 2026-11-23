import '../constants/osmium_constants.dart';

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
    required this.username,
    this.avatarData,
    required this.stoken,
    required this.stokenExpiresAtUnix,
    required this.ltoken,
    required this.ltokenExpiresAtUnix,
    this.accountStatus = 'active',
    this.locked = false,
  });

  final String userId;
  final String email;
  final String username;
  final String? avatarData;
  final String stoken;
  final int stokenExpiresAtUnix;
  final String ltoken;
  final int ltokenExpiresAtUnix;
  final String accountStatus;
  final bool locked;

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

  AccountSessionData copyWith({
    String? userId,
    String? email,
    String? username,
    String? avatarData,
    String? stoken,
    int? stokenExpiresAtUnix,
    String? ltoken,
    int? ltokenExpiresAtUnix,
    String? accountStatus,
    bool? locked,
  }) {
    return AccountSessionData(
      userId: userId ?? this.userId,
      email: email ?? this.email,
      username: username ?? this.username,
      avatarData: avatarData ?? this.avatarData,
      stoken: stoken ?? this.stoken,
      stokenExpiresAtUnix: stokenExpiresAtUnix ?? this.stokenExpiresAtUnix,
      ltoken: ltoken ?? this.ltoken,
      ltokenExpiresAtUnix: ltokenExpiresAtUnix ?? this.ltokenExpiresAtUnix,
      accountStatus: accountStatus ?? this.accountStatus,
      locked: locked ?? this.locked,
    );
  }

  Map<String, dynamic> toJson() => {
    'user_id': userId,
    'email': email,
    'username': username,
    'avatar_data': avatarData,
    'stoken': stoken,
    'stoken_expires_at': stokenExpiresAtUnix,
    'ltoken': ltoken,
    'ltoken_expires_at': ltokenExpiresAtUnix,
    'account_status': accountStatus,
    'locked': locked,
  };

  factory AccountSessionData.fromStoredJson(Map<String, dynamic> json) {
    return AccountSessionData(
      userId: (json['user_id'] ?? '').toString(),
      email: (json['email'] ?? '').toString(),
      username: (json['username'] ?? '').toString(),
      avatarData: json['avatar_data'] as String?,
      stoken: (json['stoken'] ?? '').toString(),
      stokenExpiresAtUnix: (json['stoken_expires_at'] as num?)?.toInt() ?? 0,
      ltoken: (json['ltoken'] ?? '').toString(),
      ltokenExpiresAtUnix: (json['ltoken_expires_at'] as num?)?.toInt() ?? 0,
      accountStatus: (json['account_status'] ?? 'active').toString(),
      locked: json['locked'] == true,
    );
  }

  factory AccountSessionData.fromLoginPayload(Map<String, dynamic> json) {
    final now = DateTime.now().millisecondsSinceEpoch ~/ 1000;
    final stExp = (json['stoken_expires_in'] as num?)?.toInt() ?? 0;
    final ltExp = (json['ltoken_expires_in'] as num?)?.toInt() ?? 0;
    final status = (json['account_status'] ?? 'active').toString();
    final locked = json['locked'] == true || status.toLowerCase() == 'locked';
    return AccountSessionData(
      userId: (json['user_id'] ?? '').toString(),
      email: (json['email'] ?? '').toString(),
      username: (json['username'] ?? '').toString(),
      avatarData: json['avatar_data'] as String?,
      stoken: (json['stoken'] ?? '').toString(),
      stokenExpiresAtUnix: stExp > 0 ? now + stExp : 0,
      ltoken: (json['ltoken'] ?? '').toString(),
      ltokenExpiresAtUnix: ltExp > 0 ? now + ltExp : 0,
      accountStatus: status,
      locked: locked,
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
  final bool isBeingControlled;
  final String? activeSessionId;
  final String? controllerUserName;
  final String? controllerDeviceId;
  final int lastActiveAt;

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

class AccountApiResult<T> {
  const AccountApiResult({
    required this.ok,
    required this.httpStatusCode,
    this.code = 0,
    this.message = '',
    this.data,
    this.challenge,
  });

  final bool ok;
  final int httpStatusCode;
  final int code;
  final String message;
  final T? data;
  final AccountChallengeInfo? challenge;

  /// Mirrors Windows `AccountApiResult.RequiresCaptcha`: a challenge is only
  /// actionable when the envelope carries the captcha code, not merely HTTP 202.
  bool get needsCaptcha =>
      challenge != null && code == OsmiumErrorCode.accountCaptchaRequired;

  /// Mirrors Windows `AccountApiResult.IsPending` (code 101). Pending is a
  /// successful poll state, never a terminal error.
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

  /// Validates an in-app scanned QR code URL: strict HTTPS scheme, official
  /// Osmium API host/port, `/qr/login` path, and required signed parameters.
  static QrParsedRequest? tryParseInApp(
    String raw, {
    String? expectedBaseUrl,
  }) {
    final trimmed = raw.trim();
    if (trimmed.isEmpty || trimmed.length > 2048) return null;
    final uri = Uri.tryParse(trimmed);
    final base = Uri.parse(
      expectedBaseUrl ?? OsmiumConstants.defaultApiBaseUrl,
    );
    if (uri == null || !uri.hasScheme || !uri.hasAuthority) return null;
    if (uri.scheme.toLowerCase() != base.scheme.toLowerCase()) return null;
    if (uri.host.toLowerCase() != base.host.toLowerCase()) return null;
    if (_effectivePort(uri) != _effectivePort(base)) return null;
    if (uri.path != '/qr/login') return null;

    final baseParams = _validateBaseParams(uri.queryParameters);
    if (baseParams == null) return null;

    final canonicalUrl = _buildCanonicalQrUrl(base, baseParams);
    return QrParsedRequest(
      qrId: baseParams['qid']!,
      qrUrl: canonicalUrl,
      channel: 'in_app',
    );
  }

  /// Validates an external deep link (`osmium://qr-login?...`) issued by the
  /// server landing page, requiring the independent external signature params.
  static QrParsedRequest? tryParseExternalDeepLink(
    String raw, {
    String? expectedBaseUrl,
  }) {
    final trimmed = raw.trim();
    if (trimmed.isEmpty || trimmed.length > 2048) return null;
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
