import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_miuix/miuix.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/account_models.dart';
import '../../core/theme/osmium_motion.dart';
import '../../core/theme/osmium_theme.dart';
import '../../services/session/remote_session_controller.dart';
import '../../services/signaling/signaling_client.dart';
import '../../state/app_controller.dart';
import '../auth/login_page.dart';
import '../auth/qr_login_confirm_page.dart';
import '../connect/connect_tab.dart';
import '../devices/devices_tab.dart';
import '../me/me_tab.dart';
import '../session/remote_session_page.dart';
import '../widgets/osmium_container_route.dart';
import '../widgets/osmium_dock_scope.dart';
import '../widgets/osmium_tab_switcher.dart';

const double _dockHeight = 52;
const double _dockBottomGap = 12;

class HomeShell extends ConsumerStatefulWidget {
  const HomeShell({super.key});

  @override
  ConsumerState<HomeShell> createState() => _HomeShellState();
}

class _HomeShellState extends ConsumerState<HomeShell> {
  int _tabIndex = 0;
  final ValueNotifier<bool> _dockHidden = ValueNotifier(false);
  StreamSubscription<void>? _unreachableSub;
  StreamSubscription<String>? _deepLinkSub;
  bool _unreachableVisible = false;
  bool _handlingDeepLink = false;

  @override
  void dispose() {
    _unreachableSub?.cancel();
    _deepLinkSub?.cancel();
    _dockHidden.dispose();
    super.dispose();
  }

  @override
  void initState() {
    super.initState();
    final app = ref.read(appControllerProvider);
    final bridge = ref.read(platformBridgeProvider);
    _unreachableSub = app.serverUnreachable.listen((_) {
      if (mounted) setState(() => _unreachableVisible = true);
    });
    _deepLinkSub = bridge.deepLinks.listen((link) {
      unawaited(_handleExternalDeepLink(link));
    });
    WidgetsBinding.instance.addPostFrameCallback((_) async {
      await app.initialize();
      final initialLink = await bridge.getInitialDeepLink();
      if (initialLink != null && mounted) {
        await _handleExternalDeepLink(initialLink);
      }
    });
  }

  Future<void> _handleExternalDeepLink(String rawLink) async {
    if (_handlingDeepLink || !mounted) return;
    final parsed = QrParsedRequest.tryParseExternalDeepLink(rawLink);
    if (parsed == null) return;

    _handlingDeepLink = true;
    try {
      final app = ref.read(appControllerProvider);
      if (!app.initialized) {
        await app.initialize();
      }
      if (!mounted) return;

      if (!app.isLoggedIn) {
        final loggedIn = await Navigator.of(context).push<bool>(
          MaterialPageRoute(builder: (_) => const LoginPage()),
        );
        if (loggedIn != true || !mounted || !app.isLoggedIn) return;
      }

      await Navigator.of(context).push<bool>(
        MaterialPageRoute(
          builder: (_) => QrLoginConfirmPage(request: parsed),
        ),
      );
    } finally {
      _handlingDeepLink = false;
    }
  }

  void _dismissUnreachable() {
    if (!_unreachableVisible) return;
    setState(() => _unreachableVisible = false);
  }

  void _retrySignaling() {
    _dismissUnreachable();
    ref.read(appControllerProvider).retrySignaling();
  }

  Future<void> _openSession(
    SessionConnectRequest request, {
    GlobalKey? sourceKey,
  }) async {
    OsmiumHaptics.confirm();
    await Navigator.of(context).push<void>(
      OsmiumContainerRoute(
        sourceKey: sourceKey,
        pageBuilder: (_) => RemoteSessionPage(request: request),
      ),
    );
  }

  void _selectTab(int index) {
    if (index == _tabIndex) return;
    OsmiumHaptics.selection();
    setState(() => _tabIndex = index);
  }

  void _openConnectTab() => _selectTab(1);
  void _openMeTab() => _selectTab(2);

