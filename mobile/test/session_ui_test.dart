import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_miuix/miuix.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:osmium_mobile/core/models/host_status.dart';
import 'package:osmium_mobile/core/models/osmium_settings.dart';
import 'package:osmium_mobile/core/theme/osmium_motion.dart';
import 'package:osmium_mobile/core/theme/osmium_theme.dart';
import 'package:osmium_mobile/services/session/aux_channel.dart';
import 'package:osmium_mobile/services/session/remote_session_controller.dart';
import 'package:osmium_mobile/ui/session/widgets/connect_transition_overlay.dart';
import 'package:osmium_mobile/ui/session/widgets/session_signals.dart';
import 'package:osmium_mobile/ui/session/widgets/session_view.dart';
import 'package:osmium_mobile/ui/session/widgets/touch_control_layer.dart';
import 'package:osmium_mobile/ui/session/widgets/virtual_keyboard_overlay.dart';

/// Minimal stand-in for the controller: only the read-only state the session
/// widgets paint, plus emitters for the events a live session would produce.
class FakeSession extends ChangeNotifier implements RemoteSessionController {
  @override
  SessionPhase phase = SessionPhase.idle;
  @override
  String statusText = '正在建立连接';
  @override
  String? errorMessage;
  @override
  String hostName = 'Studio Desktop';
  @override
  bool hasReceivedFirstFrame = false;
  @override
  String? toastMessage;
  @override
  SessionStatsSnapshot stats = const SessionStatsSnapshot();
  @override
  AuxTransferProgressEvent? lastTransferProgress;
  @override
  HostSessionStatusDto hostStatus = const HostSessionStatusDto();
  @override
  HostTextFocus textFocus = HostTextFocus.none;
  @override
  int? textureId;
  @override
  bool audioMuted = false;
  @override
  bool virtualSpeaker = false;
  @override
  bool micEnabled = false;
  @override
  bool pointerCaptureActive = false;
  @override
  int orientationMode = 0;

  void emitPhase(SessionPhase next) {
    phase = next;
    notifyListeners();
  }

  void emitFirstFrame() {
    hasReceivedFirstFrame = true;
    notifyListeners();
  }

  void emitStats(SessionStatsSnapshot next) {
    stats = next;
    notifyListeners();
  }

  void emitTextFocus(HostTextFocus next) {
    textFocus = next;
    notifyListeners();
  }

  void emitToast(String? message) {
    toastMessage = message;
    notifyListeners();
  }

  @override
  void clearToast() {
    toastMessage = null;
    notifyListeners();
  }

  /// Button and wheel traffic the touch layer sent, in order.
  final List<String> input = [];

  /// Absolute pointer positions, kept apart from [input].
  final List<Offset> moves = [];

  @override
  void sendAbsoluteMouse(double normX, double normY) =>
      moves.add(Offset(normX, normY));

  @override
  void sendMouseClick(int button) => input.add('click$button');

  @override
  void sendMouseButton(int button, bool isDown) =>
      input.add('${isDown ? 'down' : 'up'}$button');

  @override
  void sendMouseWheel({int dx = 0, int dy = 0}) => input.add('wheel');

  @override
  dynamic noSuchMethod(Invocation invocation) => null;
}

class _Host extends StatefulWidget {
  const _Host({
    super.key,
    required this.session,
    required this.signals,
    this.disableAnimations = false,
  });

  final FakeSession session;
  final SessionSignals signals;
  final bool disableAnimations;

  @override
  _HostState createState() => _HostState();
}

class _HostState extends State<_Host> {
  bool panelOpen = false;
  KeyboardOverlayMode keyboard = KeyboardOverlayMode.none;
  int closeRequests = 0;

