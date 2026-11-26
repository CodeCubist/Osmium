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
              'X-Client-Version': OsmiumConstants.protocolVersion,
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
    int? retryAfterSec = int.tryParse(resp.headers.value('retry-after') ?? '');
    if ((retryAfterSec == null || retryAfterSec <= 0) &&
        rawData is Map<String, dynamic>) {
      final rawRetry = rawData['retry_after'];
      if (rawRetry is num && rawRetry > 0) {
        retryAfterSec = rawRetry.ceil();
      }
    }
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
      retryAfterSec: retryAfterSec,
    );
  }

  String _formatWaitDuration(int? seconds) {
    if (seconds == null || seconds <= 0) return '';
    final hours = seconds ~/ 3600;
    final mins = (seconds % 3600) ~/ 60;
    final secs = seconds % 60;
    if (hours > 0 && mins > 0) return '$hours小时$mins分钟';
    if (hours > 0) return '$hours小时';
    if (mins > 0) return '$mins分钟';
    return '$secs秒';
  }

  String _formatAccountError(
    int code,
    int status,
    String fallback, {
    int? retryAfterSec,
  }) {
    final wait = _formatWaitDuration(retryAfterSec);
    switch (code) {
      case OsmiumErrorCode.usernameChangeCooldown:
        return wait.isNotEmpty
            ? '名称每1小时最多修改一次，请在$wait后重试'
            : '名称每1小时最多修改一次，请稍后再试';
      case OsmiumErrorCode.avatarChangeCooldown:
        return wait.isNotEmpty
            ? '头像每1小时最多修改一次，请在$wait后重试'
            : '头像每1小时最多修改一次，请稍后再试';
      case OsmiumErrorCode.emailChangeCooldown:
        return wait.isNotEmpty
            ? '邮箱每24小时最多更换一次，请在$wait后重试'
            : '邮箱每24小时最多更换一次，请稍后再试';
      case OsmiumErrorCode.passwordResetCooldown:
        return wait.isNotEmpty
            ? '密码每24小时最多重置一次，请在$wait后重试'
            : '密码每24小时最多重置一次，请稍后再试';
      case OsmiumErrorCode.invalidEmail:
        return '请输入有效的电子邮箱地址';
      case OsmiumErrorCode.invalidPassword:
        return '密码长度需在8至128位之间';
      case OsmiumErrorCode.invalidUsername:
        return '请输入有效的用户名称';
      case OsmiumErrorCode.invalidAvatar:
      case OsmiumErrorCode.payloadTooLarge:
        return '头像图片无效或超出大小限制';
      case OsmiumErrorCode.invalidCredentials:
        return '邮箱或密码错误';
      case OsmiumErrorCode.emailAlreadyRegistered:
        return '该电子邮箱已被注册';
      case OsmiumErrorCode.accountNotFound:
        return '未找到该邮箱对应的账户';
      case OsmiumErrorCode.accountLocked:
        return '账号已锁定，请先解锁';
      case OsmiumErrorCode.otpExpired:
        return '验证码已过期，请重新获取';
      case OsmiumErrorCode.otpInvalid:
      case OsmiumErrorCode.invalidOtpFormat:
        return '验证码不正确';
      case OsmiumErrorCode.otpTooManyAttempts:
        return '验证码错误次数过多，请重新获取';
      case OsmiumErrorCode.otpCooldown:
        return '验证码发送过于频繁，请稍后再试';
      case OsmiumErrorCode.rateLimited:
        return '请求过于频繁，请稍后再试';
      case OsmiumErrorCode.serviceQuotaExceeded:
        return '服务暂不可用，请稍后重试';
      case OsmiumErrorCode.accountProfileIncomplete:
        return '请先绑定邮箱并设置登录密码后再进行此操作';
      case OsmiumErrorCode.accountEmailRequired:
        return '请先绑定邮箱';
      case OsmiumErrorCode.passwordAlreadySet:
        return '当前账号已设置登录密码';
      case OsmiumErrorCode.oauthAlreadyBound:
        return '该账号已绑定其他Osmium账号';
      case OsmiumErrorCode.oauthProviderAlreadyBound:
        return '当前账号已绑定该平台';
      case OsmiumErrorCode.oauthProviderInvalid:
        return '不支持该登录方式';
      case OsmiumErrorCode.oauthExpired:
        return '登录授权已超时，请重试';
      case OsmiumErrorCode.oauthFailed:
        return '登录授权未完成，请重试';
      case OsmiumErrorCode.oauthNotConfigured:
        return '暂不支持该登录方式，请稍后再试或使用邮箱登录';
      case OsmiumErrorCode.twoFaInvalid:
        return '动态验证码不正确，请重试';
      case OsmiumErrorCode.twoFaExpired:
        return '两步验证已超时，请重新登录';
      case OsmiumErrorCode.loginKeyInvalid:
        return '登录密钥无效或已移除';
      case OsmiumErrorCode.loginKeyLimitExceeded:
        return '登录密钥数量已达上限（最多10个）';
      case OsmiumErrorCode.versionUnsupported:
        return '当前客户端版本已停用，请更新到最新版本';
      case OsmiumErrorCode.deviceIdConflict:
        return '设备 ID 发生冲突，请重试';
      case OsmiumErrorCode.forbidden:
      case OsmiumErrorCode.accountAuthRequired:
        return '登录状态已失效，请重新登录';
    }
    if (status == 426) return '当前客户端版本已停用，请更新到最新版本';
    if (status == 423) return '账号已锁定，请先解锁';
    if (status == 429) return '请求过于频繁，请稍后再试';
    return fallback;
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
          'device_secret': identity.deviceSecret,
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
      if (!parsed.ok && !parsed.isPending) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          retryAfterSec: parsed.retryAfterSec,
          message: _formatAccountError(
            parsed.code,
            parsed.httpStatusCode,
            parsed.message.isNotEmpty ? parsed.message : '验证失败',
            retryAfterSec: parsed.retryAfterSec,
          ),
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
      final parsed = _parseEnvelope<Map<String, dynamic>>(resp, (m) => m);
      if (!parsed.ok && !parsed.needsCaptcha) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          retryAfterSec: parsed.retryAfterSec,
          message: _formatAccountError(
            parsed.code,
            parsed.httpStatusCode,
            parsed.message.isNotEmpty ? parsed.message : '发送失败',
            retryAfterSec: parsed.retryAfterSec,
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
          'device_secret': identity.deviceSecret,
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

  Future<AccountApiResult<OAuthProvidersData>> fetchOAuthProviders() async {
    try {
      final resp = await _dio.get<dynamic>(
        '/api/auth/oauth/providers',
        queryParameters: const {'platform': 'android'},
      );
      final parsed = _parseEnvelope<OAuthProvidersData>(
        resp,
        OAuthProvidersData.fromJson,
      );
      if (!parsed.ok || parsed.data == null) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          message: '无法连接到账号服务，请检查网络后重试',
        );
      }
      return parsed;
    } catch (e) {
      return AccountApiResult(
        ok: false,
        httpStatusCode: 0,
        message: '无法连接到账号服务，请检查网络后重试',
      );
    }
  }

  Future<AccountApiResult<OAuthStartData>> startOAuthLogin({
    required String provider,
    required DeviceIdentity identity,
  }) async {
    try {
      final resp = await _dio.post<dynamic>(
        '/api/auth/oauth/start',
        data: {
          'provider': provider.trim().toLowerCase(),
          'platform': 'android',
          'device_id': identity.deviceId,
          'device_name': identity.deviceName,
          'os_version': Platform.isAndroid
              ? 'Android'
              : Platform.operatingSystem,
          'device_secret': identity.deviceSecret,
        },
      );
      final parsed = _parseEnvelope<OAuthStartData>(
        resp,
        OAuthStartData.fromJson,
      );
      if (!parsed.ok) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          message: _formatAccountError(
            parsed.code,
            parsed.httpStatusCode,
            parsed.message.isNotEmpty ? parsed.message : '发起登录失败，请重试',
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

  Future<AccountApiResult<OAuthStatusData>> pollOAuthStatus({
    required String pollUrl,
    required String pollSecret,
  }) async {
    try {
      final resp = await _dio.getUri<dynamic>(
        resolveUri(pollUrl),
        options: Options(headers: {'X-OAuth-Secret': pollSecret}),
      );
      final parsed = _parseEnvelope<OAuthStatusData>(
        resp,
        OAuthStatusData.fromJson,
      );
      if (parsed.ok &&
          parsed.data != null &&
          parsed.data!.state == 'completed' &&
          parsed.data!.intent != 'bind' &&
          parsed.data!.session != null) {
        final sess = parsed.data!.session!;
        if (sess.stoken.isNotEmpty && sess.ltoken.isNotEmpty) {
          await _setSession(sess);
        }
      } else if (!parsed.ok) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          message: _formatAccountError(
            parsed.code,
            parsed.httpStatusCode,
            parsed.message.isNotEmpty ? parsed.message : '授权未完成，请重试',
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

  Future<void> cancelOAuthLogin({
    required String state,
    required String pollSecret,
  }) async {
    try {
      await _dio.post<dynamic>(
        '/api/auth/oauth/cancel',
        data: {'state': state},
        options: Options(headers: {'X-OAuth-Secret': pollSecret}),
      );
    } catch (_) {}
  }

  Future<AccountApiResult<OAuthBindingsData>> fetchOAuthBindings() async {
    if (!isLoggedIn) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    try {
      final resp = await _sendAuthed(
        'GET',
        '/api/account/oauth/bindings?platform=android',
      );
      final parsed = _parseEnvelope<OAuthBindingsData>(
        resp,
        OAuthBindingsData.fromJson,
      );
      if (!parsed.ok || parsed.data == null) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          message: _formatAccountError(
            parsed.code,
            parsed.httpStatusCode,
            parsed.message.isNotEmpty ? parsed.message : '获取第三方账号绑定信息失败',
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

  Future<AccountApiResult<OAuthStartData>> startOAuthBind({
    required String provider,
  }) async {
    if (!isLoggedIn) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    try {
      final resp = await _sendAuthed(
        'POST',
        '/api/account/oauth/bind/start',
        body: {
          'provider': provider.trim().toLowerCase(),
          'platform': 'android',
        },
      );
      final parsed = _parseEnvelope<OAuthStartData>(
        resp,
        OAuthStartData.fromJson,
      );
      if (!parsed.ok) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          message: _formatAccountError(
            parsed.code,
            parsed.httpStatusCode,
            parsed.message.isNotEmpty ? parsed.message : '发起绑定失败，请重试',
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

  Future<AccountApiResult<Map<String, dynamic>>> unbindOAuthProvider({
    required String provider,
  }) async {
    if (!isLoggedIn) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    try {
      final resp = await _sendAuthed(
        'POST',
        '/api/account/oauth/unbind',
        body: {'provider': provider.trim().toLowerCase()},
      );
      final parsed = _parseEnvelope<Map<String, dynamic>>(resp, (m) => m);
      if (!parsed.ok) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          message: _formatAccountError(
            parsed.code,
            parsed.httpStatusCode,
            parsed.message.isNotEmpty ? parsed.message : '解除绑定失败，请重试',
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
          final hasAvatarKey = d.containsKey('avatar_data');
          final rawAvatar = d['avatar_data'] as String?;
          final trimmedAvatar = rawAvatar?.trim();
          final nextHasEmail = d.containsKey('has_email')
              ? d['has_email'] == true
              : cur.hasEmail;
          final nextHasPassword = d.containsKey('has_password')
              ? d['has_password'] == true
              : cur.hasPassword;
          final updated = cur.copyWith(
            userId: (d['user_id'] ?? cur.userId).toString(),
            email: nextHasEmail ? (d['email'] ?? cur.email).toString() : '',
            username: (d['username'] ?? cur.username).toString(),
            avatarData:
                (hasAvatarKey && trimmedAvatar != null && trimmedAvatar.isNotEmpty)
                    ? trimmedAvatar
                    : null,
            clearAvatar:
                hasAvatarKey && (trimmedAvatar == null || trimmedAvatar.isEmpty),
            ltoken: ltok,
            ltokenExpiresAtUnix: exp > 0 ? now + exp : 0,
            accountStatus: (d['account_status'] ?? cur.accountStatus)
                .toString(),
            locked: d['locked'] == true,
            hasEmail: nextHasEmail,
            hasPassword: nextHasPassword,
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
    return fallback.isNotEmpty ? fallback : '请求失败($status)';
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
        message: '请先登录Osmium账号',
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
        message: '请先登录Osmium账号',
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
        message: '请先登录Osmium账号',
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

  /// Fetches the latest account profile (including `avatar_data`, `username`,
  /// `email`, and lock state) from `/api/account/status`, matching desktop
  /// `AccountService.GetAccountStatusAsync`.
  Future<AccountApiResult<AccountSessionData>> fetchAccountStatus() async {
    final cur = _session;
    if (!isLoggedIn || cur == null) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    try {
      final resp = await _sendAuthed('GET', '/api/account/status');
      final parsed = _parseEnvelope<Map<String, dynamic>>(resp, (m) => m);
      if (parsed.ok && parsed.data != null) {
        final d = parsed.data!;
        final hasAvatarKey = d.containsKey('avatar_data');
        final rawAvatar = d['avatar_data'] as String?;
        final trimmedAvatar = rawAvatar?.trim();
        final status = (d['account_status'] ?? cur.accountStatus).toString();
        final locked = d['locked'] == true || status.toLowerCase() == 'locked';
        final nextHasEmail = d.containsKey('has_email')
            ? d['has_email'] == true
            : cur.hasEmail;
        final nextHasPassword = d.containsKey('has_password')
            ? d['has_password'] == true
            : cur.hasPassword;
        final updated = cur.copyWith(
          userId: (d['user_id'] ?? cur.userId).toString(),
          email: nextHasEmail ? (d['email'] ?? cur.email).toString() : '',
          username: (d['username'] ?? cur.username).toString(),
          avatarData:
              (hasAvatarKey && trimmedAvatar != null && trimmedAvatar.isNotEmpty)
                  ? trimmedAvatar
                  : null,
          clearAvatar:
              hasAvatarKey && (trimmedAvatar == null || trimmedAvatar.isEmpty),
          accountStatus: status,
          locked: locked,
          hasEmail: nextHasEmail,
          hasPassword: nextHasPassword,
          twoFaEnabled: d.containsKey('two_fa_enabled')
              ? d['two_fa_enabled'] == true
              : cur.twoFaEnabled,
        );
        await _setSession(updated);
        return AccountApiResult(
          ok: true,
          httpStatusCode: parsed.httpStatusCode,
          data: updated,
        );
      }
      return AccountApiResult(
        ok: false,
        httpStatusCode: parsed.httpStatusCode,
        code: parsed.code,
        message: parsed.message.isNotEmpty ? parsed.message : '获取账号信息失败',
      );
    } catch (e) {
      return AccountApiResult(
        ok: false,
        httpStatusCode: 0,
        message: '网络连接失败: $e',
      );
    }
  }

  /// Updates account nickname and/or avatar via `POST /api/account/profile`,
  /// matching desktop `AccountService.UpdateProfileAsync`. Pass
  /// `clearAvatar: true` to remove the current avatar (`avatar_data: ""`).
  Future<AccountApiResult<AccountSessionData>> updateProfile({
    String? username,
    String? avatarData,
    bool clearAvatar = false,
  }) async {
    final cur = _session;
    if (!isLoggedIn || cur == null) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    final payload = <String, dynamic>{};
    if (username != null) {
      payload['username'] = username.trim();
    }
    if (clearAvatar) {
      payload['avatar_data'] = '';
    } else if (avatarData != null) {
      payload['avatar_data'] = avatarData;
    }
    try {
      final resp = await _sendAuthed(
        'POST',
        '/api/account/profile',
        body: payload,
      );
      final parsed = _parseEnvelope<Map<String, dynamic>>(resp, (m) => m);
      if (parsed.ok) {
        final d = parsed.data ?? const <String, dynamic>{};
        final nextUsername = (d['username'] ?? username ?? cur.username)
            .toString()
            .trim();
        final hasAvatarInResp = d.containsKey('avatar_data');
        final respAvatar = (d['avatar_data'] as String?)?.trim();
        final bool shouldClear = clearAvatar ||
            (hasAvatarInResp && (respAvatar == null || respAvatar.isEmpty));
        final String? nextAvatar = shouldClear
            ? null
            : (hasAvatarInResp
                ? respAvatar
                : (avatarData ?? cur.avatarData));
        final updated = cur.copyWith(
          username: nextUsername.isNotEmpty ? nextUsername : cur.username,
          avatarData: nextAvatar,
          clearAvatar: shouldClear,
        );
        await _setSession(updated);
        return AccountApiResult(
          ok: true,
          httpStatusCode: parsed.httpStatusCode,
          data: updated,
        );
      }
      return AccountApiResult(
        ok: false,
        httpStatusCode: parsed.httpStatusCode,
        code: parsed.code,
        retryAfterSec: parsed.retryAfterSec,
        message: _formatAccountError(
          parsed.code,
          parsed.httpStatusCode,
          parsed.message.isNotEmpty ? parsed.message : '更新资料失败',
          retryAfterSec: parsed.retryAfterSec,
        ),
      );
    } catch (e) {
      return AccountApiResult(
        ok: false,
        httpStatusCode: 0,
        message: '网络连接失败: $e',
      );
    }
  }

  Future<AccountApiResult<Map<String, dynamic>>> startChangeEmailSendCode({
    String? password,
    required String newEmail,
  }) async {
    if (!isLoggedIn) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    try {
      final body = <String, dynamic>{
        'new_email': newEmail.trim(),
        if (password != null && password.isNotEmpty) 'password': password,
      };
      final resp = await _sendAuthed(
        'POST',
        '/api/account/email/send-code',
        body: body,
      );
      final parsed = _parseEnvelope<Map<String, dynamic>>(resp, (m) => m);
      if (!parsed.ok && !parsed.needsCaptcha) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          retryAfterSec: parsed.retryAfterSec,
          message: _formatAccountError(
            parsed.code,
            parsed.httpStatusCode,
            parsed.message.isNotEmpty ? parsed.message : '发送失败',
            retryAfterSec: parsed.retryAfterSec,
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

  Future<AccountApiResult<AccountSessionData>> confirmChangeEmail({
    String? password,
    required String newEmail,
    required String code,
  }) async {
    final cur = _session;
    if (!isLoggedIn || cur == null) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    try {
      final body = <String, dynamic>{
        'new_email': newEmail.trim(),
        'code': code.trim(),
        if (password != null && password.isNotEmpty) 'password': password,
      };
      final resp = await _sendAuthed(
        'POST',
        '/api/account/email/confirm',
        body: body,
      );
      final parsed = _parseEnvelope<Map<String, dynamic>>(resp, (m) => m);
      if (parsed.ok) {
        final d = parsed.data ?? const <String, dynamic>{};
        final updatedEmail = (d['email'] ?? newEmail).toString().trim();
        final updated = cur.copyWith(
          email: updatedEmail.isNotEmpty ? updatedEmail : cur.email,
          hasEmail: d.containsKey('has_email') ? d['has_email'] == true : true,
          hasPassword: d.containsKey('has_password')
              ? d['has_password'] == true
              : cur.hasPassword,
        );
        await _setSession(updated);
        return AccountApiResult(
          ok: true,
          httpStatusCode: parsed.httpStatusCode,
          data: updated,
        );
      }
      return AccountApiResult(
        ok: false,
        httpStatusCode: parsed.httpStatusCode,
        code: parsed.code,
        retryAfterSec: parsed.retryAfterSec,
        message: _formatAccountError(
          parsed.code,
          parsed.httpStatusCode,
          parsed.message.isNotEmpty ? parsed.message : '更新邮箱失败',
          retryAfterSec: parsed.retryAfterSec,
        ),
      );
    } catch (e) {
      return AccountApiResult(
        ok: false,
        httpStatusCode: 0,
        message: '网络连接失败: $e',
      );
    }
  }

  Future<AccountApiResult<Map<String, dynamic>>> startSetPasswordSendCode() async {
    if (!isLoggedIn) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    try {
      final resp = await _sendAuthed(
        'POST',
        '/api/account/password/send-code',
        body: const <String, dynamic>{},
      );
      final parsed = _parseEnvelope<Map<String, dynamic>>(resp, (m) => m);
      if (!parsed.ok && !parsed.needsCaptcha) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          retryAfterSec: parsed.retryAfterSec,
          message: _formatAccountError(
            parsed.code,
            parsed.httpStatusCode,
            parsed.message.isNotEmpty ? parsed.message : '发送失败',
            retryAfterSec: parsed.retryAfterSec,
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

  Future<AccountApiResult<AccountSessionData>> confirmSetPassword({
    required String code,
    required String newPassword,
  }) async {
    final cur = _session;
    if (!isLoggedIn || cur == null) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    try {
      final resp = await _sendAuthed(
        'POST',
        '/api/account/password/confirm',
        body: {
          'code': code.trim(),
          'new_password': newPassword,
        },
      );
      final parsed = _parseEnvelope<Map<String, dynamic>>(resp, (m) => m);
      if (parsed.ok) {
        final d = parsed.data ?? const <String, dynamic>{};
        final updated = cur.copyWith(
          hasEmail: d.containsKey('has_email') ? d['has_email'] == true : true,
          hasPassword: d.containsKey('has_password')
              ? d['has_password'] == true
              : true,
        );
        await _setSession(updated);
        return AccountApiResult(
          ok: true,
          httpStatusCode: parsed.httpStatusCode,
          data: updated,
        );
      }
      return AccountApiResult(
        ok: false,
        httpStatusCode: parsed.httpStatusCode,
        code: parsed.code,
        retryAfterSec: parsed.retryAfterSec,
        message: _formatAccountError(
          parsed.code,
          parsed.httpStatusCode,
          parsed.message.isNotEmpty ? parsed.message : '设置密码失败',
          retryAfterSec: parsed.retryAfterSec,
        ),
      );
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

  Future<AccountApiResult<List<QuickLaunchAppDto>>> fetchQuickLaunchApps() async {
    if (!isLoggedIn) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    try {
      final resp = await _sendAuthed('GET', '/api/account/quick-launch/apps');
      final status = resp.statusCode ?? 0;
      final body = resp.data;
      if (status >= 200 && status < 300 && body is Map<String, dynamic>) {
        final rawList = (body['data'] is Map<String, dynamic>)
            ? (body['data']['apps'] as List<dynamic>? ?? const [])
            : (body['apps'] as List<dynamic>? ?? const []);
        final apps = rawList
            .whereType<Map<String, dynamic>>()
            .map(QuickLaunchAppDto.fromJson)
            .toList();
        return AccountApiResult(
          ok: true,
          httpStatusCode: status,
          data: apps,
        );
      }
      return AccountApiResult(
        ok: false,
        httpStatusCode: status,
        message: '获取快速启动列表失败',
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

  String _formatPowerError(int code, int status, String fallback) {
    switch (code) {
      case OsmiumErrorCode.powerDisabled:
        return '目标电脑未开启远程开关机功能';
      case OsmiumErrorCode.powerUnsupported:
        return '目标设备暂不支持远程开关机，或尚未获取到网卡MAC地址';
      case OsmiumErrorCode.powerCooldown:
        return '电源操作过于频繁，请稍候几秒后再试';
      case OsmiumErrorCode.deviceOffline:
        return '目标电脑当前离线，无法执行关机或重启';
      case OsmiumErrorCode.selfConnect:
        return '无法对当前设备执行远程电源操作';
      case OsmiumErrorCode.accountLocked:
        return '账号已锁定，无法执行远程电源操作';
      case OsmiumErrorCode.accountProfileIncomplete:
        return '为了保障设备安全，请先在「我的」中绑定邮箱并设置登录密码，完成后即可使用远程开关机功能';
      case OsmiumErrorCode.forbidden:
      case OsmiumErrorCode.accountAuthRequired:
        return '仅限登录同一账户的设备可操作目标计算机';
      case OsmiumErrorCode.rateLimited:
        return '操作过于频繁，请稍后再试';
    }
    if (status == 401 || status == 403) {
      return '登录状态无效或无权操作该设备';
    }
    return fallback.isNotEmpty ? fallback : '远程电源操作失败($status)';
  }

  Future<AccountApiResult<DevicePowerResultData>> sendDevicePowerCommand({
    required String deviceId,
    required String action,
  }) async {
    if (!isLoggedIn) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        code: OsmiumErrorCode.accountAuthRequired,
        message: '请先登录同一账户后再执行远程电源操作',
      );
    }
    try {
      final resp = await _sendAuthed(
        'POST',
        '/api/account/devices/power',
        body: {
          'device_id': DeviceIdentity.normalizeDeviceId(deviceId),
          'action': action.trim().toLowerCase(),
        },
      );
      final parsed = _parseEnvelope<DevicePowerResultData>(
        resp,
        DevicePowerResultData.fromJson,
      );
      if (!parsed.ok) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          message: _formatPowerError(
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

  Future<AccountApiResult<AccountSessionData>> verifyTwoFaLogin({
    required String twoFaToken,
    required String code,
  }) async {
    try {
      final resp = await _dio.post<dynamic>(
        '/api/auth/login/2fa/verify',
        data: {
          'two_fa_token': twoFaToken.trim(),
          'code': code.trim(),
        },
      );
      final parsed = _parseEnvelope<AccountSessionData>(
        resp,
        AccountSessionData.fromLoginPayload,
      );
      if (parsed.needsCaptcha) return parsed;
      if (!parsed.ok) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          retryAfterSec: parsed.retryAfterSec,
          message: _formatAccountError(
            parsed.code,
            parsed.httpStatusCode,
            parsed.message.isNotEmpty ? parsed.message : '验证失败',
            retryAfterSec: parsed.retryAfterSec,
          ),
        );
      }
      return await _finishSignIn(parsed, parsed.data);
    } catch (e) {
      return AccountApiResult(
        ok: false,
        httpStatusCode: 0,
        message: '网络连接失败: $e',
      );
    }
  }

  Future<AccountApiResult<AccountSessionData>> loginWithKey({
    required String loginKey,
    required DeviceIdentity identity,
  }) async {
    try {
      final resp = await _dio.post<dynamic>(
        '/api/auth/login/key',
        data: {
          'login_key': loginKey.trim(),
          'device_id': identity.deviceId,
          'device_name': identity.deviceName,
          'os_version': Platform.isAndroid
              ? 'Android'
              : Platform.operatingSystem,
          'device_secret': identity.deviceSecret,
        },
      );
      final parsed = _parseEnvelope<AccountSessionData>(
        resp,
        AccountSessionData.fromLoginPayload,
      );
      if (parsed.needsCaptcha) return parsed;
      if (!parsed.ok) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          retryAfterSec: parsed.retryAfterSec,
          message: _formatAccountError(
            parsed.code,
            parsed.httpStatusCode,
            parsed.message.isNotEmpty ? parsed.message : '登录失败',
            retryAfterSec: parsed.retryAfterSec,
          ),
        );
      }
      return await _finishSignIn(parsed, parsed.data);
    } catch (e) {
      return AccountApiResult(
        ok: false,
        httpStatusCode: 0,
        message: '网络连接失败: $e',
      );
    }
  }

  Future<AccountApiResult<LoginKeysListData>> fetchLoginKeys() async {
    if (!isLoggedIn) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    try {
      final resp = await _sendAuthed('GET', '/api/account/login-keys');
      final parsed = _parseEnvelope<LoginKeysListData>(
        resp,
        LoginKeysListData.fromJson,
      );
      if (!parsed.ok) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          message: _formatAccountError(
            parsed.code,
            parsed.httpStatusCode,
            parsed.message.isNotEmpty ? parsed.message : '获取登录密钥失败',
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

  Future<AccountApiResult<Map<String, dynamic>>>
      startCreateLoginKeySendCode() async {
    if (!isLoggedIn) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    try {
      final resp = await _sendAuthed(
        'POST',
        '/api/account/login-keys/send-code',
        body: const <String, dynamic>{},
      );
      final parsed = _parseEnvelope<Map<String, dynamic>>(resp, (m) => m);
      if (parsed.needsCaptcha || parsed.ok) return parsed;
      return AccountApiResult(
        ok: false,
        httpStatusCode: parsed.httpStatusCode,
        code: parsed.code,
        retryAfterSec: parsed.retryAfterSec,
        message: _formatAccountError(
          parsed.code,
          parsed.httpStatusCode,
          parsed.message.isNotEmpty ? parsed.message : '发送验证码失败',
          retryAfterSec: parsed.retryAfterSec,
        ),
      );
    } catch (e) {
      return AccountApiResult(
        ok: false,
        httpStatusCode: 0,
        message: '网络连接失败: $e',
      );
    }
  }

  Future<AccountApiResult<LoginKeyCreateData>> createLoginKey({
    required String code,
    required String name,
  }) async {
    if (!isLoggedIn) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    try {
      final resp = await _sendAuthed(
        'POST',
        '/api/account/login-keys/create',
        body: {
          'code': code.trim(),
          'name': name.trim(),
        },
      );
      final parsed = _parseEnvelope<LoginKeyCreateData>(
        resp,
        LoginKeyCreateData.fromJson,
      );
      if (!parsed.ok) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          message: _formatAccountError(
            parsed.code,
            parsed.httpStatusCode,
            parsed.message.isNotEmpty ? parsed.message : '创建登录密钥失败',
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

  Future<AccountApiResult<Map<String, dynamic>>> deleteLoginKey(
    String id,
  ) async {
    if (!isLoggedIn) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    try {
      final resp = await _sendAuthed(
        'POST',
        '/api/account/login-keys/delete',
        body: {'id': id.trim()},
      );
      final parsed = _parseEnvelope<Map<String, dynamic>>(resp, (m) => m);
      if (!parsed.ok) {
        return AccountApiResult(
          ok: false,
          httpStatusCode: parsed.httpStatusCode,
          code: parsed.code,
          message: _formatAccountError(
            parsed.code,
            parsed.httpStatusCode,
            parsed.message.isNotEmpty ? parsed.message : '移除登录密钥失败',
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
