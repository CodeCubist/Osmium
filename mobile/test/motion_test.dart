// Motion behaviour: page pushes, predictive back, tab fades, press feedback,
// Miuix overlays and the container transform. These tests drive real widgets
// through real route/navigator machinery so the animations are exercised, not
// just constructed.
// ignore_for_file: prefer_initializing_formals

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_miuix/miuix.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:osmium_mobile/app.dart';
import 'package:osmium_mobile/core/models/account_models.dart';
import 'package:osmium_mobile/core/models/device_identity.dart';
import 'package:osmium_mobile/core/models/osmium_settings.dart';
import 'package:osmium_mobile/core/theme/osmium_motion.dart';
import 'package:osmium_mobile/core/theme/osmium_theme.dart';
import 'package:osmium_mobile/services/account/account_service.dart';
import 'package:osmium_mobile/services/session/remote_session_controller.dart';
import 'package:osmium_mobile/services/signaling/signaling_client.dart';
import 'package:osmium_mobile/services/storage/secure_storage_service.dart';
import 'package:osmium_mobile/services/storage/settings_repository.dart';
import 'package:osmium_mobile/state/app_controller.dart';
import 'package:osmium_mobile/ui/auth/login_page.dart';
import 'package:osmium_mobile/ui/connect/connect_tab.dart';
import 'package:osmium_mobile/ui/devices/devices_tab.dart';
import 'package:osmium_mobile/ui/widgets/osmium_container_route.dart';
import 'package:osmium_mobile/ui/widgets/osmium_motion_widgets.dart';
import 'package:osmium_mobile/ui/widgets/osmium_page_transitions.dart';
import 'package:osmium_mobile/ui/widgets/osmium_tab_switcher.dart';

class FakeAppController extends AppController {
  FakeAppController({
    this.loggedIn = false,
    List<AccountDeviceDto> devices = const [],
    List<ConnectionHistoryEntry> history = const [],
    AccountSessionData? account,
  }) : _devices = [...devices],
       _history = history,
       _account = account,
       super(
         settingsRepo: SettingsRepository(),
         accountService: AccountService(SecureStorageService()),
         signaling: SignalingClient(),
       );

  final bool loggedIn;
  final List<AccountDeviceDto> _devices;
  final List<ConnectionHistoryEntry> _history;
  final AccountSessionData? _account;

  final List<String> removed = [];

  @override
  Future<void> initialize() async {}

  @override
  bool get isLoggedIn => loggedIn;

  @override
  DeviceIdentity get identity =>
      const DeviceIdentity(deviceId: '771234562', deviceName: 'Osmium PC');

  @override
  OsmiumSettings get settings => const OsmiumSettings();

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
  Future<void> renameSelf(String nextName) async {}

  @override
  Future<void> refreshAccountDevices() async {}

  @override
  Future<bool> renameRemoteDevice(String deviceId, String customName) async =>
      true;

  @override
  Future<bool> removeRemoteDevice(String deviceId) async {
    removed.add(deviceId);
    _devices.removeWhere((d) => d.deviceId == deviceId);
    notifyListeners();
    return true;
  }

  @override
  void forceDisconnectDevice(String deviceId) {}

  @override
  Future<void> removeHistoryEntry(String deviceId) async {}

  @override
  Future<void> logout({bool revokeAll = false}) async {}
}

