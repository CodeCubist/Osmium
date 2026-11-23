// Private fields cannot be initialized with initializing formals for public
// named parameters, so the lint's suggestion is not applicable here.
// ignore_for_file: prefer_initializing_formals

import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'package:osmium_mobile/app.dart';
import 'package:osmium_mobile/core/models/account_models.dart';
import 'package:osmium_mobile/core/models/device_identity.dart';
import 'package:osmium_mobile/core/models/osmium_settings.dart';
import 'package:osmium_mobile/services/account/account_service.dart';
import 'package:osmium_mobile/services/signaling/signaling_client.dart';
import 'package:osmium_mobile/services/storage/secure_storage_service.dart';
import 'package:osmium_mobile/services/storage/settings_repository.dart';
import 'package:osmium_mobile/state/app_controller.dart';

/// AppController with every side effect (prefs, secure storage, websocket)
/// replaced by in-memory state. Nothing in the home UI may reach a platform
/// channel while these tests run.
class FakeAppController extends AppController {
  FakeAppController({
    this.loggedIn = false,
    List<AccountDeviceDto> devices = const [],
    List<ConnectionHistoryEntry> history = const [],
    OsmiumSettings settings = const OsmiumSettings(),
    AccountSessionData? account,
  }) : _devices = devices,
       _history = history,
       _settings = settings,
       _account = account,
       super(
         settingsRepo: SettingsRepository(),
         accountService: AccountService(SecureStorageService()),
         signaling: SignalingClient(),
       );

  final bool loggedIn;
  final List<AccountDeviceDto> _devices;
  final List<ConnectionHistoryEntry> _history;
  OsmiumSettings _settings;
  final AccountSessionData? _account;
  final unreachable = StreamController<void>.broadcast();
  int retries = 0;

  @override
  Future<void> initialize() async {}

  @override
  Stream<void> get serverUnreachable => unreachable.stream;

  @override
  void retrySignaling() => retries++;

  @override
  bool get isLoggedIn => loggedIn;

  @override
  DeviceIdentity get identity =>
      const DeviceIdentity(deviceId: '771234562', deviceName: 'Osmium PC');

  @override
  OsmiumSettings get settings => _settings;

  @override
  List<ConnectionHistoryEntry> get history => _history;

  @override
  AccountSessionData? get account => _account;

  @override
  List<AccountDeviceDto> get accountDevices => _devices;

  @override
  bool get refreshingDevices => false;

  @override
  SignalingConnectionState get signalingState =>
      SignalingConnectionState.connected;

  @override
  Future<void> updateSettings(OsmiumSettings next) async {
    _settings = next;
    notifyListeners();
  }

  @override
  Future<void> renameSelf(String nextName) async {}

  @override
  Future<void> refreshAccountDevices() async {}

  @override
  Future<bool> renameRemoteDevice(String deviceId, String customName) async =>
      true;

  @override
  Future<bool> removeRemoteDevice(String deviceId) async => true;

  @override
  void forceDisconnectDevice(String deviceId) {}

  @override
  Future<void> removeHistoryEntry(String deviceId) async {}

  @override
  Future<void> logout({bool revokeAll = false}) async {}
}

AccountDeviceDto _device(
  String id,
  String name, {
  bool online = true,
  bool controlled = false,
  String os = 'Windows 11 Pro 24H2',
}) {
  return AccountDeviceDto(
    deviceId: id,
    deviceName: name,
    osVersion: os,
    isOnline: online,
    isBeingControlled: controlled,
    lastActiveAt: 1700000000,
  );
}

AccountSessionData _session() => const AccountSessionData(
  userId: 'u1',
  email: 'marchen@example.com',
  username: 'Marchen',
  stoken: 's',
  stokenExpiresAtUnix: 0,
  ltoken: 'l',
  ltokenExpiresAtUnix: 0,
);

FakeAppController _populatedController() {
  return FakeAppController(
    loggedIn: true,
    account: _session(),
    devices: [
      _device('111222333', 'Studio Desktop'),
      _device('444555666', 'Living Room PC', online: false),
      _device('777888999', '渲染工作站（超长名称测试）', controlled: true),
    ],
    history: const [
      ConnectionHistoryEntry(
        deviceId: '123456789',
        deviceName: 'Home Server',
        lastConnectedAtMs: 1700000000,
        sameAccount: true,
      ),
      ConnectionHistoryEntry(
        deviceId: '987654321',
        deviceName: 'Office PC',
        savedAccessCode: 'ABC123',
        lastConnectedAtMs: 1690000000,
      ),
    ],
  );
}

