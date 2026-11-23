import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:osmium_mobile/core/constants/osmium_constants.dart';
import 'package:osmium_mobile/core/models/account_models.dart';
import 'package:osmium_mobile/core/models/device_identity.dart';
import 'package:osmium_mobile/services/account/account_service.dart';
import 'package:osmium_mobile/services/signaling/signaling_client.dart';
import 'package:osmium_mobile/services/storage/secure_storage_service.dart';
import 'package:osmium_mobile/services/storage/settings_repository.dart';
import 'package:osmium_mobile/state/app_controller.dart';

const _identity = DeviceIdentity(
  deviceId: '123456789',
  deviceName: 'Pixel Test',
);

const _challenge = AccountChallengeInfo(
  challengeId: 'cid',
  challengeSecret: 'secret',
  captchaUrl: '/api/captcha/cid',
  pollUrl: '/api/challenge/cid',
  expiresIn: 300,
);

const _sessionPayload = <String, dynamic>{
  'user_id': 'u1',
  'email': 'user@example.com',
  'username': 'Osmium-Tester',
  'stoken': 'st-new',
  'stoken_expires_in': 3600,
  'ltoken': 'lt-new',
  'ltoken_expires_in': 3600,
  'account_status': 'active',
};

class _FakeSecureStorage extends SecureStorageService {
  AccountSessionData? stored;

  @override
  Future<AccountSessionData?> loadAccountSession() async => stored;

  @override
  Future<void> saveAccountSession(AccountSessionData session) async {
    stored = session;
  }

  @override
  Future<void> clearAccountSession() async {
    stored = null;
  }
}

class _MockHttpAdapter implements HttpClientAdapter {
  _MockHttpAdapter(this.handler);

  final FutureOr<ResponseBody> Function(RequestOptions options, String? body)
  handler;
  final List<RequestOptions> requests = [];
  final List<String?> requestBodies = [];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    String? body;
    if (requestStream != null) {
      final bytes = <int>[];
      await for (final chunk in requestStream) {
        bytes.addAll(chunk);
      }
      body = utf8.decode(bytes);
    }
    requests.add(options);
    requestBodies.add(body);
    return handler(options, body);
  }

  @override
  void close({bool force = false}) {}
}

ResponseBody _json(Object body, {int status = 200}) {
  return ResponseBody.fromString(
    jsonEncode(body),
    status,
    headers: {
      Headers.contentTypeHeader: [Headers.jsonContentType],
    },
  );
}

typedef _Harness = ({
  AccountService service,
  _MockHttpAdapter adapter,
  _FakeSecureStorage storage,
});

_Harness _build(
  FutureOr<ResponseBody> Function(RequestOptions options, String? body) handler,
) {
  final adapter = _MockHttpAdapter(handler);
  final dio = Dio(
    BaseOptions(
      baseUrl: OsmiumConstants.defaultApiBaseUrl,
      validateStatus: (_) => true,
    ),
  )..httpClientAdapter = adapter;
  final storage = _FakeSecureStorage();
  return (
    service: AccountService(storage, dio: dio),
    adapter: adapter,
    storage: storage,
  );
}