  void setPanel(bool open) => setState(() => panelOpen = open);
  void setKeyboard(KeyboardOverlayMode mode) => setState(() => keyboard = mode);

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      theme: ThemeData(useMaterial3: true),
      builder: (context, child) {
        final media = MediaQuery.of(context);
        return MediaQuery(
          data: media.copyWith(disableAnimations: widget.disableAnimations),
          child: MiuixTheme(
            data: MiuixThemeData(
              colors: OsmiumTheme.darkColors(),
              textStyles: OsmiumTheme.textStyles(),
              brightness: Brightness.dark,
            ),
            child: Material(type: MaterialType.transparency, child: child!),
          ),
        );
      },
      home: Scaffold(
        backgroundColor: Colors.black,
        // As on the real page: the session lays itself out over the IME.
        resizeToAvoidBottomInset: false,
        body: SessionView(
          controller: widget.session,
          signals: widget.signals,
          settings: const OsmiumSettings(),
          panelOpen: panelOpen,
          keyboardMode: keyboard,
          onOpenPanel: () => setPanel(true),
          onClosePanel: () {
            closeRequests++;
            setPanel(false);
          },
          onToggleControlMode: () {},
          onKeyboardModeChanged: (mode) => setState(() => keyboard = mode),
          onSettingsChanged: (_) {},
          onDisconnect: () {},
          onRetry: () {},
        ),
      ),
    );
  }
}

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

Future<void> _settle(WidgetTester tester, [int frames = 8]) async {
  for (var i = 0; i < frames; i++) {
    await tester.pump(const Duration(milliseconds: 100));
  }
}

Future<_HostState> _mount(
  WidgetTester tester,
  FakeSession session, {
  bool disableAnimations = false,
}) async {
  _phone(tester);
  final signals = SessionSignals(session);
  addTearDown(signals.dispose);
  await tester.pumpWidget(
    _Host(
      key: const ValueKey('host'),
      session: session,
      signals: signals,
      disableAnimations: disableAnimations,
    ),
  );
  await _settle(tester);
  return tester.state<_HostState>(find.byType(_Host));
}

Future<TouchControlLayerState> _mountLayer(
  WidgetTester tester,
  FakeSession session, {
  ControlMode mode = ControlMode.directTouch,
  bool enableHaptics = false,
}) async {
  tester.view.devicePixelRatio = 1.0;
  tester.view.physicalSize = const Size(393, 873);
  addTearDown(() {
    tester.view.resetPhysicalSize();
    tester.view.resetDevicePixelRatio();
  });
  final signals = SessionSignals(session);
  addTearDown(signals.dispose);
  await tester.pumpWidget(
    MaterialApp(
      home: Scaffold(
        body: TouchControlLayer(
          controller: session,
          signals: signals,
          videoReveal: const AlwaysStoppedAnimation<double>(1),
          controlMode: mode,
          sensitivity: 1.15,
          magnifierEnabled: true,
          enableHaptics: enableHaptics,
          onUserInteraction: () {},
        ),
      ),
    ),
  );
  await tester.pump();
  return tester.state<TouchControlLayerState>(find.byType(TouchControlLayer));
}