Widget _app(FakeAppController controller) {
  return ProviderScope(
    overrides: [appControllerProvider.overrideWith((ref) => controller)],
    child: const OsmiumApp(),
  );
}

/// Pumps the real app at a real phone logical size. Tests then visit every
/// tab, because IndexedStack keeps unselected tabs offstage (and therefore out
/// of layout until selected).
Future<void> _pumpHome(
  WidgetTester tester, {
  required FakeAppController controller,
  required Size logicalSize,
  double textScale = 1.0,
  Brightness brightness = Brightness.light,
}) async {
  tester.view.devicePixelRatio = 3.0;
  tester.view.physicalSize = logicalSize * 3.0;
  tester.view.padding = const FakeViewPadding(top: 24 * 3, bottom: 24 * 3);
  tester.platformDispatcher.textScaleFactorTestValue = textScale;
  tester.platformDispatcher.platformBrightnessTestValue = brightness;
  addTearDown(() {
    tester.view.resetPhysicalSize();
    tester.view.resetDevicePixelRatio();
    tester.view.resetPadding();
    tester.view.resetViewInsets();
    tester.platformDispatcher.clearTextScaleFactorTestValue();
    tester.platformDispatcher.clearPlatformBrightnessTestValue();
  });

  await tester.pumpWidget(_app(controller));
  await tester.pump(const Duration(milliseconds: 400));
  await tester.pump(const Duration(milliseconds: 400));
}

Future<void> _selectConnectTab(WidgetTester tester) async {
  // Unique while the connect tab is offstage; its own bolt icon only exists
  // once selected.
  await tester.tap(find.byIcon(Icons.bolt_rounded));
  await tester.pump(const Duration(milliseconds: 400));
  await tester.pump(const Duration(milliseconds: 400));
}

