import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_miuix/miuix.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/account_models.dart';
import '../../core/models/device_identity.dart';
import '../../core/theme/osmium_motion.dart';
import '../../core/theme/osmium_theme.dart';
import '../../services/session/remote_session_controller.dart';
import '../../state/app_controller.dart';
import '../auth/login_page.dart';
import '../auth/qr_scanner_page.dart';
import '../shared/home_widgets.dart';
import '../shared/session_launcher.dart';
import '../widgets/osmium_dock_scope.dart';
import '../widgets/osmium_motion_widgets.dart';

class DevicesTab extends ConsumerStatefulWidget {
  const DevicesTab({
    super.key,
    required this.onConnect,
    required this.onOpenConnect,
    required this.bottomInset,
  });

  final SessionLauncher onConnect;
  final VoidCallback onOpenConnect;
  final double bottomInset;

  @override
  ConsumerState<DevicesTab> createState() => _DevicesTabState();
}

class _DevicesTabState extends ConsumerState<DevicesTab> {
  final _scrollBehavior = MiuixExitUntilCollapsedScrollBehavior();
  final Map<String, GlobalKey> _cardKeys = {};

  // Declarative Miuix overlays: visibility is driven by state so the enter and
  // exit springs (and drag-to-dismiss) run through the library.
  AccountDeviceDto? _sheetDevice;
  bool _sheetVisible = false;

  AccountDeviceDto? _renameDevice;
  bool _renameVisible = false;
  late final TextEditingController _renameCtrl = TextEditingController();

