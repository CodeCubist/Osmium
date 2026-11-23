import 'package:flutter_test/flutter_test.dart';
import 'package:osmium_mobile/core/models/device_identity.dart';
import 'package:osmium_mobile/core/models/osmium_settings.dart';

void main() {
  test('DeviceIdentity normalizes and formats 9-digit device IDs', () {
    expect(DeviceIdentity.normalizeDeviceId('123 456-789'), '123456789');
    expect(DeviceIdentity.formatDeviceId('123456789'), '123 456 789');
  });

  test('OsmiumSettings defaults to directTouch control mode', () {
    const settings = OsmiumSettings();
    expect(settings.controlMode, ControlMode.directTouch);
    expect(settings.precisionMagnifierEnabled, isTrue);
  });
}