/// Miuix overlays start their spring on the first frame after `show` flips, so
/// a single long pump only schedules it. Pump a few frames to let the surface
/// reach its resting position before tapping.
Future<void> _settle(WidgetTester tester) async {
  for (var i = 0; i < 8; i++) {
    await tester.pump(const Duration(milliseconds: 100));
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  final phone = const Size(393, 873);
  final narrow = const Size(320, 640);
  final landscape = const Size(873, 393);

  for (final size in [phone, narrow, landscape]) {
    for (final scale in [1.0, 1.3]) {
      for (final brightness in [Brightness.light, Brightness.dark]) {
        testWidgets('home ${size.width.toInt()}x${size.height.toInt()} '
            'text ${scale}x ${brightness.name}', (tester) async {
          await _pumpHome(
            tester,
            controller: _populatedController(),
            logicalSize: size,
            textScale: scale,
            brightness: brightness,
          );

          expect(tester.takeException(), isNull);

          await _selectConnectTab(tester);
          expect(tester.takeException(), isNull);

          // Back to devices, then me.
          await tester.tap(find.byIcon(Icons.devices_rounded));
          await tester.pump(const Duration(milliseconds: 400));
          await tester.tap(find.byIcon(Icons.person_rounded));
          await tester.pump(const Duration(milliseconds: 400));
          await tester.pump(const Duration(milliseconds: 400));
          expect(tester.takeException(), isNull);
        });
      }
    }
  }

  for (final size in [phone, narrow, landscape]) {
    for (final scale in [1.0, 1.3]) {
      testWidgets('connect tab with keyboard inset ${size.width.toInt()}x'
          '${size.height.toInt()} text ${scale}x', (tester) async {
        await _pumpHome(
          tester,
          controller: _populatedController(),
          logicalSize: size,
          textScale: scale,
        );
        await _selectConnectTab(tester);

        // Roughly a third of the screen, like a real IME; landscape phones
        // cannot fit a 320dp keyboard at all.
        tester.view.viewInsets = FakeViewPadding(
          bottom: size.height * 0.36 * 3,
        );
        await tester.pump(const Duration(milliseconds: 300));
        await tester.pump(const Duration(milliseconds: 300));
        expect(tester.takeException(), isNull);

        // Focus the device code field to bring up the IME for real. Focus is
        // requested directly so the check also works when the landscape IME
        // leaves only a sliver of the list hittable.
        final field = find.byType(TextField).first;
        tester.widget<TextField>(field).focusNode?.requestFocus();
        await tester.pump(const Duration(milliseconds: 300));
        await tester.pump(const Duration(milliseconds: 300));
        expect(tester.takeException(), isNull);
      });
    }
  }

  testWidgets('device actions sheet and rename dialog fit landscape/1.3', (
    tester,
  ) async {
    await _pumpHome(
      tester,
      controller: _populatedController(),
      logicalSize: landscape,
      textScale: 1.3,
    );

    await tester.longPress(find.text('Studio Desktop'));
    await _settle(tester);
    expect(tester.takeException(), isNull);
    expect(find.text('重命名'), findsOneWidget);

    await tester.tap(find.text('重命名'));
    await _settle(tester);
    expect(tester.takeException(), isNull);
    expect(find.text('设备名称'), findsOneWidget);

    await tester.tap(find.text('取消'));
    await _settle(tester);
    expect(tester.takeException(), isNull);
    expect(find.text('设备名称'), findsNothing);
  });

  testWidgets('connect form formats input and shows validation at 320/1.3', (
    tester,
  ) async {
    await _pumpHome(
      tester,
      controller: _populatedController(),
      logicalSize: narrow,
      textScale: 1.3,
    );
    await _selectConnectTab(tester);

    final fields = find.byType(TextField);
    await tester.enterText(fields.at(0), '123456789');
    await tester.pump(const Duration(milliseconds: 200));
    expect(
      tester.widget<TextField>(fields.at(0)).controller?.text,
      '123 456 789',
    );

    await tester.enterText(fields.at(1), 'abc123');
    await tester.pump(const Duration(milliseconds: 200));
    expect(tester.widget<TextField>(fields.at(1)).controller?.text, 'ABC123');

    // The clear-affordance now exists; the trailing row must still fit.
    expect(find.byIcon(Icons.close_rounded), findsWidgets);
    expect(tester.takeException(), isNull);

    // With the code cleared, 立即连接 surfaces the inline error block.
    await tester.enterText(fields.at(0), '');
    await tester.pump(const Duration(milliseconds: 200));
    await tester.tap(find.text('立即连接'));
    await tester.pump(const Duration(milliseconds: 300));
    expect(find.text('请输入 9 位设备码'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('logged-in empty device list renders without exceptions', (
    tester,
  ) async {
    await _pumpHome(
      tester,
      controller: FakeAppController(loggedIn: true, account: _session()),
      logicalSize: narrow,
      textScale: 1.3,
    );
    expect(find.text('暂无设备'), findsOneWidget);
    expect(find.text('通过设备码连接'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('self-name dialog edits and closes cleanly at 320/1.3', (
    tester,
  ) async {
    await _pumpHome(
      tester,
      controller: _populatedController(),
      logicalSize: narrow,
      textScale: 1.3,
    );

    await tester.tap(find.byIcon(Icons.person_rounded));
    await tester.pump(const Duration(milliseconds: 400));
    await tester.pump(const Duration(milliseconds: 400));
    expect(tester.takeException(), isNull);

    await tester.tap(find.text('本机名称'));
    await _settle(tester);
    expect(tester.takeException(), isNull);
    expect(find.text('保存'), findsOneWidget);

    await tester.enterText(find.byType(TextField).first, 'Renamed PC');
    await tester.tap(find.text('保存'));
    await _settle(tester);
    expect(tester.takeException(), isNull);
    expect(find.text('保存'), findsNothing);
  });

  testWidgets('an unreachable server on launch asks to retry', (tester) async {
    final controller = FakeAppController();
    await _pumpHome(tester, controller: controller, logicalSize: phone);
    expect(find.text('无法连接服务器'), findsNothing);

    controller.unreachable.add(null);
    await _settle(tester);
    expect(tester.takeException(), isNull);
    expect(find.text('无法连接服务器'), findsOneWidget);

    await tester.tap(find.text('重试'));
    await _settle(tester);
    expect(controller.retries, 1);
    expect(find.text('无法连接服务器'), findsNothing);
  });

  testWidgets('logged-out empty states render without exceptions', (
    tester,
  ) async {
    await _pumpHome(
      tester,
      controller: FakeAppController(),
      logicalSize: narrow,
      textScale: 1.3,
    );
    expect(tester.takeException(), isNull);

    await _selectConnectTab(tester);
    expect(tester.takeException(), isNull);
  });
}
