import 'dart:async';
import 'dart:io';

import 'package:dio/dio.dart';

import '../../core/constants/osmium_constants.dart';
import '../../core/models/account_models.dart';
import '../../core/models/device_identity.dart';
import '../storage/secure_storage_service.dart';

class AccountService {
  AccountService(
    this._secureStorage, {
    Dio? dio,
    AccountSessionData? savedSession,
  }) : _session = savedSession {
    _dio =
        dio ??
        Dio(
          BaseOptions(
            baseUrl: OsmiumConstants.defaultApiBaseUrl,
            connectTimeout: const Duration(seconds: 15),
            receiveTimeout: const Duration(seconds: 25),
            headers: {
              'User-Agent': OsmiumConstants.userAgent,
              'Content-Type': 'application/json',
            },
            validateStatus: (_) => true,
          ),
        );
  }

  final SecureStorageService _secureStorage;
  late final Dio _dio;

  AccountSessionData? _session;
  AccountSessionData? get session => _session;
  bool get isLoggedIn => _session != null && _session!.isStokenValid;
  String get currentLtoken =>
      (_session != null && _session!.isLtokenValid) ? _session!.ltoken : '';

  final _sessionController = StreamController<AccountSessionData?>.broadcast();
  Stream<AccountSessionData?> get sessionStream => _sessionController.stream;

  /// Restores the saved sign-in without touching the network; the caller
  /// refreshes it with [rotateLtoken] in the background.
  Future<AccountSessionData?> initialize() async {
    _session ??= await _secureStorage.loadAccountSession();
    _sessionController.add(_session);
    return _session;
  }

  Uri resolveUri(String relativeOrAbsolute) {
    final base = Uri.parse('${OsmiumConstants.defaultApiBaseUrl}/');
    final parsed = Uri.tryParse(relativeOrAbsolute);
    if (parsed != null && parsed.hasScheme) {
      return parsed;
    }
    return base.resolve(relativeOrAbsolute.replaceFirst(RegExp(r'^/+'), ''));
  }

  /// Parses the server envelope the same way Windows `ParseEnvelope` does:
  /// `ok` is the envelope flag (never inferred from the HTTP status), the
  /// challenge exists only for HTTP 202 + code 100, and code 101 is pending.
  AccountApiResult<T> _parseEnvelope<T>(
    Response<dynamic> resp,
    T Function(Map<String, dynamic> data)? mapper,
  ) {
    final status = resp.statusCode ?? 0;
    final body = resp.data;
    final map = body is Map<String, dynamic> ? body : const <String, dynamic>{};

    final okFlag = map['ok'] == true;
    final code = (map['code'] as num?)?.toInt() ?? (okFlag ? 0 : status);
    final message = (map['message'] ?? map['error'] ?? '').toString();

    AccountChallengeInfo? challenge;
    if (status == 202 && code == OsmiumErrorCode.accountCaptchaRequired) {
      final rawChallenge = map['challenge'] ?? map['data'];
      if (rawChallenge is Map<String, dynamic>) {
        challenge = AccountChallengeInfo.fromJson(rawChallenge);
      }
    }

    T? mappedData;
    final rawData = map['data'];
    if (challenge == null &&
        code != OsmiumErrorCode.challengePending &&
        okFlag &&
        mapper != null &&
        rawData is Map<String, dynamic>) {
      mappedData = mapper(rawData);
    }

    return AccountApiResult<T>(
      ok: okFlag,
      httpStatusCode: status,
      code: code,
      message: message,
      data: mappedData,
      challenge: challenge,
    );
  }

  Future<AccountApiResult<AccountSessionData>> startLogin({
    required String email,
    required String password,
    required DeviceIdentity identity,
  }) async {
    try {
      final resp = await _dio.post<dynamic>(
        '/api/auth/login',
        data: {
          'email': email.trim(),
          'password': password,
          'device_id': identity.deviceId,
          'device_name': identity.deviceName,
          'os_version': Platform.isAndroid
              ? 'Android'
              : Platform.operatingSystem,
        },
      );
      final parsed = _parseEnvelope<AccountSessionData>(
        resp,
        AccountSessionData.fromLoginPayload,
      );
      return await _finishSignIn(parsed, parsed.data);
    } catch (e) {
      return AccountApiResult(
        ok: false,
        httpStatusCode: 0,
        message: '网络连接失败: $e',
      );
    }
  }

