import 'dart:convert';
import 'dart:math';

class DeviceIdentity {
  const DeviceIdentity({
    required this.deviceId,
    required this.deviceName,
    this.deviceSecret = '',
  });

  final String deviceId;
  final String deviceName;
  final String deviceSecret;

  static String generateDeviceId() {
    final rng = Random.secure();
    final first = 1 + rng.nextInt(9);
    final rest = List.generate(8, (_) => rng.nextInt(10)).join();
    return '$first$rest';
  }

  static String generateDeviceSecret() {
    final rng = Random.secure();
    final bytes = List<int>.generate(32, (_) => rng.nextInt(256));
    return base64Url.encode(bytes).replaceAll('=', '');
  }

  static String normalizeDeviceId(String raw) {
    final buf = StringBuffer();
    for (final code in raw.codeUnits) {
      if (code >= 48 && code <= 57) {
        buf.writeCharCode(code);
      }
    }
    return buf.toString();
  }

  static String formatDeviceId(String raw) {
    final digits = normalizeDeviceId(raw);
    if (digits.length != 9) return digits;
    return '${digits.substring(0, 3)} ${digits.substring(3, 6)} ${digits.substring(6, 9)}';
  }

  /// Mirrors Windows `P2PCoordinator` (`accessCode.Trim().ToUpperInvariant()`):
  /// inner whitespace is significant because the host hashes the code verbatim.
  static String normalizeAccessCode(String raw) {
    return raw.trim().toUpperCase();
  }

  DeviceIdentity copyWith({
    String? deviceId,
    String? deviceName,
    String? deviceSecret,
  }) {
    return DeviceIdentity(
      deviceId: deviceId ?? this.deviceId,
      deviceName: deviceName ?? this.deviceName,
      deviceSecret: deviceSecret ?? this.deviceSecret,
    );
  }
}