void main() {
  group('account constants', () {
    test('challenge pending is code 101', () {
      expect(OsmiumErrorCode.challengePending, 101);
      expect(OsmiumErrorCode.accountCaptchaRequired, 100);
    });
  });

  group('Login', () {
    test(
      'live server shape ok:true + code 100 + HTTP 202 yields the captcha',
      () async {
        final harness = _build(
          (options, body) => _json({
            'ok': true,
            'code': 100,
            'data': {
              'challenge_id': 'cid',
              'challenge_secret': 'secret',
              'captcha_url': 'https://captcha.example/captcha/cid',
              'poll_url': 'https://captcha.example/api/auth/challenge/status?challenge_id=cid',
              'expires_in': 300,
            },
          }, status: 202),
        );

        final res = await harness.service.startLogin(
          email: 'user@example.com',
          password: 'password123',
          identity: _identity,
        );

        expect(res.needsCaptcha, isTrue);
        expect(res.challenge!.challengeId, 'cid');
        expect(res.message, isNot('登录凭证缺失，请重试'));
        expect(harness.service.isLoggedIn, isFalse);
        expect(harness.storage.stored, isNull);
      },
    );

    test(
      'code 100 + HTTP 202 yields a captcha challenge, not a session',
      () async {
        final harness = _build(
          (options, body) => _json({
            'ok': false,
            'code': 100,
            'data': {
              'challenge_id': 'cid',
              'challenge_secret': 'secret',
              'captcha_url': '/api/captcha/cid',
              'poll_url': '/api/challenge/cid',
              'expires_in': 300,
            },
          }, status: 202),
        );

        final res = await harness.service.startLogin(
          email: ' user@example.com ',
          password: 'password123',
          identity: _identity,
        );

        expect(res.ok, isFalse);
        expect(res.needsCaptcha, isTrue);
        expect(res.challenge!.challengeId, 'cid');
        expect(harness.service.isLoggedIn, isFalse);
        expect(harness.storage.stored, isNull);

        final sent = jsonDecode(
          harness.adapter.requestBodies.single!,
        ) as Map<String, dynamic>;
        expect(harness.adapter.requests.single.uri.path, '/api/auth/login');
        expect(sent['email'], 'user@example.com');
        expect(sent['device_id'], _identity.deviceId);
        expect(sent['device_name'], _identity.deviceName);
      },
    );

    test(
      'HTTP 202 without code 100 is not treated as a captcha challenge',
      () async {
        final harness = _build(
          (options, body) => _json({
            'ok': false,
            'code': 0,
            'data': {
              'challenge_id': 'cid',
              'challenge_secret': 'secret',
              'captcha_url': '/api/captcha/cid',
              'poll_url': '/api/challenge/cid',
            },
          }, status: 202),
        );

        final res = await harness.service.startLogin(
          email: 'user@example.com',
          password: 'password123',
          identity: _identity,
        );

        expect(res.ok, isFalse);
        expect(res.needsCaptcha, isFalse);
        expect(res.challenge, isNull);
      },
    );

    test('successful login applies typed session tokens', () async {
      final harness = _build(
        (options, body) =>
            _json({'ok': true, 'code': 0, 'data': _sessionPayload}),
      );

      final res = await harness.service.startLogin(
        email: 'user@example.com',
        password: 'password123',
        identity: _identity,
      );

      expect(res.ok, isTrue);
      expect(res.needsCaptcha, isFalse);
      expect(res.data!.userId, 'u1');
      expect(harness.service.isLoggedIn, isTrue);
      expect(harness.service.session!.stoken, 'st-new');
      expect(harness.service.currentLtoken, 'lt-new');
      expect(harness.storage.stored!.userId, 'u1');
    });

    test('ok:false with HTTP 200 is a terminal error, not success', () async {
      final harness = _build(
        (options, body) => _json({
          'ok': false,
          'code': 2002,
          'message': 'Incorrect email or password',
        }),
      );

      final res = await harness.service.startLogin(
        email: 'user@example.com',
        password: 'wrong-password',
        identity: _identity,
      );

      expect(res.ok, isFalse);
      expect(res.httpStatusCode, 200);
      expect(res.message, 'Incorrect email or password');
      expect(harness.service.isLoggedIn, isFalse);
      expect(harness.storage.stored, isNull);
    });
  });

  group('Challenge polling', () {
    test('code 101 is pending regardless of HTTP status', () async {
      final harness = _build(
        (options, body) =>
            _json({'ok': false, 'code': 101, 'message': 'pending'}),
      );

      final res = await harness.service.pollChallenge(challenge: _challenge);

      expect(res.isPending, isTrue);
      expect(res.ok, isFalse);
      expect(res.httpStatusCode, 202);
      expect(harness.service.isLoggedIn, isFalse);
      expect(
        harness.adapter.requests.single.headers['X-Challenge-Secret'],
        'secret',
      );
    });

    test('status text pending also keeps polling', () async {
      final harness = _build(
        (options, body) => _json({'ok': false, 'code': 0, 'status': 'pending'}),
      );

      final res = await harness.service.pollChallenge(challenge: _challenge);

      expect(res.isPending, isTrue);
      expect(res.ok, isFalse);
    });

    test('completed poll applies typed session and returns payload', () async {
      final harness = _build(
        (options, body) =>
            _json({'ok': true, 'code': 0, 'data': _sessionPayload}),
      );

      final res = await harness.service.pollChallenge(challenge: _challenge);

      expect(res.ok, isTrue);
      expect(res.isPending, isFalse);
      expect(res.data!['stoken'], 'st-new');
      expect(harness.service.session!.ltoken, 'lt-new');
      expect(harness.service.isLoggedIn, isTrue);
      expect(harness.storage.stored!.stoken, 'st-new');
    });

    test(
      'completed login poll without ltoken fails instead of closing',
      () async {
        final harness = _build(
          (options, body) => _json({
            'ok': true,
            'code': 0,
            'data': {..._sessionPayload, 'ltoken': ''},
          }),
        );

        final res = await harness.service.pollChallenge(challenge: _challenge);

        expect(res.ok, isFalse);
        expect(res.isPending, isFalse);
        expect(res.message, isNotEmpty);
        expect(harness.service.isLoggedIn, isFalse);
        expect(harness.storage.stored, isNull);
      },
    );

    test(
      'completed non-login poll succeeds without touching the session',
      () async {
        final harness = _build(
          (options, body) => _json({
            'ok': true,
            'code': 0,
            'data': {'expires_in': 600},
          }),
        );

        final res = await harness.service.pollChallenge(challenge: _challenge);

        expect(res.ok, isTrue);
        expect(harness.service.isLoggedIn, isFalse);
        expect(harness.storage.stored, isNull);
      },
    );

    test('login success envelope without tokens is a failure', () async {
      final harness = _build(
        (options, body) => _json({
          'ok': true,
          'code': 0,
          'data': {'user_id': 'u1'},
        }),
      );

      final res = await harness.service.startLogin(
        email: 'user@example.com',
        password: 'password123',
        identity: _identity,
      );

      expect(res.ok, isFalse);
      expect(harness.service.isLoggedIn, isFalse);
    });

    test('expired challenge is terminal, not pending', () async {
      final harness = _build(
        (options, body) =>
            _json({'ok': false, 'code': 3001, 'message': 'challenge expired'}),
      );

      final res = await harness.service.pollChallenge(challenge: _challenge);

      expect(res.isPending, isFalse);
      expect(res.ok, isFalse);
      expect(res.code, 3001);
      expect(harness.service.isLoggedIn, isFalse);
    });
  });

  group('Registration', () {
    test(
      'confirm answered with ok:true + code 100 keeps the captcha',
      () async {
        final harness = _build(
          (options, body) => _json({
            'ok': true,
            'code': 100,
            'data': {
              'challenge_id': 'cid',
              'challenge_secret': 'secret',
              'captcha_url': '/api/captcha/cid',
              'poll_url': '/api/challenge/cid',
              'expires_in': 300,
            },
          }, status: 202),
        );

        final res = await harness.service.completeRegister(
          email: 'user@example.com',
          code: '123456',
          password: 'password123',
          username: 'Osmium-Tester',
          identity: _identity,
        );

        expect(res.needsCaptcha, isTrue);
        expect(harness.service.isLoggedIn, isFalse);
      },
    );

    test('send-code posts to the Windows-compatible route', () async {
      final harness = _build(
        (options, body) => _json({
          'ok': true,
          'code': 0,
          'data': {'expires_in': 600},
        }),
      );

      final res = await harness.service.startRegisterSendCode(
        'user@example.com',
      );

      expect(res.ok, isTrue);
      expect(
        harness.adapter.requests.single.uri.path,
        '/api/auth/register/send-code',
      );
      final sent = jsonDecode(
        harness.adapter.requestBodies.single!,
      ) as Map<String, dynamic>;
      expect(sent['email'], 'user@example.com');
    });

    test('confirm uses /confirm and the aligned payload', () async {
      final harness = _build(
        (options, body) =>
            _json({'ok': true, 'code': 0, 'data': _sessionPayload}),
      );

      final res = await harness.service.completeRegister(
        email: ' user@example.com ',
        code: ' 123456 ',
        password: 'password123',
        username: ' Osmium-Tester ',
        identity: _identity,
      );

      expect(res.ok, isTrue);
      expect(harness.service.isLoggedIn, isTrue);
      expect(
        harness.adapter.requests.single.uri.path,
        '/api/auth/register/confirm',
      );

      final sent = jsonDecode(
        harness.adapter.requestBodies.single!,
      ) as Map<String, dynamic>;
      expect(sent['email'], 'user@example.com');
      expect(sent['username'], 'Osmium-Tester');
      expect(sent['password'], 'password123');
      expect(sent['code'], '123456');
      expect(sent['device_id'], _identity.deviceId);
      expect(sent['device_name'], _identity.deviceName);
      expect(sent.containsKey('os_version'), isTrue);
    });
  });

  group('Saved sign-in', () {
    test('restores from disk without waiting on the network', () async {
      final h = _build((options, body) => _json({'ok': true}));
      h.storage.stored = AccountSessionData.fromLoginPayload(_sessionPayload);
      final session = await h.service.initialize();
      expect(session, isNotNull);
      expect(h.service.isLoggedIn, isTrue);
      expect(h.adapter.requests, isEmpty);
    });

    test('a session read before launch is signed in from the start', () {
      final app = AppController(
        settingsRepo: SettingsRepository(),
        accountService: AccountService(
          _FakeSecureStorage(),
          savedSession: AccountSessionData.fromLoginPayload(_sessionPayload),
        ),
        signaling: SignalingClient(),
      );
      addTearDown(app.dispose);
      expect(app.isLoggedIn, isTrue);
      expect(app.account?.username, 'Osmium-Tester');
      expect(app.refreshingDevices, isTrue);
    });
  });
}