AccountDeviceDto _device(String id, String name, {bool online = true}) {
  return AccountDeviceDto(
    deviceId: id,
    deviceName: name,
    osVersion: 'Windows 11 Pro 24H2',
    isOnline: online,
    isBeingControlled: false,
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

FakeAppController _controller() => FakeAppController(
  loggedIn: true,
  account: _session(),
  devices: [_device('111222333', 'Studio Desktop')],
  history: const [
    ConnectionHistoryEntry(
      deviceId: '123456789',
      deviceName: 'Home Server',
      lastConnectedAtMs: 1700000000,
      sameAccount: true,
    ),
  ],
);

void _phone(WidgetTester tester) {
  tester.view.devicePixelRatio = 3.0;
  tester.view.physicalSize = const Size(393, 873) * 3.0;
  tester.view.padding = const FakeViewPadding(top: 24 * 3, bottom: 24 * 3);
  addTearDown(() {
    tester.view.resetPhysicalSize();
    tester.view.resetDevicePixelRatio();
    tester.view.resetPadding();
    tester.view.resetViewInsets();
  });
}

/// Wraps a single tab with the Miuix theme it would normally inherit from the
/// shell app. `disableAnimations` swaps in the platform reduced-motion flag.
Widget _tabHost(
  FakeAppController controller,
  Widget child, {
  bool disableAnimations = false,
}) {
  return ProviderScope(
    overrides: [appControllerProvider.overrideWith((ref) => controller)],
    child: MaterialApp(
      theme: ThemeData(useMaterial3: true),
      builder: (context, appChild) {
        final media = MediaQuery.of(context);
        return MediaQuery(
          data: media.copyWith(disableAnimations: disableAnimations),
          child: MiuixTheme(
            data: MiuixThemeData(
              colors: OsmiumTheme.lightColors(),
              textStyles: OsmiumTheme.textStyles(),
              brightness: Brightness.light,
            ),
            child: Material(type: MaterialType.transparency, child: appChild!),
          ),
        );
      },
      home: child,
    ),
  );
}

Future<void> _settle(WidgetTester tester, [int frames = 8]) async {
  for (var i = 0; i < frames; i++) {
    await tester.pump(const Duration(milliseconds: 100));
  }
}

/// Starts a frame-accurate motion pump: the first pump schedules the
/// animation, the second advances it.
Future<void> _advance(WidgetTester tester, Duration by) async {
  await tester.pump();
  await tester.pump(by);
}

const MethodCodec _codec = StandardMethodCodec();

Future<void> _sendBackGesture(
  WidgetTester tester,
  String method, [
  Map<String, Object?>? arguments,
]) async {
  await TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
      .handlePlatformMessage(
        'flutter/backgesture',
        _codec.encodeMethodCall(MethodCall(method, arguments)),
        (_) {},
      );
  await tester.pump();
}

Map<String, Object?> _backEvent(double progress) => {
  'touchOffset': <Object?>[0.0, 300.0],
  'progress': progress,
  'swipeEdge': 0,
};

// ---------------------------------------------------------------- page routes

class _NavHome extends StatelessWidget {
  const _NavHome({required this.onPush});
  final VoidCallback onPush;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: Align(
        alignment: Alignment.topLeft,
        child: TextButton(onPressed: onPush, child: const Text('open')),
      ),
    );
  }
}

class _NavDetail extends StatelessWidget {
  const _NavDetail({required this.onPop});
  final VoidCallback onPop;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: Align(
        alignment: Alignment.topLeft,
        child: TextButton(onPressed: onPop, child: const Text('back')),
      ),
    );
  }
}