  @override
  Widget build(BuildContext context) {
    final media = MediaQuery.of(context);
    final bottomPad = media.padding.bottom;
    final navBarReserve = bottomPad + _dockBottomGap + _dockHeight + 16;
    final keyboardOpen = media.viewInsets.bottom > 0;
    final colors = MiuixTheme.of(context).colors;

    // The server came back on its own while the alert was up.
    ref.listen(appControllerProvider.select((c) => c.signalingState), (
      _,
      next,
    ) {
      if (next == SignalingConnectionState.connected) _dismissUnreachable();
    });

    // Root scaffold: paints the solid page colour under the tab fade-through
    // (so a switch only fades page content, never dips to the black window)
    // and hosts root popups above the dock.
    return MiuixScaffold(
      content: (_) => OsmiumDockScope(
        hidden: _dockHidden,
        child: Stack(
          children: [
            OsmiumTabSwitcher(
              index: _tabIndex,
              children: [
                DevicesTab(
                  onConnect: _openSession,
                  onOpenConnect: _openConnectTab,
                  onOpenMe: _openMeTab,
                  bottomInset: navBarReserve,
                ),
                ConnectTab(onConnect: _openSession, bottomInset: navBarReserve),
                MeTab(bottomInset: navBarReserve),
              ],
            ),
            Positioned(
              left: 20,
              right: 20,
              bottom: bottomPad + _dockBottomGap,
              child: ValueListenableBuilder<bool>(
                valueListenable: _dockHidden,
                child: Center(
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(maxWidth: 380),
                    child: ClipRRect(
                      borderRadius: BorderRadius.circular(_dockHeight / 2),
                      child: BackdropFilter(
                        filter: OsmiumGlass.filter(24),
                        child: DecoratedBox(
                          decoration: BoxDecoration(
                            borderRadius: BorderRadius.circular(_dockHeight / 2),
                            border: Border.all(
                              color: OsmiumColors.glassBorder(
                                MiuixTheme.of(context).brightness ==
                                    Brightness.dark,
                              ),
                              width: 0.9,
                            ),
                          ),
                          child: MiuixGlassNavigationBar(
                            selectedIndex: _tabIndex,
                            height: _dockHeight,
                            alpha: 0.54,
                            shape: const MiuixGlassShape(
                              cornerRadius: _dockHeight / 2,
                              smoothing: 0.25,
                            ),
                            selectedColor: colors.primary,
                            indicatorColor: colors.primary.withValues(
                              alpha: 0.14,
                            ),
                            indicatorPressedColor: colors.primary.withValues(
                              alpha: 0.20,
                            ),
                            unselectedColor: colors.onSurface.withValues(
                              alpha: 0.56,
                            ),
                            onSelect: _selectTab,
                            items: const [
                              MiuixGlassNavigationItem(
                                icon: Icon(Icons.devices_rounded),
                                label: '设备',
                              ),
                              MiuixGlassNavigationItem(
                                icon: Icon(Icons.bolt_rounded),
                                label: '连接',
                              ),
                              MiuixGlassNavigationItem(
                                icon: Icon(Icons.person_rounded),
                                label: '我的',
                              ),
                            ],
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
                builder: (context, hidden, child) {
                  final away = keyboardOpen || hidden;
                  return IgnorePointer(
                    ignoring: away,
                    child: AnimatedSlide(
                      offset: away ? const Offset(0, 1.45) : Offset.zero,
                      duration: OsmiumMotion.durationOr(
                        context,
                        const Duration(milliseconds: 240),
                      ),
                      curve: MiuixMotion.standardDecelerate,
                      child: AnimatedOpacity(
                        opacity: away ? 0 : 1,
                        duration: OsmiumMotion.durationOr(
                          context,
                          const Duration(milliseconds: 180),
                        ),
                        child: child,
                      ),
                    ),
                  );
                },
              ),
            ),
            MiuixOverlayDialog(
              show: _unreachableVisible,
              title: '无法连接服务器',
              onDismissRequest: _dismissUnreachable,
              content: Row(
                children: [
                  Expanded(
                    child: MiuixTextButton(
                      '取消',
                      onPressed: _dismissUnreachable,
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: MiuixTextButton(
                      '重试',
                      onPressed: _retrySignaling,
                      colors: MiuixButtonDefaults.buttonColorsPrimary(context),
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}