  Future<AccountApiResult<Map<String, dynamic>>> pollChallenge({
    required AccountChallengeInfo challenge,
  }) async {
    try {
      final uri = resolveUri(
        challenge.pollUrl.isNotEmpty
            ? challenge.pollUrl
            : '/api/challenge/${challenge.challengeId}',
      );
      final resp = await _dio.getUri<dynamic>(
        uri,
        options: Options(
          headers: {'X-Challenge-Secret': challenge.challengeSecret},
        ),
      );
      final body = resp.data is Map<String, dynamic>
          ? resp.data as Map<String, dynamic>
          : <String, dynamic>{};
      final code = (body['code'] as num?)?.toInt() ?? 0;
      final statusText = (body['status'] ?? '').toString().toLowerCase();

      // Windows treats code 101 as the only pending state; every other
      // response (including `ok:false` and non-202 HTTP statuses) is terminal.
      if (code == OsmiumErrorCode.challengePending || statusText == 'pending') {
        return const AccountApiResult(
          ok: false,
          httpStatusCode: 202,
          code: OsmiumErrorCode.challengePending,
          message: 'pending',
        );
      }

      final parsed = _parseEnvelope<Map<String, dynamic>>(resp, (m) => m);
      final data = parsed.data;
      if (parsed.ok && data != null && data.containsKey('stoken')) {
        return await _finishSignIn(
          parsed,
          AccountSessionData.fromLoginPayload(data),
        );
      }
      return parsed;
    } catch (e) {
      return AccountApiResult(ok: false, httpStatusCode: 0, message: '$e');
    }
  }

  Future<AccountApiResult<Map<String, dynamic>>> startRegisterSendCode(
    String email,
  ) async {
    try {
      final resp = await _dio.post<dynamic>(
        '/api/auth/register/send-code',
        data: {'email': email.trim()},
      );
      return _parseEnvelope<Map<String, dynamic>>(resp, (m) => m);
    } catch (e) {
      return AccountApiResult(
        ok: false,
        httpStatusCode: 0,
        message: '网络连接失败: $e',
      );
    }
  }

  Future<AccountApiResult<AccountSessionData>> completeRegister({
    required String email,
    required String code,
    required String password,
    required String username,
    required DeviceIdentity identity,
  }) async {
    try {
      final resp = await _dio.post<dynamic>(
        '/api/auth/register/confirm',
        data: {
          'email': email.trim(),
          'username': username.trim(),
          'password': password,
          'code': code.trim(),
          'device_id': identity.deviceId,
          'device_name': identity.deviceName,
          'os_version': Platform.isAndroid
              ? 'Android'
              : Platform.operatingSystem,
        },
      );
      final parsed = _parseEnvelope<AccountSessionData>(
        resp,
        AccountSessionData.fromLoginPayload,
      );
      return await _finishSignIn(parsed, parsed.data);
    } catch (e) {
      return AccountApiResult(
        ok: false,
        httpStatusCode: 0,
        message: '网络连接失败: $e',
      );
    }
  }