  String? _removingDeviceId;
  ValueNotifier<bool>? _dockHidden;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _dockHidden = OsmiumDockScope.maybeOf(context);
  }

  @override
  void dispose() {
    _dockHidden?.value = false;
    _renameCtrl.dispose();
    super.dispose();
  }

  /// Keeps the floating dock out of the modal's way while either overlay is up.
  void _syncDock() {
    _dockHidden?.value = _sheetVisible || _renameVisible;
  }

  GlobalKey _cardKey(String deviceId) =>
      _cardKeys.putIfAbsent(deviceId, GlobalKey.new);

  Future<void> _openLogin() async {
    await Navigator.of(context)
        .push<bool>(MaterialPageRoute(builder: (_) => const LoginPage()));
  }

  void _connectDevice(AccountDeviceDto device, {bool viewOnly = false}) {
    if (!device.isOnline) return;
    widget.onConnect(
      SessionConnectRequest(
        targetDeviceId: device.deviceId,
        targetDeviceName: device.displayName,
        sameAccount: true,
        viewOnly: viewOnly,
      ),
      sourceKey: _cardKey(device.deviceId),
    );
  }

  void _showDeviceActions(AccountDeviceDto device) {
    OsmiumHaptics.lock();
    setState(() {
      _sheetDevice = device;
      _sheetVisible = true;
    });
    _syncDock();
  }

  void _dismissSheet() {
    if (!_sheetVisible) return;
    setState(() => _sheetVisible = false);
    _syncDock();
  }

  void _openRename(AccountDeviceDto device) {
    _renameCtrl.text = device.displayName;
    _renameCtrl.selection = TextSelection(
      baseOffset: 0,
      extentOffset: device.displayName.length,
    );
    setState(() {
      _renameDevice = device;
      _renameVisible = true;
    });
    _syncDock();
  }

  void _dismissRename() {
    if (!_renameVisible) return;
    FocusScope.of(context).unfocus();
    setState(() => _renameVisible = false);
    _syncDock();
  }

  Future<void> _confirmRename() async {
    final device = _renameDevice;
    final trimmed = _renameCtrl.text.trim();
    _dismissRename();
    if (device != null && trimmed.isNotEmpty) {
      await ref
          .read(appControllerProvider)
          .renameRemoteDevice(device.deviceId, trimmed);
    }
  }

  Future<void> _removeDevice(AccountDeviceDto device) async {
    _dismissSheet();
    OsmiumHaptics.confirm();
    setState(() => _removingDeviceId = device.deviceId);
    // Let the row collapse before the list loses the item.
    await Future<void>.delayed(const Duration(milliseconds: 240));
    if (!mounted) return;
    setState(() => _removingDeviceId = null);
    await ref.read(appControllerProvider).removeRemoteDevice(device.deviceId);
  }

  @override
  Widget build(BuildContext context) {
    final app = ref.watch(appControllerProvider);
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;
    final keyboardInset = MediaQuery.viewInsetsOf(context).bottom;
    final scrollBottom = keyboardInset > 0 ? 20.0 : widget.bottomInset + 24;

    final sortedDevices = [...app.accountDevices]
      ..sort((a, b) {
        if (a.isOnline != b.isOnline) return a.isOnline ? -1 : 1;
        return b.lastActiveAt.compareTo(a.lastActiveAt);
      });

    return MiuixScaffold(
      topBar: MiuixTopAppBar(
        title: '设备',
        blurred: true,
        scrollBehavior: _scrollBehavior,
        actions: [
          if (app.isLoggedIn) ...[
            MiuixIconButton(
              onPressed: () {
                OsmiumHaptics.selection();
                Navigator.of(context).push<bool>(
                  MaterialPageRoute(builder: (_) => const QrScannerPage()),
                );
              },
              child: Icon(
                Icons.qr_code_scanner_rounded,
                color: colors.onSurface,
                semanticLabel: '扫码登录',
              ),
            ),
            MiuixIconButton(
              onPressed: app.refreshingDevices
                  ? null
                  : app.refreshAccountDevices,
              child: app.refreshingDevices
                  ? SizedBox(
                      width: 18,
                      height: 18,
                      child: CircularProgressIndicator(
                        strokeWidth: 2,
                        color: colors.primary,
                      ),
                    )
                  : Icon(
                      Icons.refresh_rounded,
                      color: colors.onSurface,
                      semanticLabel: '刷新',
                    ),
            ),
          ] else
            Tooltip(
              message: '通过设备码连接',
              child: MiuixIconButton(
                onPressed: widget.onOpenConnect,
                child: Icon(Icons.keyboard_rounded, color: colors.onSurface),
              ),
            ),
        ],
      ),
      content: (contentPadding) {
        return Stack(
          children: [
            Padding(
              padding: EdgeInsets.only(bottom: keyboardInset),
              child: MiuixScrollBehaviorListener(
                behavior: _scrollBehavior,
                child: MiuixPullToRefresh(
                  isRefreshing: app.refreshingDevices,
                  onRefresh: () async {
                    if (app.isLoggedIn) {
                      await app.refreshAccountDevices();
                    }
                  },
                  color: colors.primary,
                  contentPadding: EdgeInsets.only(top: contentPadding.top),
                  refreshTexts: const ['下拉刷新', '松开刷新', '刷新中…', '已刷新'],
                  child: Center(
                    child: ConstrainedBox(
                      constraints: const BoxConstraints(maxWidth: 720),
                      child: ListView(
                        keyboardDismissBehavior:
                            ScrollViewKeyboardDismissBehavior.onDrag,
                        physics: const AlwaysScrollableScrollPhysics(
                          parent: BouncingScrollPhysics(),
                        ),
                        padding: EdgeInsets.fromLTRB(
                          16,
                          contentPadding.top + 8,
                          16,
                          scrollBottom,
                        ),
                        children: [
                          if (!app.isLoggedIn)
                            _UnloggedHeroCard(onLogin: _openLogin)
                          else if (sortedDevices.isEmpty &&
                              app.refreshingDevices) ...[
                            for (var i = 0; i < 2; i++)
                              Padding(
                                padding: const EdgeInsets.only(bottom: 12),
                                child: _DevicePlaceholder(index: i),
                              ),
                          ] else if (sortedDevices.isEmpty)
                            _EmptyDevicesCard(
                              onOpenConnect: widget.onOpenConnect,
                            )
                          else ...[
                            OsmiumSectionHeader(
                              '我的设备',
                              trailing: OsmiumStatusPill(
                                label: '${sortedDevices.length}',
                                background: colors.secondaryContainer,
                                foreground: colors.onSurfaceVariantSummary,
                                fontSize: 11,
                              ),
                            ),
                            for (var i = 0; i < sortedDevices.length; i++)
                              Padding(
                                padding: const EdgeInsets.only(bottom: 12),
                                child: OsmiumEntrance(
                                  key: ValueKey(
                                    'entrance-${sortedDevices[i].deviceId}',
                                  ),
                                  index: i,
                                  child: OsmiumCollapse(
                                    collapsed:
                                        _removingDeviceId ==
                                        sortedDevices[i].deviceId,
                                    child: _DeviceHeroCard(
                                      key: _cardKey(sortedDevices[i].deviceId),
                                      device: sortedDevices[i],
                                      isDark: isDark,
                                      wallpaperPath: app.wallpaperPathFor(
                                        sortedDevices[i].deviceId,
                                      ),
                                      onConnect: () =>
                                          _connectDevice(sortedDevices[i]),
                                      onViewOnly: () => _connectDevice(
                                        sortedDevices[i],
                                        viewOnly: true,
                                      ),
                                      onMore: () =>
                                          _showDeviceActions(sortedDevices[i]),
                                    ),
                                  ),
                                ),
                              ),
                          ],
                        ],
                      ),
                    ),
                  ),
                ),
              ),
            ),
            _buildActionSheet(context),
            _buildRenameDialog(context),
          ],
        );
      },
    );
  }

  Widget _buildActionSheet(BuildContext context) {
    final device = _sheetDevice;
    if (device == null) return const SizedBox.shrink();
    final app = ref.read(appControllerProvider);
    final colors = MiuixTheme.of(context).colors;
    final isDark = MiuixTheme.of(context).brightness == Brightness.dark;

    return MiuixOverlayBottomSheet(
      show: _sheetVisible,
      title: device.displayName,
      endAction: Text(
        DeviceIdentity.formatDeviceId(device.deviceId),
        style: OsmiumTheme.monoStyle(
          fontSize: 13,
          color: colors.onSurfaceVariantSummary,
        ),
      ),
      onDismissRequest: _dismissSheet,
      // The sheet widget stays mounted after its first open: the popup host may
      // still rebuild its entry while the exit transition drains, and rebuilding
      // an entry whose owning element is gone throws. Keeping the (invisible)
      // widget alive is cheaper than that, and `show` alone controls visibility.
      onDismissFinished: _syncDock,
      insideMargin: const Size(0, 10),
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            MiuixArrowPreference(
              title: '重命名',
              startAction: const OsmiumPrefIcon(Icons.edit_outlined),
              onClick: () {
                OsmiumHaptics.selection();
                _dismissSheet();
                _openRename(device);
              },
            ),
            MiuixArrowPreference(
              title: '复制设备码',
              startAction: const OsmiumPrefIcon(Icons.copy_rounded),
              onClick: () {
                OsmiumHaptics.selection();
                Clipboard.setData(ClipboardData(text: device.deviceId));
                _dismissSheet();
              },
            ),
            if (device.isBeingControlled)
              MiuixArrowPreference(
                title: '强制断开远控',
                startAction: const OsmiumPrefIcon(Icons.link_off_rounded),
                onClick: () {
                  OsmiumHaptics.confirm();
                  app.forceDisconnectDevice(device.deviceId);
                  _dismissSheet();
                },
              ),
            MiuixArrowPreference(
              title: '移除设备',
              startAction: const OsmiumPrefIcon(Icons.delete_outline_rounded),
              titleColor: MiuixBasicComponentColors(
                color: OsmiumColors.dangerFg(isDark),
                disabledColor: colors.disabledOnSecondaryVariant,
              ),
              onClick: () => _removeDevice(device),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildRenameDialog(BuildContext context) {
    final device = _renameDevice;
    if (device == null) return const SizedBox.shrink();

    return MiuixOverlayDialog(
      show: _renameVisible,
      title: '重命名设备',
      onDismissRequest: _dismissRename,
      onDismissFinished: () {
        if (mounted) setState(() => _renameDevice = null);
        _syncDock();
      },
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            MiuixTextField(
              controller: _renameCtrl,
              label: '设备名称',
              singleLine: true,
              autofocus: true,
              onSubmitted: (_) => _confirmRename(),
            ),
            const SizedBox(height: 18),
            Row(
              children: [
                Expanded(
                  child: MiuixTextButton('取消', onPressed: _dismissRename),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: MiuixTextButton(
                    '确定',
                    onPressed: _confirmRename,
                    colors: MiuixButtonDefaults.buttonColorsPrimary(context),
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

/// Quiet skeleton card shown on the very first device load. A slow opacity
/// breath, no shimmer.
class _DevicePlaceholder extends StatefulWidget {
  const _DevicePlaceholder({required this.index});

  final int index;

  @override
  State<_DevicePlaceholder> createState() => _DevicePlaceholderState();
}

class _DevicePlaceholderState extends State<_DevicePlaceholder>
    with SingleTickerProviderStateMixin {
  late final AnimationController _breath = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 1500),
    value: 0.5 + widget.index * 0.18,
  );

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (OsmiumMotion.reduced(context)) {
      _breath.value = 0.62;
    } else if (!_breath.isAnimating) {
      _breath.repeat(reverse: true, min: 0.35, max: 0.72);
    }
  }

  @override
  void dispose() {
    _breath.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final colors = MiuixTheme.of(context).colors;
    final base = colors.onSurface.withValues(alpha: 0.06);
    final strong = colors.onSurface.withValues(alpha: 0.10);

    return RepaintBoundary(
      child: MiuixCard(
        cornerRadius: 24,
        insideMargin: EdgeInsets.zero,
        child: FadeTransition(
          opacity: _breath,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Container(
                height: 106,
                color: strong,
                padding: const EdgeInsets.fromLTRB(16, 14, 16, 14),
                child: Row(
                  children: [
                    Container(
                      width: 46,
                      height: 46,
                      decoration: ShapeDecoration(
                        color: base,
                        shape: MiuixSquircleBorder(cornerRadius: 15),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Column(
                      mainAxisAlignment: MainAxisAlignment.center,
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Container(width: 120, height: 14, color: base),
                        const SizedBox(height: 10),
                        Container(width: 54, height: 10, color: base),
                      ],
                    ),
                  ],
                ),
              ),
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 14, 16, 14),
                child: Row(
                  children: [
                    Container(width: 96, height: 12, color: base),
                    const Spacer(),
                    Container(width: 84, height: 32, color: base),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _UnloggedHeroCard extends StatelessWidget {
  const _UnloggedHeroCard({required this.onLogin});

  final VoidCallback onLogin;

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final isDark = theme.brightness == Brightness.dark;

    return MiuixCard(
      cornerRadius: 26,
      insideMargin: EdgeInsets.zero,
      feedbackType: MiuixPressFeedbackType.sink,
      onPressed: onLogin,
      child: ClipPath(
        clipper: ShapeBorderClipper(
          shape: MiuixSquircleBorder(cornerRadius: 26),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            OsmiumBanner(
              colors: OsmiumBannerGradients.brand(isDark),
              height: 168,
              padding: const EdgeInsets.fromLTRB(22, 24, 22, 24),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisAlignment: MainAxisAlignment.end,
                children: [
                  Container(
                    width: 52,
                    height: 52,
                    padding: const EdgeInsets.all(6),
                    decoration: ShapeDecoration(
                      color: Colors.white.withValues(alpha: 0.16),
                      shape: MiuixSquircleBorder(cornerRadius: 16),
                    ),
                    child: ClipRRect(
                      borderRadius: BorderRadius.circular(11),
                      child: Image.asset(
                        'assets/images/osmium_logo.png',
                        fit: BoxFit.cover,
                      ),
                    ),
                  ),
                  const SizedBox(height: 14),
                  const Text(
                    'Osmium',
                    style: TextStyle(
                      fontFamily: OsmiumTheme.fontFamily,
                      fontSize: 24,
                      fontWeight: FontWeight.w700,
                      height: 1.15,
                      color: Colors.white,
                    ),
                  ),
                ],
              ),
            ),
            Padding(
              padding: const EdgeInsets.all(16),
              child: SizedBox(
                width: double.infinity,
                child: MiuixTextButton(
                  '登录账号',
                  onPressed: onLogin,
                  colors: MiuixButtonDefaults.buttonColorsPrimary(context),
                  minHeight: 50,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _EmptyDevicesCard extends StatelessWidget {
  const _EmptyDevicesCard({required this.onOpenConnect});

  final VoidCallback onOpenConnect;

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    return MiuixCard(
      cornerRadius: 24,
      insideMargin: const EdgeInsets.fromLTRB(24, 32, 24, 24),
      child: Column(
        children: [
          OsmiumIconTile(
            icon: Icons.desktop_windows_rounded,
            size: 58,
            radius: 18,
            background: colors.secondaryContainer,
            foreground: colors.onSecondaryContainer,
          ),
          const SizedBox(height: 16),
          MiuixText('暂无设备', style: theme.textStyles.title3),
          const SizedBox(height: 20),
          SizedBox(
            width: double.infinity,
            child: MiuixTextButton(
              '通过设备码连接',
              onPressed: onOpenConnect,
              colors: MiuixButtonDefaults.buttonColorsPrimary(context),
              minHeight: 48,
            ),
          ),
        ],
      ),
    );
  }
}

class _DeviceHeroCard extends StatelessWidget {
  const _DeviceHeroCard({
    super.key,
    required this.device,
    required this.isDark,
    this.wallpaperPath,
    required this.onConnect,
    required this.onViewOnly,
    required this.onMore,
  });

  final AccountDeviceDto device;
  final bool isDark;
  final String? wallpaperPath;
  final VoidCallback onConnect;
  final VoidCallback onViewOnly;
  final VoidCallback onMore;

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final online = device.isOnline;
    final controlled = device.isBeingControlled;
    final hasWallpaper = wallpaperPath != null && wallpaperPath!.isNotEmpty;

    final pillBackground = controlled
        ? Colors.white.withValues(alpha: 0.92)
        : hasWallpaper
        ? Colors.black.withValues(alpha: online ? 0.34 : 0.28)
        : Colors.white.withValues(alpha: online ? 0.18 : 0.13);
    final pillForeground = controlled
        ? const Color(0xFF8A4A06)
        : Colors.white.withValues(alpha: online ? 1 : 0.88);
    final pillLabel = controlled ? '远控中' : (online ? '在线' : '离线');

    return AnimatedOpacity(
      opacity: online ? 1 : 0.74,
      duration: OsmiumMotion.durationOr(context, OsmiumMotion.small),
      curve: MiuixMotion.standardDecelerate,
      child: MiuixCard(
        cornerRadius: 24,
        insideMargin: EdgeInsets.zero,
        feedbackType: online
            ? MiuixPressFeedbackType.sink
            : MiuixPressFeedbackType.none,
        onPressed: online ? onConnect : null,
        onLongPress: onMore,
        child: ClipPath(
          clipper: ShapeBorderClipper(
            shape: MiuixSquircleBorder(cornerRadius: 24),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              OsmiumBanner(
                colors: OsmiumBannerGradients.device(
                  isDark: isDark,
                  online: online,
                  controlled: controlled,
                ),
                height: 106,
                padding: const EdgeInsets.fromLTRB(16, 14, 16, 14),
                backgroundImage: hasWallpaper
                    ? FileImage(File(wallpaperPath!))
                    : null,
                child: Row(
                  children: [
                    Container(
                      width: 46,
                      height: 46,
                      alignment: Alignment.center,
                      decoration: ShapeDecoration(
                        color: hasWallpaper
                            ? Colors.black.withValues(alpha: 0.28)
                            : Colors.white.withValues(alpha: 0.16),
                        shape: MiuixSquircleBorder(
                          cornerRadius: 15,
                          side: hasWallpaper
                              ? BorderSide(
                                  color: Colors.white.withValues(alpha: 0.18),
                                )
                              : BorderSide.none,
                        ),
                      ),
                      child: const Icon(
                        Icons.desktop_windows_rounded,
                        size: 24,
                        color: Colors.white,
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(
                        mainAxisAlignment: MainAxisAlignment.center,
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            device.displayName,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: TextStyle(
                              fontFamily: OsmiumTheme.fontFamily,
                              fontSize: 18,
                              fontWeight: FontWeight.w600,
                              height: 1.2,
                              color: Colors.white,
                              shadows: hasWallpaper
                                  ? const [
                                      Shadow(
                                        color: Color(0x66000000),
                                        blurRadius: 8,
                                        offset: Offset(0, 1),
                                      ),
                                    ]
                                  : null,
                            ),
                          ),
                          const SizedBox(height: 8),
                          OsmiumStatusPill(
                            label: pillLabel,
                            background: pillBackground,
                            foreground: pillForeground,
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 12, 12, 12),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Row(
                      children: [
                        Text(
                          DeviceIdentity.formatDeviceId(device.deviceId),
                          style: OsmiumTheme.monoStyle(
                            fontSize: 13,
                            color: colors.onSurfaceVariantSummary,
                          ),
                        ),
                        if (device.osVersion.isNotEmpty) ...[
                          const SizedBox(width: 8),
                          Text(
                            '·',
                            style: TextStyle(
                              fontFamily: OsmiumTheme.fontFamily,
                              fontSize: 12,
                              color: colors.onSurfaceVariantSummary.withValues(
                                alpha: 0.6,
                              ),
                            ),
                          ),
                          const SizedBox(width: 8),
                          Flexible(
                            child: Text(
                              device.osVersion,
                              maxLines: 1,
                              overflow: TextOverflow.ellipsis,
                              style: TextStyle(
                                fontFamily: OsmiumTheme.fontFamily,
                                fontSize: 12,
                                color: colors.onSurfaceVariantSummary,
                              ),
                            ),
                          ),
                        ],
                      ],
                    ),
                    const SizedBox(height: 10),
                    Row(
                      children: [
                        Expanded(
                          child: MiuixButton(
                            onPressed: online ? onConnect : null,
                            enabled: online,
                            colors: MiuixButtonDefaults.buttonColorsPrimary(
                              context,
                            ),
                            cornerRadius: 14,
                            minWidth: 0,
                            minHeight: 44,
                            insideMargin: const EdgeInsets.symmetric(
                              horizontal: 12,
                              vertical: 10,
                            ),
                            child: const Text(
                              '远程控制',
                              style: TextStyle(
                                fontSize: 14,
                                fontWeight: FontWeight.w600,
                              ),
                            ),
                          ),
                        ),
                        const SizedBox(width: 8),
                        Tooltip(
                          message: '仅观看',
                          child: MiuixIconButton(
                            onPressed: online ? onViewOnly : null,
                            backgroundColor: colors.secondaryContainer,
                            child: Icon(
                              Icons.visibility_outlined,
                              size: 20,
                              color: online
                                  ? colors.onSecondaryContainer
                                  : colors.disabledOnSecondaryVariant,
                            ),
                          ),
                        ),
                        const SizedBox(width: 6),
                        Tooltip(
                          message: '更多',
                          child: MiuixIconButton(
                            onPressed: onMore,
                            backgroundColor: colors.secondaryContainer,
                            child: Icon(
                              Icons.more_horiz_rounded,
                              size: 20,
                              color: colors.onSecondaryContainer,
                            ),
                          ),
                        ),
                      ],
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