Widget _navApp(
  GlobalKey<NavigatorState> navigatorKey, {
  bool disableAnimations = false,
}) {
  return MaterialApp(
    navigatorKey: navigatorKey,
    builder: (context, child) => MediaQuery(
      data: MediaQuery.of(context)
          .copyWith(disableAnimations: disableAnimations),
      child: child!,
    ),
    theme: ThemeData(
      useMaterial3: true,
      pageTransitionsTheme: const PageTransitionsTheme(
        builders: {TargetPlatform.android: OsmiumPageTransitionsBuilder()},
      ),
    ),
    home: _NavHome(
      onPush: () => navigatorKey.currentState!.push<void>(
        MaterialPageRoute(
          builder: (context) =>
              _NavDetail(onPop: () => Navigator.of(context).pop()),
        ),
      ),
    ),
  );
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('OsmiumMotion', () {
    test('spring curve starts at 0, ends at 1 and is monotonic enough', () {
      final curve = OsmiumMotion.pushCurve;
      expect(curve.transform(0), 0);
      expect(curve.transform(1), 1);
      expect(curve.transform(0.4), greaterThan(0.5));
      // Critically damped: never overshoots past 1 by more than a rounding hair.
      for (var i = 0; i <= 20; i++) {
        expect(curve.transform(i / 20), inInclusiveRange(0, 1.001));
      }
    });

    test('under-damped spring overshoots slightly', () {
      final curve = SpringCurve(response: 0.36, damping: 0.68);
      expect(curve.transform(0), 0);
      expect(curve.transform(1), 1);
      final peak = List.generate(
        50,
        (i) => curve.transform(i / 50),
      ).reduce((a, b) => a > b ? a : b);
      expect(peak, greaterThan(1.0));
      expect(peak, lessThan(1.3));
    });
  });

  group('page transitions', () {
    testWidgets('push slides in from the trailing edge and shifts the page '
        'underneath', (tester) async {
      final key = GlobalKey<NavigatorState>();
      await tester.pumpWidget(_navApp(key));
      await tester.pumpAndSettle();

      final resting = tester.getTopLeft(find.text('open')).dx;
      await tester.tap(find.text('open'));
      await _advance(tester, const Duration(milliseconds: 130));

      final incomingDx = tester.getTopLeft(find.text('back')).dx;
      expect(incomingDx, greaterThan(resting + 40));
      // Page underneath drifts a fraction of the width to the leading side.
      expect(tester.getTopLeft(find.text('open')).dx, lessThan(resting));
      expect(tester.takeException(), isNull);

      await _settle(tester);
      expect(tester.getTopLeft(find.text('back')).dx, closeTo(resting, 0.5));
      expect(find.text('open'), findsNothing);
    });

    testWidgets('pop slides the top page out and restores the one below', (
      tester,
    ) async {
      final key = GlobalKey<NavigatorState>();
      await tester.pumpWidget(_navApp(key));
      await tester.pumpAndSettle();
      final resting = tester.getTopLeft(find.text('open')).dx;
      await tester.tap(find.text('open'));
      await _settle(tester);
      expect(tester.getTopLeft(find.text('back')).dx, closeTo(resting, 0.5));

      await tester.tap(find.text('back'));
      await _advance(tester, const Duration(milliseconds: 110));
      expect(
        tester.getTopLeft(find.text('back')).dx,
        greaterThan(resting + 40),
      );
      expect(tester.getTopLeft(find.text('open')).dx, lessThan(resting));

      await _settle(tester);
      expect(find.text('back'), findsNothing);
      expect(tester.getTopLeft(find.text('open')).dx, closeTo(resting, 0.5));
    });

    testWidgets('reduced motion pushes without sliding or dimming', (
      tester,
    ) async {
      final key = GlobalKey<NavigatorState>();
      await tester.pumpWidget(_navApp(key, disableAnimations: true));
      await tester.pumpAndSettle();
      final resting = tester.getTopLeft(find.text('open')).dx;
      await tester.tap(find.text('open'));
      await tester.pump();
      await tester.pump();

      // The page is in place from the first frame; only the navigator's own
      // bookkeeping still runs in the background.
      expect(tester.getTopLeft(find.text('back')).dx, closeTo(resting, 0.5));
      expect(tester.getTopLeft(find.text('open')).dx, closeTo(resting, 0.5));
      await _settle(tester);
      expect(find.text('back'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('predictive back follows the finger and cancels back', (
      tester,
    ) async {
      final key = GlobalKey<NavigatorState>();
      await tester.pumpWidget(_navApp(key));
      await tester.pumpAndSettle();
      final resting = tester.getTopLeft(find.text('open')).dx;
      await tester.tap(find.text('open'));
      await _settle(tester);

      final width =
          tester.view.physicalSize.width / tester.view.devicePixelRatio;

      await _sendBackGesture(tester, 'startBackGesture', _backEvent(0));
      await _sendBackGesture(
        tester,
        'updateBackGestureProgress',
        _backEvent(0.5),
      );
      // Gesture frames map linearly, so half a swipe is half a screen.
      expect(
        tester.getTopLeft(find.text('back')).dx,
        closeTo(resting + width * 0.5, 2),
      );

      await _sendBackGesture(tester, 'cancelBackGesture');
      await _settle(tester);
      expect(find.text('back'), findsOneWidget);
      expect(tester.getTopLeft(find.text('back')).dx, closeTo(resting, 0.5));
      expect(tester.takeException(), isNull);
    });

    testWidgets('predictive back commits and pops the route', (tester) async {
      final key = GlobalKey<NavigatorState>();
      await tester.pumpWidget(_navApp(key));
      await tester.pumpAndSettle();
      await tester.tap(find.text('open'));
      await _settle(tester);

      await _sendBackGesture(tester, 'startBackGesture', _backEvent(0));
      await _sendBackGesture(
        tester,
        'updateBackGestureProgress',
        _backEvent(1),
      );
      await _sendBackGesture(tester, 'commitBackGesture');
      await _settle(tester);

      expect(find.text('back'), findsNothing);
      expect(find.text('open'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  });

  group('container transform route', () {
    testWidgets('grows from the source rect and fades the page in', (
      tester,
    ) async {
      final sourceKey = GlobalKey();
      final navigatorKey = GlobalKey<NavigatorState>();
      await tester.pumpWidget(
        MaterialApp(
          navigatorKey: navigatorKey,
          home: Scaffold(
            body: Align(
              alignment: Alignment.topLeft,
              child: SizedBox(key: sourceKey, width: 120, height: 64),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      navigatorKey.currentState!.push<void>(
        OsmiumContainerRoute(
          sourceKey: sourceKey,
          pageBuilder: (_) =>
              const Scaffold(body: Center(child: Text('session'))),
        ),
      );
      await _advance(tester, const Duration(milliseconds: 150));
      expect(tester.takeException(), isNull);
      // Mid-flight the session page is still mostly transparent.
      final opacity = tester
          .widgetList<Opacity>(
            find.ancestor(
              of: find.text('session'),
              matching: find.byType(Opacity),
            ),
          )
          .map((w) => w.opacity)
          .reduce((a, b) => a < b ? a : b);
      expect(opacity, lessThan(1));

      await _settle(tester);
      expect(find.text('session'), findsOneWidget);
      expect(
        tester
            .widgetList<Opacity>(
              find.ancestor(
                of: find.text('session'),
                matching: find.byType(Opacity),
              ),
            )
            .map((w) => w.opacity)
            .reduce((a, b) => a < b ? a : b),
        1,
      );
      expect(tester.takeException(), isNull);
    });

    testWidgets('survives a source card that no longer exists', (tester) async {
      final navigatorKey = GlobalKey<NavigatorState>();
      await tester.pumpWidget(
        MaterialApp(
          navigatorKey: navigatorKey,
          home: const Scaffold(body: SizedBox()),
        ),
      );
      await tester.pumpAndSettle();

      navigatorKey.currentState!.push<void>(
        OsmiumContainerRoute(
          sourceKey: GlobalKey(),
          pageBuilder: (_) =>
              const Scaffold(body: Center(child: Text('session'))),
        ),
      );
      await _advance(tester, const Duration(milliseconds: 150));
      expect(tester.takeException(), isNull);

      await _settle(tester);
      expect(find.text('session'), findsOneWidget);

      // Pop back to the (absent) source without throwing.
      navigatorKey.currentState!.pop();
      await _advance(tester, const Duration(milliseconds: 150));
      await _settle(tester);
      expect(find.text('session'), findsNothing);
      expect(tester.takeException(), isNull);
    });
  });

  group('tabs', () {
    testWidgets('tab change fades through, drifting from the dock side', (
      tester,
    ) async {
      // Ancestors come nearest first; the tab's own wrapper is the outermost
      // transform inside the switcher, below anything a list item adds.
      double slideOf(String text) => tester
          .widget<Transform>(
            find
                .ancestor(
                  of: find.text(text),
                  matching: find.descendant(
                    of: find.byType(OsmiumTabSwitcher),
                    matching: find.byType(Transform),
                  ),
                )
                .last,
          )
          .transform
          .getTranslation()
          .x;

      _phone(tester);
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            appControllerProvider.overrideWith((ref) => _controller()),
          ],
          child: const OsmiumApp(),
        ),
      );
      await _settle(tester);
      expect(find.text('Studio Desktop'), findsOneWidget);

      await tester.tap(find.byIcon(Icons.bolt_rounded).first);
      await _settle(tester);
      final field = find.byType(TextField).first;
      await tester.enterText(field, '123456789');
      await tester.pump(const Duration(milliseconds: 200));
      expect(tester.widget<TextField>(field).controller?.text, '123 456 789');

      await tester.tap(find.byIcon(Icons.devices_rounded));
      await _advance(tester, const Duration(milliseconds: 100));
      // Both surfaces are painted mid-switch. Devices sits left of Connect, so
      // it drifts in from the left, a few pixels only; Connect fades in place.
      expect(find.text('立即连接'), findsOneWidget);
      expect(find.text('Studio Desktop'), findsOneWidget);
      expect(slideOf('Studio Desktop'), inInclusiveRange(-20, -0.5));
      expect(slideOf('立即连接'), 0);
      await _settle(tester);
      expect(find.text('立即连接'), findsNothing);
      expect(slideOf('Studio Desktop'), 0);

      await tester.tap(find.byIcon(Icons.bolt_rounded).first);
      await _settle(tester);
      expect(
        tester.widget<TextField>(find.byType(TextField).first).controller?.text,
        '123 456 789',
      );
      expect(tester.takeException(), isNull);
    });

    testWidgets('reduced motion switches tabs without a fade', (tester) async {
      _phone(tester);
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            appControllerProvider.overrideWith((ref) => _controller()),
          ],
          child: const MediaQuery(
            data: MediaQueryData(disableAnimations: true),
            child: OsmiumApp(),
          ),
        ),
      );
      await _settle(tester);

      await tester.tap(find.byIcon(Icons.bolt_rounded).first);
      await tester.pump();
      expect(find.text('Studio Desktop'), findsNothing);
      expect(find.text('立即连接'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  });

  group('press feedback and overlays', () {
    testWidgets('device card connect button fires the launcher', (
      tester,
    ) async {
      _phone(tester);
      SessionConnectRequest? launched;
      GlobalKey? source;
      await tester.pumpWidget(
        _tabHost(
          _controller(),
          DevicesTab(
            onConnect: (request, {sourceKey}) {
              launched = request;
              source = sourceKey;
            },
            onOpenConnect: () {},
            bottomInset: 0,
          ),
        ),
      );
      await _settle(tester);

      await tester.tap(find.text('远程控制'));
      await _settle(tester);
      expect(launched?.targetDeviceId, '111222333');
      expect(source, isNotNull);
      expect(tester.takeException(), isNull);
    });

    testWidgets('long-press opens the Miuix sheet, remove confirms and fires', (
      tester,
    ) async {
      _phone(tester);
      final controller = _controller();
      await tester.pumpWidget(
        _tabHost(
          controller,
          DevicesTab(
            onConnect: (_, {sourceKey}) {},
            onOpenConnect: () {},
            bottomInset: 0,
          ),
        ),
      );
      await _settle(tester);

      await tester.longPress(find.text('Studio Desktop'));
      await _settle(tester);
      expect(find.byType(MiuixOverlayBottomSheet), findsOneWidget);
      expect(find.text('移除设备'), findsOneWidget);

      await tester.tap(find.text('移除设备'));
      await _settle(tester, 16);
      expect(controller.removed, ['111222333']);
      expect(find.text('移除设备'), findsNothing);
      expect(find.text('暂无设备'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('rename dialog opens and cancels without removing', (
      tester,
    ) async {
      _phone(tester);
      final controller = _controller();
      await tester.pumpWidget(
        _tabHost(
          controller,
          DevicesTab(
            onConnect: (_, {sourceKey}) {},
            onOpenConnect: () {},
            bottomInset: 0,
          ),
        ),
      );
      await _settle(tester);

      await tester.longPress(find.text('Studio Desktop'));
      await _settle(tester);
      await tester.tap(find.text('重命名'));
      await _settle(tester);
      expect(find.byType(MiuixOverlayDialog), findsOneWidget);
      expect(find.text('设备名称'), findsOneWidget);

      await tester.tap(find.text('取消'));
      await _settle(tester);
      expect(find.text('设备名称'), findsNothing);
      expect(controller.removed, isEmpty);
      expect(tester.takeException(), isNull);
    });

    testWidgets('invalid device code shakes in an inline error, valid one '
        'launches', (tester) async {
      _phone(tester);
      SessionConnectRequest? launched;
      await tester.pumpWidget(
        _tabHost(
          _controller(),
          ConnectTab(
            onConnect: (request, {sourceKey}) => launched = request,
            bottomInset: 0,
          ),
        ),
      );
      await _settle(tester);

      await tester.tap(find.text('立即连接'));
      await _settle(tester);
      expect(find.text('请输入 9 位设备码'), findsOneWidget);
      expect(launched, isNull);

      await tester.enterText(find.byType(TextField).first, '123456789');
      await tester.pump(const Duration(milliseconds: 200));
      await tester.tap(find.text('立即连接'));
      await _settle(tester);
      expect(launched?.targetDeviceId, '123456789');
      expect(tester.takeException(), isNull);
    });
  });

  testWidgets('login toggle slides the pill under the selected label', (
    tester,
  ) async {
    _phone(tester);
    await tester.pumpWidget(_tabHost(_controller(), const LoginPage()));
    await _settle(tester);

    final pill = find.byType(FractionallySizedBox);
    final login = tester.getCenter(find.text('登录').first);
    final register = tester.getCenter(find.text('注册'));
    expect(tester.getRect(pill).contains(login), isTrue);
    expect(tester.getRect(pill).contains(register), isFalse);

    await tester.tap(find.text('注册'));
    await _settle(tester);
    expect(tester.getRect(pill).contains(register), isTrue);
    expect(tester.getRect(pill).contains(login), isFalse);
    // Register-only fields are revealed with the mode.
    expect(find.text('验证码'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  group('motion widgets', () {
    testWidgets('reveal collapses to zero height and reports collapsed', (
      tester,
    ) async {
      var collapsed = 0;
      Widget build({required bool visible}) => MaterialApp(
        home: Scaffold(
          body: Center(
            child: OsmiumCollapse(
              collapsed: !visible,
              onCollapsed: () => collapsed++,
              child: const SizedBox(width: 100, height: 40),
            ),
          ),
        ),
      );

      await tester.pumpWidget(build(visible: true));
      final full = tester.getSize(find.byType(OsmiumCollapse));
      expect(full.height, 40);

      await tester.pumpWidget(build(visible: false));
      await _settle(tester);
      expect(tester.getSize(find.byType(OsmiumCollapse)).height, 0);
      expect(collapsed, 1);
    });

    testWidgets('reveal is instant under reduced motion', (tester) async {
      Widget at(bool visible) => MaterialApp(
        builder: (context, child) => MediaQuery(
          data: MediaQuery.of(context).copyWith(disableAnimations: true),
          child: child!,
        ),
        home: Scaffold(
          body: OsmiumAnimatedReveal(
            visible: visible,
            child: const SizedBox(height: 30, width: 50),
          ),
        ),
      );

      await tester.pumpWidget(at(false));
      expect(tester.getSize(find.byType(OsmiumAnimatedReveal)).height, 0);
      await tester.pumpWidget(at(true));
      expect(tester.getSize(find.byType(OsmiumAnimatedReveal)).height, 30);
    });
  });
}
