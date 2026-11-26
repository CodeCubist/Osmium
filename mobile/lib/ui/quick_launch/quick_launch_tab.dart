import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_miuix/miuix.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/account_models.dart';
import '../../core/theme/osmium_motion.dart';
import '../../core/theme/osmium_theme.dart';
import '../../services/session/remote_session_controller.dart';
import '../../state/app_controller.dart';
import '../auth/login_page.dart';
import '../shared/home_widgets.dart';
import '../shared/session_launcher.dart';
import '../widgets/osmium_dock_scope.dart';

class QuickLaunchTab extends ConsumerStatefulWidget {
  const QuickLaunchTab({
    super.key,
    required this.onConnect,
    required this.bottomInset,
  });

  final SessionLauncher onConnect;
  final double bottomInset;

  @override
  ConsumerState<QuickLaunchTab> createState() => _QuickLaunchTabState();
}

class _QuickLaunchTabState extends ConsumerState<QuickLaunchTab> {
  final Map<String, GlobalKey> _tileKeys = {};
  bool _scrolled = false;

  bool _launchDialogVisible = false;
  String _launchTitle = '';
  String _launchStage = '';
  bool _launchError = false;
  ValueNotifier<bool>? _cancelToken;
  ValueNotifier<bool>? _dockHidden;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _dockHidden = OsmiumDockScope.maybeOf(context);
  }

  @override
  void dispose() {
    _cancelToken?.value = true;
    _cancelToken?.dispose();
    _dockHidden?.value = false;
    super.dispose();
  }

  GlobalKey _tileKey(String id) => _tileKeys.putIfAbsent(id, GlobalKey.new);

  void _syncDock() {
    _dockHidden?.value = _launchDialogVisible;
  }

  Future<void> _openLogin() async {
    await Navigator.of(context).push<bool>(
      MaterialPageRoute(builder: (_) => const LoginPage()),
    );
  }

  void _dismissLaunchDialog() {
    if (!_launchDialogVisible) return;
    if (!_launchError) {
      _cancelToken?.value = true;
    }
    setState(() => _launchDialogVisible = false);
    _syncDock();
  }

  Future<void> _handleAppTap(QuickLaunchAppDto app) async {
    if (_launchDialogVisible) return;
    OsmiumHaptics.selection();

    _cancelToken?.dispose();
    final cancel = ValueNotifier<bool>(false);
    _cancelToken = cancel;

    setState(() {
      _launchTitle = '正在启动${app.name}';
      _launchStage = '正在校验目标设备在线状态、权限与程序目录…';
      _launchError = false;
      _launchDialogVisible = true;
    });
    _syncDock();

    final result = await ref
        .read(appControllerProvider)
        .launchQuickApp(
          app,
          onStage: (stage) {
            if (mounted && _launchDialogVisible && !_launchError) {
              setState(() => _launchStage = stage);
            }
          },
          cancelled: cancel,
        );

    if (!mounted) return;
    if (result.cancelled) {
      setState(() => _launchDialogVisible = false);
      _syncDock();
      return;
    }

    if (!result.ok) {
      setState(() {
        _launchTitle = '无法启动';
        _launchStage = result.message;
        _launchError = true;
      });
      return;
    }

    setState(() => _launchDialogVisible = false);
    _syncDock();
    OsmiumHaptics.confirm();

    widget.onConnect(
      SessionConnectRequest(
        targetDeviceId: app.deviceId,
        targetDeviceName: result.deviceName,
        sameAccount: true,
        viewOnly: false,
      ),
      sourceKey: _tileKey(app.id),
    );
  }

  @override
  Widget build(BuildContext context) {
    final app = ref.watch(appControllerProvider);
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;
    final topPad = MediaQuery.paddingOf(context).top;
    final headerHeight = topPad + OsmiumTopHeader.barHeight;
    final apps = app.quickLaunchApps;

    return MiuixScaffold(
      content: (_) {
        return Stack(
          children: [
            NotificationListener<ScrollNotification>(
              onNotification: (n) {
                if (n.metrics.axis == Axis.vertical) {
                  final next = n.metrics.pixels > 4;
                  if (next != _scrolled) {
                    setState(() => _scrolled = next);
                  }
                }
                return false;
              },
              child: MiuixPullToRefresh(
                isRefreshing: app.refreshingQuickLaunch,
                onRefresh: () async {
                  if (app.isLoggedIn) {
                    await Future.wait([
                      app.refreshAccountDevices(),
                      app.refreshQuickLaunchApps(),
                    ]);
                  }
                },
                color: colors.primary,
                contentPadding: EdgeInsets.only(top: headerHeight + 14),
                refreshTexts: const ['下拉刷新', '松开刷新', '刷新中…', '已刷新'],
                child: Center(
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(maxWidth: 720),
                    child: CustomScrollView(
                      physics: const AlwaysScrollableScrollPhysics(
                        parent: BouncingScrollPhysics(),
                      ),
                      slivers: [
                        SliverPadding(
                          padding: EdgeInsets.fromLTRB(
                            16,
                            headerHeight + 10,
                            16,
                            widget.bottomInset + 24,
                          ),
                          sliver: !app.isLoggedIn
                              ? SliverToBoxAdapter(
                                  child: _EmptyPromptCard(
                                    icon: Icons.apps_rounded,
                                    title: '登录账号以使用快速启动',
                                    subtitle:
                                        '登录同一账户后，即可一键启动已在电脑端添加的游戏或程序，窗口就绪后自动进入远程控制。',
                                    actionLabel: '前往登录',
                                    onAction: _openLogin,
                                  ),
                                )
                              : apps.isEmpty && app.refreshingQuickLaunch
                              ? SliverToBoxAdapter(
                                  child: Padding(
                                    padding: const EdgeInsets.only(top: 72),
                                    child: Center(
                                      child: SizedBox(
                                        width: 28,
                                        height: 28,
                                        child: CircularProgressIndicator(
                                          strokeWidth: 2.5,
                                          color: colors.primary,
                                        ),
                                      ),
                                    ),
                                  ),
                                )
                              : apps.isEmpty
                              ? SliverToBoxAdapter(
                                  child: _EmptyPromptCard(
                                    icon: Icons.rocket_launch_rounded,
                                    title: '暂无快速启动程序',
                                    subtitle:
                                        '请在电脑端「快速启动」页面中选择已开启该功能的计算机并添加程序，添加后将自动同步到这里。',
                                    actionLabel: '刷新列表',
                                    onAction: () {
                                      unawaited(app.refreshQuickLaunchApps());
                                    },
                                  ),
                                )
                              : SliverLayoutBuilder(
                                  builder: (context, constraints) {
                                    final w = constraints.crossAxisExtent;
                                    final cols = w >= 580
                                        ? 6
                                        : (w >= 430 ? 5 : 4);
                                    return SliverGrid(
                                      gridDelegate:
                                          SliverGridDelegateWithFixedCrossAxisCount(
                                            crossAxisCount: cols,
                                            mainAxisSpacing: 18,
                                            crossAxisSpacing: 10,
                                            childAspectRatio: 0.72,
                                          ),
                                      delegate: SliverChildBuilderDelegate((
                                        context,
                                        index,
                                      ) {
                                        final item = apps[index];
                                        final liveDev = app.findAccountDevice(
                                          item.deviceId,
                                        );
                                        final isCached =
                                            liveDev != null ||
                                            (app.refreshingDevices &&
                                                item.deviceCached);
                                        final online =
                                            liveDev?.isOnline ??
                                            (isCached && item.deviceOnline);
                                        final enabled =
                                            liveDev?.allowQuickLaunch ??
                                            (isCached && item.allowQuickLaunch);
                                        final allowRemote =
                                            liveDev?.allowRemote ??
                                            (isCached && item.allowRemote);
                                        final busy =
                                            liveDev?.isBeingControlled ?? false;
                                        final deviceName =
                                            liveDev?.displayName ??
                                            item.deviceName;
                                        return _LauncherIconTile(
                                          key: _tileKey(item.id),
                                          app: item,
                                          deviceName: deviceName,
                                          isCached: isCached,
                                          isOnline: online,
                                          isEnabled: enabled,
                                          allowRemote: allowRemote,
                                          isBusy: busy,
                                          isDark: isDark,
                                          onTap: () => _handleAppTap(item),
                                        );
                                      }, childCount: apps.length),
                                    );
                                  },
                                ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            ),
            Positioned(
              top: 0,
              left: 0,
              right: 0,
              child: OsmiumTopHeader(
                title: '快速启动',
                scrolled: _scrolled,
                actions: [
                  if (app.isLoggedIn)
                    IconButton(
                      onPressed: app.refreshingQuickLaunch
                          ? null
                          : () {
                              OsmiumHaptics.selection();
                              unawaited(app.refreshAccountDevices());
                              unawaited(app.refreshQuickLaunchApps());
                            },
                      icon: Icon(
                        Icons.refresh_rounded,
                        color: colors.onSurface,
                        size: 22,
                      ),
                    ),
                ],
              ),
            ),
            MiuixOverlayDialog(
              show: _launchDialogVisible,
              title: _launchTitle,
              onDismissRequest: _dismissLaunchDialog,
              content: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Row(
                    crossAxisAlignment: CrossAxisAlignment.center,
                    children: [
                      if (!_launchError) ...[
                        SizedBox(
                          width: 20,
                          height: 20,
                          child: CircularProgressIndicator(
                            strokeWidth: 2.3,
                            color: colors.primary,
                          ),
                        ),
                        const SizedBox(width: 12),
                      ],
                      Expanded(
                        child: Text(
                          _launchStage,
                          style: TextStyle(
                            fontFamily: OsmiumTheme.fontFamily,
                            fontSize: 14,
                            height: 1.45,
                            color: colors.onSurface.withValues(alpha: 0.84),
                          ),
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 18),
                  MiuixTextButton(
                    _launchError ? '知道了' : '取消',
                    onPressed: _dismissLaunchDialog,
                    colors: _launchError
                        ? MiuixButtonDefaults.buttonColorsPrimary(context)
                        : null,
                  ),
                ],
              ),
            ),
          ],
        );
      },
    );
  }
}

class _LauncherIconTile extends StatelessWidget {
  const _LauncherIconTile({
    super.key,
    required this.app,
    required this.deviceName,
    required this.isCached,
    required this.isOnline,
    required this.isEnabled,
    required this.allowRemote,
    required this.isBusy,
    required this.isDark,
    required this.onTap,
  });

  final QuickLaunchAppDto app;
  final String deviceName;
  final bool isCached;
  final bool isOnline;
  final bool isEnabled;
  final bool allowRemote;
  final bool isBusy;
  final bool isDark;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final colors = MiuixTheme.of(context).colors;
    final iconBytes = decodeAvatarDataUri(app.iconData);
    final isCacheLost = !isOnline && !isCached;
    final ready = isOnline && isEnabled && allowRemote && !isBusy;

    final Color dotColor;
    if (!isOnline || !isEnabled) {
      dotColor = const Color(0xFF8E8E93);
    } else if (isBusy) {
      dotColor = const Color(0xFFF59E0B);
    } else {
      dotColor = const Color(0xFF22C55E);
    }

    Widget iconVisual = iconBytes != null
        ? ClipRRect(
            borderRadius: BorderRadius.circular(10),
            child: Image.memory(
              iconBytes,
              width: 40,
              height: 40,
              fit: BoxFit.contain,
              gaplessPlayback: true,
              errorBuilder: (context, error, stackTrace) => Icon(
                Icons.apps_rounded,
                size: 28,
                color: isCacheLost
                    ? colors.onSurface.withValues(alpha: 0.72)
                    : colors.primary,
              ),
            ),
          )
        : Icon(
            Icons.apps_rounded,
            size: 28,
            color: isCacheLost
                ? colors.onSurface.withValues(alpha: 0.72)
                : colors.primary,
          );

    if (isCacheLost) {
      iconVisual = ColorFiltered(
        colorFilter: OsmiumColors.grayscaleFilter,
        child: iconVisual,
      );
    }

    return MiuixPressable(
      onPressed: onTap,
      borderRadius: BorderRadius.circular(18),
      feedbackType: MiuixPressFeedbackType.sink,
      sinkAmount: 0.93,
      child: Opacity(
        opacity: isCacheLost ? 1.0 : (ready ? 1.0 : 0.66),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            SizedBox(
              width: 60,
              height: 60,
              child: Stack(
                clipBehavior: Clip.none,
                children: [
                  Container(
                    width: 60,
                    height: 60,
                    decoration: BoxDecoration(
                      color: isDark
                          ? const Color(0xFF2B2B2E)
                          : const Color(0xFFFFFFFF),
                      borderRadius: BorderRadius.circular(16),
                      border: Border.all(
                        color: OsmiumColors.cardBorder(isDark),
                        width: 0.9,
                      ),
                      boxShadow: [
                        BoxShadow(
                          color: OsmiumColors.cardShadow(isDark),
                          blurRadius: 10,
                          offset: const Offset(0, 3),
                        ),
                      ],
                    ),
                    alignment: Alignment.center,
                    child: iconVisual,
                  ),
                  Positioned(
                    right: -2,
                    bottom: -2,
                    child: Container(
                      width: 13,
                      height: 13,
                      decoration: BoxDecoration(
                        color: dotColor,
                        shape: BoxShape.circle,
                        border: Border.all(
                          color: colors.background,
                          width: 2,
                        ),
                      ),
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(height: 7),
            Text(
              app.name,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              textAlign: TextAlign.center,
              style: TextStyle(
                fontFamily: OsmiumTheme.fontFamily,
                fontSize: 12,
                fontWeight: FontWeight.w500,
                height: 1.25,
                color: colors.onSurface,
              ),
            ),
            const SizedBox(height: 2),
            Text(
              deviceName,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              textAlign: TextAlign.center,
              style: TextStyle(
                fontFamily: OsmiumTheme.fontFamily,
                fontSize: 10,
                height: 1.2,
                color: colors.onSurface.withValues(alpha: 0.54),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _EmptyPromptCard extends StatelessWidget {
  const _EmptyPromptCard({
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.actionLabel,
    required this.onAction,
  });

  final IconData icon;
  final String title;
  final String subtitle;
  final String actionLabel;
  final VoidCallback onAction;

  @override
  Widget build(BuildContext context) {
    final colors = MiuixTheme.of(context).colors;
    return OsmiumSurfaceCard(
      padding: const EdgeInsets.symmetric(horizontal: 22, vertical: 28),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Container(
            width: 52,
            height: 52,
            decoration: BoxDecoration(
              color: colors.primary.withValues(alpha: 0.12),
              borderRadius: BorderRadius.circular(16),
            ),
            child: Icon(icon, size: 26, color: colors.primary),
          ),
          const SizedBox(height: 14),
          Text(
            title,
            textAlign: TextAlign.center,
            style: TextStyle(
              fontFamily: OsmiumTheme.fontFamily,
              fontSize: 16,
              fontWeight: FontWeight.w600,
              color: colors.onSurface,
            ),
          ),
          const SizedBox(height: 6),
          Text(
            subtitle,
            textAlign: TextAlign.center,
            style: TextStyle(
              fontFamily: OsmiumTheme.fontFamily,
              fontSize: 13,
              height: 1.45,
              color: colors.onSurface.withValues(alpha: 0.64),
            ),
          ),
          const SizedBox(height: 18),
          MiuixTextButton(
            actionLabel,
            onPressed: onAction,
            colors: MiuixButtonDefaults.buttonColorsPrimary(context),
          ),
        ],
      ),
    );
  }
}