  Future<AccountApiResult<AccountSessionData>> rotateLtoken() async {
    final cur = _session;
    if (cur == null || !cur.isStokenValid) {
      await clearSession();
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    try {
      final resp = await _dio.post<dynamic>(
        '/api/auth/ltoken',
        data: const <String, dynamic>{},
        options: Options(headers: {'X-SToken': cur.stoken}),
      );
      final status = resp.statusCode ?? 0;
      if (status == 403) {
        await clearSession();
        return const AccountApiResult(
          ok: false,
          httpStatusCode: 403,
          message: '登录凭证已失效',
        );
      }
      final parsed = _parseEnvelope<Map<String, dynamic>>(resp, (m) => m);
      if (parsed.ok && parsed.data != null) {
        final d = parsed.data!;
        final ltok = (d['ltoken'] ?? '').toString();
        if (ltok.isNotEmpty) {
          final now = DateTime.now().millisecondsSinceEpoch ~/ 1000;
          final exp = (d['expires_in'] as num?)?.toInt() ?? 0;
          final updated = cur.copyWith(
            userId: (d['user_id'] ?? cur.userId).toString(),
            email: (d['email'] ?? cur.email).toString(),
            username: (d['username'] ?? cur.username).toString(),
            avatarData: (d['avatar_data'] as String?) ?? cur.avatarData,
            ltoken: ltok,
            ltokenExpiresAtUnix: exp > 0 ? now + exp : 0,
            accountStatus: (d['account_status'] ?? cur.accountStatus)
                .toString(),
            locked: d['locked'] == true,
          );
          await _setSession(updated);
          return AccountApiResult(ok: true, httpStatusCode: 200, data: updated);
        }
      }
      return AccountApiResult(
        ok: false,
        httpStatusCode: status,
        message: parsed.message,
      );
    } catch (e) {
      return AccountApiResult(ok: false, httpStatusCode: 0, message: '$e');
    }
  }

  Future<bool> ensureAuthenticated() async {
    if (_session != null && _session!.isLtokenValid) return true;
    if (!isLoggedIn) return false;
    final res = await rotateLtoken();
    return res.ok && currentLtoken.isNotEmpty;
  }

  Future<Response<dynamic>> _sendAuthed(
    String method,
    String path, {
    Map<String, dynamic>? body,
    Map<String, String>? extraHeaders,
  }) async {
    await ensureAuthenticated();
    Future<Response<dynamic>> sendOnce() => _dio.request<dynamic>(
      path,
      data: body,
      options: Options(
        method: method,
        headers: {
          ...?extraHeaders,
          'X-LToken': currentLtoken,
        },
      ),
    );

    var resp = await sendOnce();
    if (resp.statusCode == 401 && isLoggedIn) {
      final rotated = await rotateLtoken();
      if (rotated.ok) {
        resp = await sendOnce();
      }
    }
    return resp;
  }

  Future<Response<dynamic>> _sendAuthedUri(
    String method,
    Uri uri, {
    Map<String, dynamic>? body,
    Map<String, String>? extraHeaders,
  }) async {
    await ensureAuthenticated();
    Future<Response<dynamic>> sendOnce() => _dio.requestUri<dynamic>(
      uri,
      data: body,
      options: Options(
        method: method,
        headers: {
          ...?extraHeaders,
          'X-LToken': currentLtoken,
        },
      ),
    );

    var resp = await sendOnce();
    if (resp.statusCode == 401 && isLoggedIn) {
      final rotated = await rotateLtoken();
      if (rotated.ok) {
        resp = await sendOnce();
      }
    }
    return resp;
  }

  String _formatQrError(int code, int status, String fallback) {
    switch (code) {
      case OsmiumErrorCode.qrExpired:
        return '二维码已过期，请在电脑端刷新后重扫';
      case OsmiumErrorCode.qrNotFound:
        return '登录二维码不存在或已失效';
      case OsmiumErrorCode.qrInvalidSignature:
        return '二维码签名校验失败';
      case OsmiumErrorCode.qrStateInvalid:
        return '该二维码状态已变更，请重新扫码';
      case OsmiumErrorCode.qrConfirmCooldown:
        return '请先核对登录信息后再确认';
      case OsmiumErrorCode.qrCancelled:
        return '该登录请求已取消';
      case OsmiumErrorCode.accountLocked:
        return '账号已锁定，无法授权扫码登录';
      case OsmiumErrorCode.rateLimited:
        return '操作过于频繁，请稍后再试';
    }
    if (status == 401 || status == 403) {
      return '登录状态已失效，请重新登录';
    }
    return fallback.isNotEmpty ? fallback : '请求失败 ($status)';
  }

  /// Step 1 (mobile): Register the scanned QR login request on the server with
  /// custom `X-QR-Action: register` header and channel (`in_app` / `external`).
  Future<AccountApiResult<QrMobileRegisterData>> registerMobileQrLogin(
    QrParsedRequest request,
  ) async {
    if (!isLoggedIn) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '请先登录 Osmium 账号',
      );
    }
    try {
      final body = <String, dynamic>{
        'qr_url': request.qrUrl,
        'channel': request.channel,
        if (request.isExternal && request.extTs != null)
          'ext_ts': request.extTs,
        if (request.isExternal && request.extNonce != null)
          'ext_nonce': request.extNonce,
        if (request.isExternal && request.extSig != null)
          'ext_sig': request.extSig,
      };
      final resp = await _sendAuthed(
        'POST',
        '/api/auth/qr/mobile/register',
        body: body,
        extraHeaders: const {'X-QR-Action': 'register'},
      );
      final parsed = _parseEnvelope<QrMobileRegisterData>(
        resp,
        QrMobileRegisterData.fromJson,
      );
      if (!parsed.ok) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          message: _formatQrError(
            parsed.code,
            parsed.httpStatusCode,
            parsed.message,
          ),
        );
      }
      return parsed;
    } catch (e) {
      return AccountApiResult(
        ok: false,
        httpStatusCode: 0,
        message: '网络连接失败: $e',
      );
    }
  }

  /// Step 2 (mobile): Request the QR code URL with the app-verify headers
  /// (`X-Osmium-QR-Mode: app-verify`, `X-QR-Scan-Token`, `X-QR-Scan-Nonce`,
  /// `X-QR-Channel`) to trigger the Geetest human-verification challenge.
  Future<AccountApiResult<Map<String, dynamic>>> requestQrVerifyChallenge(
    QrMobileRegisterData registration,
  ) async {
    if (!isLoggedIn) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '请先登录 Osmium 账号',
      );
    }
    final verifyUri = resolveUri(registration.verifyUrl);
    final baseUri = Uri.parse(OsmiumConstants.defaultApiBaseUrl);
    if (verifyUri.scheme.toLowerCase() != baseUri.scheme.toLowerCase() ||
        verifyUri.host.toLowerCase() != baseUri.host.toLowerCase() ||
        verifyUri.path != '/qr/login') {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 400,
        code: OsmiumErrorCode.qrInvalidSignature,
        message: '验证地址域名异常，已拒绝请求',
      );
    }
    try {
      final headers = <String, String>{
        ...registration.requiredHeaders,
        'X-Osmium-QR-Mode': 'app-verify',
        'X-QR-Scan-Token': registration.scanToken,
        'X-QR-Scan-Nonce': registration.scanNonce,
        'X-QR-Channel': registration.channel,
      };
      final resp = await _sendAuthedUri(
        'GET',
        verifyUri,
        extraHeaders: headers,
      );
      final parsed = _parseEnvelope<Map<String, dynamic>>(resp, (m) => m);
      if (!parsed.ok && !parsed.needsCaptcha) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          message: _formatQrError(
            parsed.code,
            parsed.httpStatusCode,
            parsed.message,
          ),
        );
      }
      return parsed;
    } catch (e) {
      return AccountApiResult(
        ok: false,
        httpStatusCode: 0,
        message: '网络连接失败: $e',
      );
    }
  }

  /// Step 3 (mobile): Confirm or cancel PC login after inspecting PC info and
  /// waiting out the mandatory confirmation cooldown.
  Future<AccountApiResult<Map<String, dynamic>>> confirmMobileQrLogin({
    required String qid,
    required String confirmToken,
    required bool approve,
  }) async {
    if (!isLoggedIn) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '请先登录 Osmium 账号',
      );
    }
    try {
      final resp = await _sendAuthed(
        'POST',
        '/api/auth/qr/mobile/confirm',
        body: {'qid': qid, 'action': approve ? 'confirm' : 'cancel'},
        extraHeaders: {'X-QR-Confirm-Token': confirmToken},
      );
      final parsed = _parseEnvelope<Map<String, dynamic>>(resp, (m) => m);
      if (!parsed.ok) {
        final retryAfterRaw = resp.headers.value('retry-after');
        final retryAfterSec = int.tryParse(retryAfterRaw ?? '');
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          message: _formatQrError(
            parsed.code,
            parsed.httpStatusCode,
            parsed.message,
          ),
          data: retryAfterSec != null
              ? {'retry_after': retryAfterSec}
              : null,
        );
      }
      return parsed;
    } catch (e) {
      return AccountApiResult(
        ok: false,
        httpStatusCode: 0,
        message: '网络连接失败: $e',
      );
    }
  }

  Future<AccountApiResult<List<AccountDeviceDto>>> fetchDevices() async {
    if (!isLoggedIn) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    try {
      final resp = await _sendAuthed('GET', '/api/account/devices');
      final status = resp.statusCode ?? 0;
      final body = resp.data;
      if (status >= 200 && status < 300 && body is Map<String, dynamic>) {
        final rawList = (body['data'] is Map<String, dynamic>)
            ? (body['data']['devices'] as List<dynamic>? ?? const [])
            : (body['devices'] as List<dynamic>? ?? const []);
        final devices = rawList
            .whereType<Map<String, dynamic>>()
            .map(AccountDeviceDto.fromJson)
            .toList();
        return AccountApiResult(
          ok: true,
          httpStatusCode: status,
          data: devices,
        );
      }
      return AccountApiResult(
        ok: false,
        httpStatusCode: status,
        message: '获取设备列表失败',
      );
    } catch (e) {
      return AccountApiResult(ok: false, httpStatusCode: 0, message: '$e');
    }
  }

  Future<AccountApiResult<void>> renameDevice(
    String deviceId,
    String customName,
  ) async {
    try {
      final resp = await _sendAuthed(
        'PUT',
        '/api/account/devices/${DeviceIdentity.normalizeDeviceId(deviceId)}/name',
        body: {'custom_name': customName.trim()},
      );
      final parsed = _parseEnvelope<Map<String, dynamic>>(resp, (m) => m);
      return AccountApiResult(
        ok: parsed.ok,
        httpStatusCode: parsed.httpStatusCode,
        message: parsed.message,
      );
    } catch (e) {
      return AccountApiResult(ok: false, httpStatusCode: 0, message: '$e');
    }
  }

  Future<AccountApiResult<void>> removeDevice(String deviceId) async {
    try {
      final resp = await _sendAuthed(
        'DELETE',
        '/api/account/devices/${DeviceIdentity.normalizeDeviceId(deviceId)}',
      );
      final parsed = _parseEnvelope<Map<String, dynamic>>(resp, (m) => m);
      return AccountApiResult(
        ok: parsed.ok,
        httpStatusCode: parsed.httpStatusCode,
        message: parsed.message,
      );
    } catch (e) {
      return AccountApiResult(ok: false, httpStatusCode: 0, message: '$e');
    }
  }

  Future<void> logout({
    required String deviceId,
    bool revokeAll = false,
  }) async {
    if (currentLtoken.isNotEmpty) {
      try {
        await _sendAuthed(
          'POST',
          '/api/auth/logout',
          body: {'revoke_stoken': revokeAll, 'device_id': deviceId},
        );
      } catch (_) {}
    }
    await clearSession();
  }

  /// Persists the session carried by a successful sign-in envelope. A success
  /// envelope without both tokens is reported as a failure so callers never
  /// close the login flow while still signed out. The live server answers a
  /// captcha step with `ok:true, code:100`, so challenge and pending envelopes
  /// pass through untouched.
  Future<AccountApiResult<T>> _finishSignIn<T>(
    AccountApiResult<T> parsed,
    AccountSessionData? session,
  ) async {
    if (!parsed.ok || parsed.challenge != null || parsed.isPending) {
      return parsed;
    }
    if (session == null || session.stoken.isEmpty || session.ltoken.isEmpty) {
      return AccountApiResult<T>(
        ok: false,
        httpStatusCode: parsed.httpStatusCode,
        code: parsed.code,
        message: '登录凭证缺失，请重试',
      );
    }
    await _setSession(session);
    return parsed;
  }

  Future<void> _setSession(AccountSessionData sess) async {
    _session = sess;
    await _secureStorage.saveAccountSession(sess);
    _sessionController.add(_session);
  }

  Future<void> clearSession() async {
    _session = null;
    await _secureStorage.clearAccountSession();
    _sessionController.add(null);
  }

  void dispose() {
    _sessionController.close();
    _dio.close();
  }
}
