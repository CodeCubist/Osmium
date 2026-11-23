import 'package:flutter_test/flutter_test.dart';
import 'package:osmium_mobile/core/constants/osmium_constants.dart';
import 'package:osmium_mobile/core/models/account_models.dart';

void main() {
  const baseUrl = OsmiumConstants.defaultApiBaseUrl;

  group('QrParsedRequest.tryParseInApp', () {
    test('accepts valid official Osmium QR login URL', () {
      const raw =
          '$baseUrl/qr/login?v=1&qid=qrSession12345678&ts=1760000000&exp=1760000180&nonce=nonce_abcd1234&sig=sigValueBase64Url';
      final parsed = QrParsedRequest.tryParseInApp(raw);
      expect(parsed, isNotNull);
      expect(parsed!.qrId, 'qrSession12345678');
      expect(parsed.channel, 'in_app');
      expect(parsed.isExternal, isFalse);
      expect(parsed.qrUrl, startsWith('$baseUrl/qr/login?'));
      expect(parsed.qrUrl, contains('qid=qrSession12345678'));
      expect(parsed.qrUrl, contains('sig=sigValueBase64Url'));
    });

    test('rejects foreign domain, http downgrade, wrong path, or invalid range', () {
      expect(
        QrParsedRequest.tryParseInApp(
          'https://evil.example.com/qr/login?v=1&qid=qrSession12345678&ts=1760000000&exp=1760000180&nonce=nonce_abcd1234&sig=sigValue',
        ),
        isNull,
      );
      expect(
        QrParsedRequest.tryParseInApp(
          'http://api.marchen.cc/qr/login?v=1&qid=qrSession12345678&ts=1760000000&exp=1760000180&nonce=nonce_abcd1234&sig=sigValue',
        ),
        isNull,
      );
      expect(
        QrParsedRequest.tryParseInApp(
          '$baseUrl/other/path?v=1&qid=qrSession12345678&ts=1760000000&exp=1760000180&nonce=nonce_abcd1234&sig=sigValue',
        ),
        isNull,
      );
      expect(
        QrParsedRequest.tryParseInApp(
          '$baseUrl/qr/login?v=1&qid=qrSession12345678&ts=1760000180&exp=1760000180&nonce=nonce_abcd1234&sig=sigValue',
        ),
        isNull,
      );
      expect(
        QrParsedRequest.tryParseInApp(
          '$baseUrl/qr/login?v=2&qid=qrSession12345678&ts=1760000000&exp=1760000180&nonce=nonce_abcd1234&sig=sigValue',
        ),
        isNull,
      );
    });
  });

  group('QrParsedRequest.tryParseExternalDeepLink', () {
    test('accepts valid osmium://qr-login deep link with independent external signature', () {
      const link =
          'osmium://qr-login?v=1&qid=qrSession12345678&ts=1760000000&exp=1760000180&nonce=nonce_abcd1234&sig=sigValue&channel=external&ext_ts=1760000012&ext_nonce=extNonce_987654&ext_sig=extSigIndependent';
      final parsed = QrParsedRequest.tryParseExternalDeepLink(link);
      expect(parsed, isNotNull);
      expect(parsed!.qrId, 'qrSession12345678');
      expect(parsed.channel, 'external');
      expect(parsed.isExternal, isTrue);
      expect(parsed.extTs, '1760000012');
      expect(parsed.extNonce, 'extNonce_987654');
      expect(parsed.extSig, 'extSigIndependent');
      expect(parsed.qrUrl, startsWith('$baseUrl/qr/login?'));
      expect(parsed.qrUrl, isNot(contains('ext_sig')));
    });

    test('rejects deep links missing independent external signature parameters', () {
      expect(
        QrParsedRequest.tryParseExternalDeepLink(
          'osmium://qr-login?v=1&qid=qrSession12345678&ts=1760000000&exp=1760000180&nonce=nonce_abcd1234&sig=sigValue',
        ),
        isNull,
      );
      expect(
        QrParsedRequest.tryParseExternalDeepLink(
          'other://qr-login?v=1&qid=qrSession12345678&ts=1760000000&exp=1760000180&nonce=nonce_abcd1234&sig=sigValue&ext_ts=1760000012&ext_nonce=extNonce_987654&ext_sig=extSig',
        ),
        isNull,
      );
    });
  });

  group('QrConfirmTicket.fromJson', () {
    test('parses PC login info and forced cooldown metadata', () {
      final ticket = QrConfirmTicket.fromJson({
        'qr_id': 'qrSession12345678',
        'confirm_token': 'ctok_123',
        'channel': 'external',
        'confirm_delay_sec': 3,
        'confirm_unlock_at': 1760000015000,
        'expires_in': 160,
        'pc_info': {
          'device_id': '123456789',
          'device_name': 'Workstation-PC',
          'os_version': 'Windows 11',
          'ip_masked': '203.0.*.42',
          'ip_location': '中国 · 上海',
          'login_time': 1760000000000,
          'login_time_iso': '2025-10-09T08:53:20.000Z',
        },
      });

      expect(ticket.qrId, 'qrSession12345678');
      expect(ticket.confirmToken, 'ctok_123');
      expect(ticket.isExternal, isTrue);
      expect(ticket.confirmDelaySec, 3);
      expect(ticket.confirmUnlockAtMs, 1760000015000);
      expect(ticket.pcInfo.deviceName, 'Workstation-PC');
      expect(ticket.pcInfo.ipMasked, '203.0.*.42');
      expect(ticket.pcInfo.ipLocation, '中国 · 上海');
      expect(ticket.pcInfo.loginDateTime, isNotNull);
    });
  });
}