void main() {
  group('S1 stats fan-out', () {
    testWidgets('stats ticks repaint the OSD only, never the touch layer', (
      tester,
    ) async {
      final session = FakeSession()
        ..emitPhase(SessionPhase.streaming)
        ..emitFirstFrame();
      await _mount(tester, session);

      final layer = tester.state<TouchControlLayerState>(
        find.byType(TouchControlLayer),
      );
      final buildsBefore = layer.debugBuildCount;

      for (var i = 0; i < 5; i++) {
        session.emitStats(
          SessionStatsSnapshot(
            fps: 60,
            rttMs: 20 + i,
            bitrateKbps: 12000,
            videoWidth: 1920,
            videoHeight: 1080,
          ),
        );
        await tester.pump(const Duration(milliseconds: 300));
      }

      expect(layer.debugBuildCount, buildsBefore);
      expect(find.byType(TouchControlLayer), findsOneWidget);
    });
  });

  group('S2 tap ripple', () {
    testWidgets('a tap starts a ripple ticker and it clears itself', (
      tester,
    ) async {
      final session = FakeSession()
        ..emitPhase(SessionPhase.streaming)
        ..emitFirstFrame();
      await _mount(tester, session);

      final layer = tester.state<TouchControlLayerState>(
        find.byType(TouchControlLayer),
      );
      expect(layer.activeRippleCount, 0);
      expect(layer.debugRippleTicking, isFalse);

      await tester.tapAt(const Offset(200, 400));
      await tester.pump();

      expect(layer.activeRippleCount, 1);
      expect(layer.debugRippleTicking, isTrue);

      await tester.pump(const Duration(milliseconds: 120));
      expect(layer.activeRippleCount, 1);

      await tester.pump(const Duration(milliseconds: 400));
      expect(layer.activeRippleCount, 0);
      expect(layer.debugRippleTicking, isFalse);
    });
  });

  group('S4 connect transition', () {
    testWidgets('the overlay crossfades out as the first frame lands', (
      tester,
    ) async {
      final session = FakeSession();
      await _mount(tester, session);
      expect(find.text('取消'), findsOneWidget);

      session.emitPhase(SessionPhase.streaming);
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 120));
      // Streaming without a frame still waits on the transition overlay.
      expect(find.text('等待首帧画面'), findsOneWidget);

      session.emitFirstFrame();
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 120));

      final opacities = tester
          .widgetList<Opacity>(
            find.descendant(
              of: find.byType(ConnectTransitionOverlay),
              matching: find.byType(Opacity),
            ),
          )
          .map((o) => o.opacity);
      expect(opacities.any((o) => o < 1), isTrue);
      expect(find.text('取消'), findsOneWidget);

      await _settle(tester);
      expect(find.text('取消'), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets('errors fade in and offer retry', (tester) async {
      final session = FakeSession()
        ..phase = SessionPhase.failed
        ..errorMessage = '主机拒绝连接';
      await _mount(tester, session);
      await tester.pump(const Duration(milliseconds: 300));

      expect(find.text('主机拒绝连接'), findsOneWidget);
      expect(find.text('重连'), findsOneWidget);
    });
  });

  group('floating pieces', () {
    testWidgets('the orb rests right-middle and keeps it across rotation', (
      tester,
    ) async {
      final session = FakeSession()
        ..emitPhase(SessionPhase.streaming)
        ..emitFirstFrame();
      await _mount(tester, session);

      final orb = find.byIcon(Icons.touch_app_rounded);
      var center = tester.getCenter(orb);
      expect(center.dx, greaterThan(393 - 60));
      expect(center.dy, moreOrLessEquals(873 / 2, epsilon: 1));

      tester.view.physicalSize = const Size(873, 393) * 3.0;
      await _settle(tester);
      center = tester.getCenter(orb);
      expect(center.dx, greaterThan(873 - 60));
      expect(center.dy, moreOrLessEquals(393 / 2, epsilon: 1));
    });

    testWidgets('the keyboard tucks away downward', (tester) async {
      final session = FakeSession()
        ..emitPhase(SessionPhase.streaming)
        ..emitFirstFrame();
      final host = await _mount(tester, session);

      host.setKeyboard(KeyboardOverlayMode.pcFull);
      await _settle(tester);
      final open = tester.getTopLeft(find.text('Esc')).dy;

      host.setKeyboard(KeyboardOverlayMode.none);
      var last = open;
      for (var i = 0; i < 4; i++) {
        await tester.pump(const Duration(milliseconds: 40));
        if (find.text('Esc').evaluate().isEmpty) break;
        final y = tester.getTopLeft(find.text('Esc')).dy;
        expect(y, greaterThanOrEqualTo(last));
        last = y;
      }
      expect(last, greaterThan(open));
      await _settle(tester);
      expect(find.text('Esc'), findsNothing);
    });

    testWidgets('the collapse chip stays pinned right', (tester) async {
      final session = FakeSession()
        ..emitPhase(SessionPhase.streaming)
        ..emitFirstFrame();
      final host = await _mount(tester, session);
      host.setKeyboard(KeyboardOverlayMode.pcFull);
      await _settle(tester);

      final width =
          tester.view.physicalSize.width / tester.view.devicePixelRatio;
      final collapse = find.text('收起');
      final right = tester.getTopRight(collapse).dx;
      expect(right, greaterThan(width - 40));

      await tester.drag(find.text('Ctrl'), const Offset(-200, 0));
      await _settle(tester);
      expect(tester.getTopRight(collapse).dx, right);

      await tester.tap(collapse);
      await _settle(tester);
      expect(host.keyboard, KeyboardOverlayMode.none);
    });

    testWidgets('the OSD rides above either keyboard, then settles back', (
      tester,
    ) async {
      final session = FakeSession()
        ..emitPhase(SessionPhase.streaming)
        ..emitFirstFrame();
      final host = await _mount(tester, session);
      final osd = find.textContaining('FPS');
      final rest = tester.getBottomRight(osd).dy;

      host.setKeyboard(KeyboardOverlayMode.pcFull);
      await _settle(tester);
      final barTop = tester.getTopLeft(find.text('Esc')).dy - 6;
      expect(tester.getBottomRight(osd).dy, lessThanOrEqualTo(barTop - 10));

      host.setKeyboard(KeyboardOverlayMode.systemIme);
      tester.view.viewInsets = const FakeViewPadding(bottom: 300 * 3);
      await _settle(tester);
      final imeBarTop = tester.getTopLeft(find.text('Esc')).dy - 6;
      expect(imeBarTop, lessThan(873 - 300));
      expect(tester.getBottomRight(osd).dy, lessThanOrEqualTo(imeBarTop - 10));

      tester.view.resetViewInsets();
      host.setKeyboard(KeyboardOverlayMode.none);
      await _settle(tester);
      expect(tester.getBottomRight(osd).dy, moreOrLessEquals(rest));
    });

    testWidgets('a host text field brings the phone IME up and down', (
      tester,
    ) async {
      final session = FakeSession()
        ..emitPhase(SessionPhase.streaming)
        ..emitFirstFrame();
      final host = await _mount(tester, session);

      session.emitTextFocus(const HostTextFocus(active: true, serial: 1));
      await _settle(tester);
      expect(host.keyboard, KeyboardOverlayMode.systemIme);

      session.emitTextFocus(const HostTextFocus(serial: 2));
      await _settle(tester);
      expect(host.keyboard, KeyboardOverlayMode.none);

      // Hopping fields passes through nothing for a moment.
      session.emitTextFocus(const HostTextFocus(active: true, serial: 3));
      await _settle(tester);
      session.emitTextFocus(const HostTextFocus(serial: 4));
      await tester.pump(const Duration(milliseconds: 150));
      session.emitTextFocus(const HostTextFocus(active: true, serial: 5));
      await _settle(tester);
      expect(host.keyboard, KeyboardOverlayMode.systemIme);

      // Collapsed by hand, the same field stays collapsed; the next one asks.
      await tester.tap(find.text('收起'));
      await _settle(tester);
      expect(host.keyboard, KeyboardOverlayMode.none);
      session.emitTextFocus(
        const HostTextFocus(
          active: true,
          serial: 5,
          caret: Rect.fromLTWH(0.2, 0.4, 0.001, 0.02),
        ),
      );
      await _settle(tester);
      expect(host.keyboard, KeyboardOverlayMode.none);
      session.emitTextFocus(const HostTextFocus(active: true, serial: 6));
      await _settle(tester);
      expect(host.keyboard, KeyboardOverlayMode.systemIme);

      // Once the user switches keyboards, it is theirs to close.
      await tester.drag(find.text('Ctrl'), const Offset(-600, 0));
      await _settle(tester);
      await tester.tap(find.text('PC键盘'));
      await _settle(tester);
      session.emitTextFocus(const HostTextFocus(serial: 7));
      await _settle(tester);
      expect(host.keyboard, KeyboardOverlayMode.pcFull);
    });

    testWidgets('the picture lifts the host caret above the keyboard', (
      tester,
    ) async {
      final session = FakeSession()
        ..emitPhase(SessionPhase.streaming)
        ..emitFirstFrame();
      final host = await _mount(tester, session);
      tester.view.physicalSize = const Size(873, 393) * 3.0;
      await _settle(tester);
      final layer = tester.state<TouchControlLayerState>(
        find.byType(TouchControlLayer),
      );
      final keyboardTop = 393.0 - 200;

      session.emitTextFocus(
        const HostTextFocus(
          active: true,
          serial: 1,
          caret: Rect.fromLTWH(0.3, 0.86, 0.001, 0.04),
        ),
      );
      tester.view.viewInsets = const FakeViewPadding(bottom: 200 * 3);
      await _settle(tester);
      expect(host.keyboard, KeyboardOverlayMode.systemIme);
      final barTop = tester.getTopLeft(find.text('Esc')).dy - 6;
      expect(barTop, lessThan(keyboardTop));
      final lift = layer.debugLift;
      expect(lift, greaterThan(0));

      // The caret sits clear of the bar, and a tap on it still lands on it.
      final caretBottom = 0.9 * 393 - lift;
      expect(caretBottom, lessThanOrEqualTo(barTop));
      await tester.tapAt(Offset(873 / 2, 0.88 * 393 - lift));
      await tester.pump();
      expect(session.moves.last.dy, moreOrLessEquals(0.88, epsilon: 0.01));

      // A caret already in the clear leaves the picture where it is.
      session.emitTextFocus(
        const HostTextFocus(
          active: true,
          serial: 1,
          caret: Rect.fromLTWH(0.3, 0.1, 0.001, 0.04),
        ),
      );
      await _settle(tester);
      expect(layer.debugLift, 0);

      session.emitTextFocus(
        const HostTextFocus(
          active: true,
          serial: 1,
          caret: Rect.fromLTWH(0.3, 0.86, 0.001, 0.04),
        ),
      );
      await _settle(tester);
      expect(layer.debugLift, moreOrLessEquals(lift));

      tester.view.resetViewInsets();
      host.setKeyboard(KeyboardOverlayMode.none);
      await _settle(tester);
      expect(layer.debugLift, 0);
    });

    testWidgets('the OSD starts bottom-right and drags freely', (tester) async {
      final session = FakeSession()
        ..emitPhase(SessionPhase.streaming)
        ..emitFirstFrame();
      await _mount(tester, session);

      final osd = find.textContaining('FPS');
      final start = tester.getBottomRight(osd);
      expect(start.dx, greaterThan(393 - 40));
      expect(start.dy, greaterThan(873 - 60));

      await tester.drag(osd, const Offset(-200, -300));
      await tester.pump();
      final moved = tester.getBottomRight(osd);
      expect(moved.dx, lessThan(start.dx - 150));
      expect(moved.dy, lessThan(start.dy - 250));
    });
  });

  group('S3 control panel', () {
    testWidgets('opens, animates and closes back to nothing', (tester) async {
      final session = FakeSession()
        ..emitPhase(SessionPhase.streaming)
        ..emitFirstFrame();
      final host = await _mount(tester, session);
      expect(find.text('操控'), findsNothing);

      host.setPanel(true);
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 32));
      expect(find.text('操控'), findsOneWidget);

      await _settle(tester);
      expect(find.text('操控'), findsOneWidget);

      // Back button parity: the page flips panelOpen, the sheet springs away.
      host.setPanel(false);
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
      expect(find.text('操控'), findsOneWidget);

      await _settle(tester);
      expect(find.text('操控'), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets('scrim tap and drag-down both dismiss', (tester) async {
      final session = FakeSession()
        ..emitPhase(SessionPhase.streaming)
        ..emitFirstFrame();
      final host = await _mount(tester, session);

      host.setPanel(true);
      await _settle(tester);
      await tester.tapAt(const Offset(12, 12));
      await _settle(tester);
      expect(host.closeRequests, 1);
      expect(find.text('操控'), findsNothing);

      host.setPanel(true);
      await _settle(tester);
      await tester.drag(
        find.descendant(
          of: find.byType(MiuixGlassPanel),
          matching: find.text('Studio Desktop'),
        ),
        const Offset(0, 320),
      );
      await _settle(tester);
      expect(host.closeRequests, 2);
      expect(find.text('操控'), findsNothing);
    });

    testWidgets('reduced motion shows and hides the sheet immediately', (
      tester,
    ) async {
      final session = FakeSession()..emitPhase(SessionPhase.streaming);
      final host = await _mount(tester, session, disableAnimations: true);

      host.setPanel(true);
      await tester.pump();
      expect(find.text('操控'), findsOneWidget);

      host.setPanel(false);
      await tester.pump();
      expect(find.text('操控'), findsNothing);
    });
  });

  group('S3 toast', () {
    testWidgets('slides in, then clears itself after the two second timer', (
      tester,
    ) async {
      final session = FakeSession()
        ..emitPhase(SessionPhase.streaming)
        ..emitFirstFrame();
      await _mount(tester, session);

      session.emitToast('已切换控制模式');
      await _settle(tester, 4);
      expect(find.text('已切换控制模式'), findsOneWidget);

      await _settle(tester, 30);
      expect(find.text('已切换控制模式'), findsNothing);
      expect(session.toastMessage, isNull);
    });
  });

  group('S1 precision cursor', () {
    testWidgets('moving the virtual cursor repaints without rebuilding', (
      tester,
    ) async {
      final session = FakeSession()
        ..emitPhase(SessionPhase.streaming)
        ..emitFirstFrame();
      final layer = await _mountLayer(
        tester,
        session,
        mode: ControlMode.precisionMouse,
      );
      final buildsBefore = layer.debugBuildCount;

      final gesture = await tester.startGesture(const Offset(200, 500));
      await gesture.moveBy(const Offset(24, 18));
      await gesture.moveBy(const Offset(24, 18));
      await gesture.moveBy(const Offset(24, 18));
      await gesture.up();
      await tester.pump(const Duration(milliseconds: 50));

      expect(layer.debugBuildCount, buildsBefore);
      expect(tester.takeException(), isNull);
    });
  });

  group('S6 haptics', () {
    testWidgets('only the hold vibrates; taps and drags stay silent', (
      tester,
    ) async {
      final calls = <String>[];
      tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
        SystemChannels.platform,
        (call) async {
          if (call.method == 'HapticFeedback.vibrate') {
            calls.add(call.arguments as String);
          }
          return null;
        },
      );
      addTearDown(() {
        tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
          SystemChannels.platform,
          null,
        );
        OsmiumHaptics.enabled = true;
      });

      OsmiumHaptics.enabled = true;
      final session = FakeSession()
        ..emitPhase(SessionPhase.streaming)
        ..emitFirstFrame();
      await _mountLayer(tester, session, enableHaptics: true);

      await tester.tapAt(const Offset(200, 400));
      await tester.pump();
      expect(calls, isEmpty);

      final longPress = await tester.startGesture(const Offset(200, 300));
      await tester.pump(const Duration(milliseconds: 400));
      await longPress.up();
      await tester.pump();
      expect(calls, ['HapticFeedbackType.mediumImpact']);

      calls.clear();
      final drag = await tester.startGesture(const Offset(200, 300));
      await tester.pump(const Duration(milliseconds: 400));
      await drag.moveBy(const Offset(30, 0));
      await drag.moveBy(const Offset(30, 0));
      await drag.up();
      await tester.pump();
      expect(calls, ['HapticFeedbackType.mediumImpact']);
    });
  });

  group('S7 touch gestures', () {
    Future<FakeSession> mounted(WidgetTester tester) async {
      final session = FakeSession()
        ..emitPhase(SessionPhase.streaming)
        ..emitFirstFrame();
      await _mountLayer(tester, session);
      return session;
    }

    Future<void> pinchOut(WidgetTester tester) async {
      final a = await tester.startGesture(const Offset(150, 436));
      final b = await tester.startGesture(const Offset(250, 436));
      for (var i = 0; i < 6; i++) {
        await a.moveBy(const Offset(-10, 0));
        await b.moveBy(const Offset(10, 0));
      }
      await a.up();
      await b.up();
      await tester.pump();
    }

    testWidgets('a tap left-clicks', (tester) async {
      final session = await mounted(tester);
      await tester.tapAt(const Offset(200, 436));
      await tester.pump();
      expect(session.input, ['click1']);
    });

    testWidgets('one finger pans the zoomed picture without pressing', (
      tester,
    ) async {
      final session = await mounted(tester);
      final layer = tester.state<TouchControlLayerState>(
        find.byType(TouchControlLayer),
      );

      await pinchOut(tester);
      expect(layer.debugZoom, greaterThan(1.5));
      expect(session.input, isEmpty);

      final before = layer.debugPan;
      final drag = await tester.startGesture(const Offset(200, 436));
      await drag.moveBy(const Offset(20, 0));
      await drag.moveBy(const Offset(20, 0));
      await drag.up();
      await tester.pump();

      expect(layer.debugPan.dx, greaterThan(before.dx + 30));
      expect(session.input, isEmpty);
    });

    testWidgets('hold then lift right-clicks', (tester) async {
      final session = await mounted(tester);
      final hold = await tester.startGesture(const Offset(200, 436));
      await tester.pump(const Duration(milliseconds: 400));
      await hold.up();
      await tester.pump();
      // 3 is the protocol's right button; 2 would be a middle click.
      expect(session.input, ['click3']);
    });

    testWidgets('hold then move drags with the left button', (tester) async {
      final session = await mounted(tester);
      final hold = await tester.startGesture(const Offset(200, 436));
      await tester.pump(const Duration(milliseconds: 400));
      await hold.moveBy(const Offset(30, 0));
      expect(session.input, ['down1']);
      await hold.moveBy(const Offset(30, 0));
      await hold.up();
      await tester.pump();
      expect(session.input, ['down1', 'up1']);
    });

    testWidgets('a quick slide before the hold never presses', (tester) async {
      final session = await mounted(tester);
      final slide = await tester.startGesture(const Offset(200, 436));
      await slide.moveBy(const Offset(30, 0));
      await tester.pump(const Duration(milliseconds: 400));
      await slide.moveBy(const Offset(30, 0));
      await slide.up();
      await tester.pump();
      expect(session.input, isEmpty);
    });

    testWidgets('a two-finger tap right-clicks', (tester) async {
      final session = await mounted(tester);
      final a = await tester.startGesture(const Offset(150, 436));
      final b = await tester.startGesture(const Offset(250, 436));
      await tester.pump(const Duration(milliseconds: 60));
      await a.up();
      await b.up();
      await tester.pump();
      expect(session.input, ['click3']);
    });

    testWidgets('a two-finger tap survives finger jitter', (tester) async {
      final session = await mounted(tester);
      final a = await tester.startGesture(const Offset(150, 436));
      final b = await tester.startGesture(const Offset(250, 436));
      await a.moveBy(const Offset(-6, 4));
      await b.moveBy(const Offset(5, 6));
      await tester.pump(const Duration(milliseconds: 220));
      await a.up();
      await b.up();
      await tester.pump();
      expect(session.input, ['click3']);
    });

    testWidgets('a second tap nearby lands on the first tap\'s pixel', (
      tester,
    ) async {
      final session = await mounted(tester);
      await tester.tapAt(const Offset(200, 436));
      await tester.pump(const Duration(milliseconds: 150));
      await tester.tapAt(const Offset(207, 441));
      await tester.pump();
      expect(session.input, ['click1', 'click1']);
      expect(session.moves[1], session.moves[0]);

      // Too late or too far: the tap goes where the finger is.
      await tester.pump(const Duration(milliseconds: 600));
      await tester.tapAt(const Offset(207, 441));
      await tester.pump(const Duration(milliseconds: 150));
      await tester.tapAt(const Offset(300, 441));
      await tester.pump();
      expect(session.moves[2], isNot(session.moves[0]));
      expect(session.moves[3], isNot(session.moves[2]));
    });

    testWidgets('the precision dock right button right-clicks', (tester) async {
      final session = FakeSession()
        ..emitPhase(SessionPhase.streaming)
        ..emitFirstFrame();
      await _mountLayer(tester, session, mode: ControlMode.precisionMouse);
      await tester.tap(find.text('右键'));
      await tester.pump();
      await tester.tap(find.text('左键'));
      await tester.pump();
      expect(session.input, ['click3', 'click1']);
    });

    testWidgets('two fingers sliding together scroll', (tester) async {
      final session = await mounted(tester);
      final a = await tester.startGesture(const Offset(150, 436));
      final b = await tester.startGesture(const Offset(250, 436));
      for (var i = 0; i < 6; i++) {
        await a.moveBy(const Offset(0, 10));
        await b.moveBy(const Offset(0, 10));
      }
      await a.up();
      await b.up();
      await tester.pump();
      expect(session.input, isNotEmpty);
      expect(session.input.every((e) => e == 'wheel'), isTrue);
    });
  });
}
