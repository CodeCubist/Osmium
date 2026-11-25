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

enum _DeviceSortMode {
  onlineFirst('在线优先'),
  nameAsc('A-Z'),
  nameDesc('Z-A');

  const _DeviceSortMode(this.label);
  final String label;
}

class DevicesTab extends ConsumerStatefulWidget {
  const DevicesTab({
    super.key,
    required this.onConnect,
    required this.onOpenConnect,
    this.onOpenMe,
    required this.bottomInset,
  });

  final SessionLauncher onConnect;
  final VoidCallback onOpenConnect;
  final VoidCallback? onOpenMe;
  final double bottomInset;

  @override
  ConsumerState<DevicesTab> createState() => _DevicesTabState();
}

class _DevicesTabState extends ConsumerState<DevicesTab> {
  final Map<String, GlobalKey> _cardKeys = {};
  final TextEditingController _searchCtrl = TextEditingController();
  final FocusNode _searchFocus = FocusNode();

  bool _scrolled = false;
  bool _gridMode = false;
  bool _onlineOnly = false;
  bool _sectionExpanded = true;
  bool _searchVisible = false;
  _DeviceSortMode _sortMode = _DeviceSortMode.onlineFirst;

  // Declarative Miuix overlays: visibility is driven by state so the enter and
  // exit springs (and drag-to-dismiss) run through the library.
  AccountDeviceDto? _sheetDevice;
  bool _sheetVisible = false;

  AccountDeviceDto? _renameDevice;
  bool _renameVisible = false;
  late final TextEditingController _renameCtrl = TextEditingController();

  AccountDeviceDto? _powerConfirmDevice;
  String? _powerConfirmAction;
  bool _powerConfirmVisible = false;

  String _powerResultTitle = '';
  String _powerResultMessage = '';
  bool _powerResultVisible = false;
  bool _powerBusy = false;

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
    _searchCtrl.dispose();
    _searchFocus.dispose();
    _renameCtrl.dispose();
    super.dispose();
  }

  void _syncDock() {
    _dockHidden?.value =
        _sheetVisible ||
        _renameVisible ||
        _powerConfirmVisible ||
        _powerResultVisible;
  }

  GlobalKey _cardKey(String deviceId) =>
      _cardKeys.putIfAbsent(deviceId, GlobalKey.new);

  Future<void> _openLogin() async {
    await Navigator.of(context)
        .push<bool>(MaterialPageRoute(builder: (_) => const LoginPage()));
  }

  void _toggleSearch() {
    OsmiumHaptics.selection();
    setState(() {
      _searchVisible = !_searchVisible;
      if (!_searchVisible) {
        _searchCtrl.clear();
        _searchFocus.unfocus();
      }
    });
    if (_searchVisible) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _searchFocus.requestFocus();
      });
    }
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
    await Future<void>.delayed(const Duration(milliseconds: 240));
    if (!mounted) return;
    setState(() => _removingDeviceId = null);
    await ref.read(appControllerProvider).removeRemoteDevice(device.deviceId);
  }

  void _openPowerConfirm(AccountDeviceDto device, String action) {
    setState(() {
      _powerConfirmDevice = device;
      _powerConfirmAction = action;
      _powerConfirmVisible = true;
    });
    _syncDock();
  }

  void _dismissPowerConfirm() {
    if (!_powerConfirmVisible) return;
    setState(() => _powerConfirmVisible = false);
    _syncDock();
  }

  void _showPowerResult(String title, String message) {
    if (!mounted) return;
    setState(() {
      _powerResultTitle = title;
      _powerResultMessage = message;
      _powerResultVisible = true;
    });
    _syncDock();
  }

  void _dismissPowerResult() {
    if (!_powerResultVisible) return;
    setState(() => _powerResultVisible = false);
    _syncDock();
  }

  Future<void> _executePowerAction(
    AccountDeviceDto device,
    String action,
  ) async {
    if (_powerBusy) return;
    setState(() => _powerBusy = true);
    try {
      final result = await ref
          .read(appControllerProvider)
          .executeRemotePowerAction(device, action);
      final title = action == 'wake'
          ? '远程开机'
          : (action == 'restart' ? '远程重启' : '远程关机');
      _showPowerResult(result.ok ? title : '$title失败', result.message);
    } finally {
      if (mounted) {
        setState(() => _powerBusy = false);
      }
    }
  }

  void _handlePowerMenuTap(AccountDeviceDto device, String action) {
    _dismissSheet();
    final app = ref.read(appControllerProvider);
    if (!app.isProfileComplete) {
      _showPowerResult(
        '请完善账号安全信息',
        '为了保障设备安全，请先在「我的」中绑定邮箱并设置登录密码，完成后即可使用远程开关机功能。',
      );
      return;
    }
    if (!device.allowRemotePower) {
      _showPowerResult(
        '远程电源未开启',
        '目标电脑「${device.displayName}」尚未开启远程开关机功能。请先在该电脑的设置->远程开关机中开启，并确认主板BIOS已开启Wake-on-LAN。',
      );
      return;
    }
    if (action == 'wake' && device.wolMacs.isEmpty) {
      _showPowerResult(
        '无法远程开机',
        '尚未获取到「${device.displayName}」的网卡MAC地址，请先在目标电脑在线时开启远程开关机功能。',
      );
      return;
    }
    if (action == 'wake') {
      OsmiumHaptics.confirm();
      _executePowerAction(device, 'wake');
      return;
    }
    OsmiumHaptics.selection();
    _openPowerConfirm(device, action);
  }

  List<AccountDeviceDto> _filteredAndSorted(List<AccountDeviceDto> source) {
    final query = _searchCtrl.text.trim().toLowerCase();
    final digitsQuery = DeviceIdentity.normalizeDeviceId(query);

    final list = source.where((d) {
      if (_onlineOnly && !d.isOnline) return false;
      if (query.isEmpty) return true;
      if (d.displayName.toLowerCase().contains(query)) return true;
      if (digitsQuery.isNotEmpty && d.deviceId.contains(digitsQuery)) {
        return true;
      }
      return false;
    }).toList();

    list.sort((a, b) {
      switch (_sortMode) {
        case _DeviceSortMode.onlineFirst:
          if (a.isOnline != b.isOnline) return a.isOnline ? -1 : 1;
          return b.lastActiveAt.compareTo(a.lastActiveAt);
        case _DeviceSortMode.nameAsc:
          return a.displayName.toLowerCase().compareTo(
            b.displayName.toLowerCase(),
          );
        case _DeviceSortMode.nameDesc:
          return b.displayName.toLowerCase().compareTo(
            a.displayName.toLowerCase(),
          );
      }
    });
    return list;
  }

  @override
  Widget build(BuildContext context) {
    final app = ref.watch(appControllerProvider);
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;
    final topPad = MediaQuery.paddingOf(context).top;
    final keyboardInset = MediaQuery.viewInsetsOf(context).bottom;
    final scrollBottom = keyboardInset > 0 ? 20.0 : widget.bottomInset + 24;
    final headerHeight =
        topPad + OsmiumTopHeader.barHeight + (_searchVisible ? 50 : 0);

    final avatarBytes = app.isLoggedIn
        ? decodeAvatarDataUri(app.account?.avatarData)
        : null;
    final hasAnyDevices = app.accountDevices.isNotEmpty;
    final visibleDevices = _filteredAndSorted(app.accountDevices);

    return MiuixScaffold(
      content: (_) {
        return Stack(
          children: [
            Padding(
              padding: EdgeInsets.only(bottom: keyboardInset),
              child: NotificationListener<ScrollNotification>(
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
                  isRefreshing: app.refreshingDevices,
                  onRefresh: () async {
                    if (app.isLoggedIn) {
                      await Future.wait([
                        app.refreshAccountProfile(),
                        app.refreshAccountDevices(),
                      ]);
                    }
                  },
                  color: colors.primary,
                  contentPadding: EdgeInsets.only(top: headerHeight + 14),
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
                          headerHeight + 6,
                          16,
                          scrollBottom,
                        ),
                        children: [
                          if (!app.isLoggedIn)
                            _UnloggedHeroCard(onLogin: _openLogin)
                          else if (!hasAnyDevices && app.refreshingDevices) ...[
                            for (var i = 0; i < 2; i++)
                              Padding(
                                padding: const EdgeInsets.only(bottom: 14),
                                child: _DevicePlaceholder(index: i),
                              ),
                          ] else if (!hasAnyDevices)
                            _EmptyDevicesCard(
                              onOpenConnect: widget.onOpenConnect,
                            )
                          else ...[
                            _DeviceToolbar(
                              gridMode: _gridMode,
                              sortMode: _sortMode,
                              onlineOnly: _onlineOnly,
                              onGridModeChanged: (v) {
                                setState(() => _gridMode = v);
                              },
                              onSortModeChanged: (v) {
                                setState(() => _sortMode = v);
                              },
                              onOnlineOnlyChanged: (v) {
                                setState(() => _onlineOnly = v);
                              },
                            ),
                            const SizedBox(height: 6),
                            _CollapsibleSectionHeader(
                              title: '电脑连接',
                              expanded: _sectionExpanded,
                              onTap: () {
                                OsmiumHaptics.selection();
                                setState(
                                  () => _sectionExpanded = !_sectionExpanded,
                                );
                              },
                            ),
                            AnimatedSize(
                              duration: OsmiumMotion.durationOr(
                                context,
                                OsmiumMotion.tabSwitch,
                              ),
                              curve: MiuixMotion.standardDecelerate,
                              alignment: Alignment.topCenter,
                              child: !_sectionExpanded
                                  ? const SizedBox.shrink()
                                  : visibleDevices.isEmpty
                                  ? _NoMatchCard(
                                      onReset: () {
                                        OsmiumHaptics.selection();
                                        setState(() {
                                          _onlineOnly = false;
                                          _searchCtrl.clear();
                                        });
                                      },
                                    )
                                  : _gridMode
                                  ? _buildGridDevices(app, visibleDevices)
                                  : _buildListDevices(app, visibleDevices),
                            ),
                          ],
                        ],
                      ),
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
                title: '设备',
                scrolled: _scrolled,
                leading: _ProfileAvatarButton(
                  avatarBytes: avatarBytes,
                  isDark: isDark,
                  onTap: () {
                    OsmiumHaptics.selection();
                    if (app.isLoggedIn) {
                      widget.onOpenMe?.call();
                    } else {
                      _openLogin();
                    }
                  },
                ),
                actions: [
                  if (app.isLoggedIn)
                    MiuixIconButton(
                      onPressed: () {
                        OsmiumHaptics.selection();
                        Navigator.of(context).push<bool>(
                          MaterialPageRoute(
                            builder: (_) => const QrScannerPage(),
                          ),
                        );
                      },
                      minWidth: 40,
                      minHeight: 40,
                      child: Icon(
                        Icons.qr_code_scanner_rounded,
                        size: 21,
                        color: colors.onSurface,
                        semanticLabel: '扫码登录',
                      ),
                    ),
                  MiuixIconButton(
                    onPressed: () {
                      OsmiumHaptics.selection();
                      widget.onOpenConnect();
                    },
                    minWidth: 40,
                    minHeight: 40,
                    child: Icon(
                      Icons.add_rounded,
                      size: 25,
                      color: colors.onSurface,
                      semanticLabel: '通过设备码连接',
                    ),
                  ),
                  if (app.isLoggedIn)
                    MiuixIconButton(
                      onPressed: _toggleSearch,
                      minWidth: 40,
                      minHeight: 40,
                      child: Icon(
                        _searchVisible
                            ? Icons.close_rounded
                            : Icons.search_rounded,
                        size: 22,
                        color: _searchVisible
                            ? colors.primary
                            : colors.onSurface,
                        semanticLabel: '搜索设备',
                      ),
                    ),
                ],
                bottom: _searchVisible
                    ? Padding(
                        padding: const EdgeInsets.fromLTRB(16, 0, 16, 10),
                        child: MiuixTextField(
                          cornerRadius: OsmiumFieldDefaults.cornerRadius,
                          controller: _searchCtrl,
                          focusNode: _searchFocus,
                          label: '搜索设备',
                          useLabelAsPlaceholder: true,
                          singleLine: true,
                          insideMargin: OsmiumFieldDefaults.compactMargin,
                          onChanged: (_) => setState(() {}),
                          trailingIcon: _searchCtrl.text.isNotEmpty
                              ? MiuixIconButton(
                                  onPressed: () {
                                    _searchCtrl.clear();
                                    setState(() {});
                                  },
                                  minWidth: 32,
                                  minHeight: 32,
                                  child: Icon(
                                    Icons.close_rounded,
                                    size: 16,
                                    color: colors.onSurfaceVariantSummary,
                                  ),
                                )
                              : null,
                        ),
                      )
                    : null,
              ),
            ),
            _buildDeviceSheet(context, app, colors, isDark),
            _buildRenameDialog(context),
            _buildPowerConfirmDialog(context, colors, isDark),
            _buildPowerResultDialog(context),
          ],
        );
      },
    );
  }

  Widget _buildListDevices(AppController app, List<AccountDeviceDto> devices) {
    return Column(
      children: [
        for (final device in devices)
          OsmiumCollapse(
            key: ValueKey('row_${device.deviceId}'),
            collapsed: _removingDeviceId == device.deviceId,
            child: Padding(
              padding: const EdgeInsets.only(bottom: 14),
              child: _DeviceWallpaperCard(
                key: _cardKey(device.deviceId),
                device: device,
                wallpaperPath: app.wallpaperPathFor(device.deviceId),
                compact: false,
                powerBusy: _powerBusy,
                onConnect: () => _connectDevice(device),
                onMore: () => _showDeviceActions(device),
                onPowerAction: (action) => _handlePowerMenuTap(device, action),
              ),
            ),
          ),
      ],
    );
  }

  Widget _buildGridDevices(AppController app, List<AccountDeviceDto> devices) {
    final rows = <Widget>[];
    for (var i = 0; i < devices.length; i += 2) {
      final left = devices[i];
      final right = i + 1 < devices.length ? devices[i + 1] : null;
      rows.add(
        Padding(
          padding: const EdgeInsets.only(bottom: 12),
          child: Row(
            children: [
              Expanded(
                child: _DeviceWallpaperCard(
                  key: _cardKey(left.deviceId),
                  device: left,
                  wallpaperPath: app.wallpaperPathFor(left.deviceId),
                  compact: true,
                  powerBusy: _powerBusy,
                  onConnect: () => _connectDevice(left),
                  onMore: () => _showDeviceActions(left),
                  onPowerAction: (action) => _handlePowerMenuTap(left, action),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: right != null
                    ? _DeviceWallpaperCard(
                        key: _cardKey(right.deviceId),
                        device: right,
                        wallpaperPath: app.wallpaperPathFor(right.deviceId),
                        compact: true,
                        powerBusy: _powerBusy,
                        onConnect: () => _connectDevice(right),
                        onMore: () => _showDeviceActions(right),
                        onPowerAction: (action) =>
                            _handlePowerMenuTap(right, action),
                      )
                    : const SizedBox.shrink(),
              ),
            ],
          ),
        ),
      );
    }
    return Column(children: rows);
  }

  Widget _buildDeviceSheet(
    BuildContext context,
    AppController app,
    MiuixColors colors,
    bool isDark,
  ) {
    final seed = _sheetDevice;
    if (seed == null) return const SizedBox.shrink();
    final device = app.accountDevices.firstWhere(
      (d) => d.deviceId == seed.deviceId,
      orElse: () => seed,
    );
    final theme = MiuixTheme.of(context);
    final profileIncomplete = !app.isProfileComplete;
    final powerDisabled = profileIncomplete || !device.allowRemotePower;
    final powerDisabledLabel = profileIncomplete ? '需完善账号' : '未开启';
    final powerDisabledTag = profileIncomplete
        ? '需完善账号'
        : (!device.allowRemotePower
            ? '未开启'
            : (!device.isOnline && device.wolMacs.isEmpty ? '无MAC' : null));

    return MiuixOverlayBottomSheet(
      show: _sheetVisible,
      title: device.displayName,
      onDismissRequest: _dismissSheet,
      onDismissFinished: _syncDock,
      insideMargin: const Size(0, 10),
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (device.isOnline) ...[
              MiuixArrowPreference(
                title: '远程控制',
                startAction: const OsmiumPrefIcon(
                  Icons.desktop_windows_rounded,
                ),
                onClick: () {
                  OsmiumHaptics.selection();
                  _dismissSheet();
                  _connectDevice(device);
                },
              ),
              MiuixArrowPreference(
                title: '仅观看',
                startAction: const OsmiumPrefIcon(Icons.visibility_outlined),
                onClick: () {
                  OsmiumHaptics.selection();
                  _dismissSheet();
                  _connectDevice(device, viewOnly: true);
                },
              ),
            ],
            if (device.isComputerDevice && !device.isOnline)
              MiuixArrowPreference(
                title: '远程开机（网卡唤醒）',
                startAction: const OsmiumPrefIcon(
                  Icons.power_settings_new_rounded,
                ),
                titleColor: powerDisabledTag != null
                    ? MiuixBasicComponentColors(
                        color: colors.disabledOnSecondaryVariant,
                        disabledColor: colors.disabledOnSecondaryVariant,
                      )
                    : null,
                endActions: powerDisabledTag != null
                    ? [
                        MiuixText(
                          powerDisabledTag,
                          style: theme.textStyles.body2,
                          color: colors.onSurfaceVariantSummary,
                        ),
                      ]
                    : const [],
                onClick: () => _handlePowerMenuTap(device, 'wake'),
              ),
            if (device.isComputerDevice && device.isOnline) ...[
              MiuixArrowPreference(
                title: '远程重启',
                startAction: const OsmiumPrefIcon(Icons.restart_alt_rounded),
                titleColor: powerDisabled
                    ? MiuixBasicComponentColors(
                        color: colors.disabledOnSecondaryVariant,
                        disabledColor: colors.disabledOnSecondaryVariant,
                      )
                    : null,
                endActions: powerDisabled
                    ? [
                        MiuixText(
                          powerDisabledLabel,
                          style: theme.textStyles.body2,
                          color: colors.onSurfaceVariantSummary,
                        ),
                      ]
                    : const [],
                onClick: () => _handlePowerMenuTap(device, 'restart'),
              ),
              MiuixArrowPreference(
                title: '远程关机',
                startAction: const OsmiumPrefIcon(
                  Icons.power_settings_new_rounded,
                ),
                titleColor: MiuixBasicComponentColors(
                  color: !powerDisabled
                      ? OsmiumColors.dangerFg(isDark)
                      : colors.disabledOnSecondaryVariant,
                  disabledColor: colors.disabledOnSecondaryVariant,
                ),
                endActions: powerDisabled
                    ? [
                        MiuixText(
                          powerDisabledLabel,
                          style: theme.textStyles.body2,
                          color: colors.onSurfaceVariantSummary,
                        ),
                      ]
                    : const [],
                onClick: () => _handlePowerMenuTap(device, 'shutdown'),
              ),
            ],
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
              cornerRadius: OsmiumFieldDefaults.cornerRadius,
              insideMargin: OsmiumFieldDefaults.floatingMargin,
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
                  child: MiuixTextButton(
                    '取消',
                    onPressed: _dismissRename,
                  ),
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

  Widget _buildPowerConfirmDialog(
    BuildContext context,
    MiuixColors colors,
    bool isDark,
  ) {
    final device = _powerConfirmDevice;
    final action = _powerConfirmAction;
    if (device == null || action == null) return const SizedBox.shrink();
    final isRestart = action == 'restart';
    final title = isRestart ? '确认远程重启' : '确认远程关机';
    final body = isRestart
        ? '确定要远程重启「${device.displayName}」吗？目标电脑将在约3秒后强制重启，未保存的工作可能会丢失。'
        : '确定要远程关闭「${device.displayName}」吗？目标电脑将在约3秒后强制关机，未保存的工作可能会丢失。';
    final theme = MiuixTheme.of(context);

    return MiuixOverlayDialog(
      show: _powerConfirmVisible,
      title: title,
      onDismissRequest: _dismissPowerConfirm,
      onDismissFinished: () {
        if (mounted) {
          setState(() {
            _powerConfirmDevice = null;
            _powerConfirmAction = null;
          });
        }
        _syncDock();
      },
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          MiuixText(
            body,
            style: theme.textStyles.body2,
            color: colors.onSurfaceVariantSummary,
          ),
          const SizedBox(height: 18),
          Row(
            children: [
              Expanded(
                child: MiuixTextButton('取消', onPressed: _dismissPowerConfirm),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: MiuixTextButton(
                  isRestart ? '立即重启' : '立即关机',
                  onPressed: () {
                    final target = device;
                    final act = action;
                    _dismissPowerConfirm();
                    OsmiumHaptics.confirm();
                    _executePowerAction(target, act);
                  },
                  colors: MiuixButtonDefaults.buttonColorsPrimary(context),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }

  Widget _buildPowerResultDialog(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    return MiuixOverlayDialog(
      show: _powerResultVisible,
      title: _powerResultTitle.isNotEmpty ? _powerResultTitle : '远程电源',
      onDismissRequest: _dismissPowerResult,
      onDismissFinished: _syncDock,
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          MiuixText(
            _powerResultMessage,
            style: theme.textStyles.body2,
            color: colors.onSurfaceVariantSummary,
          ),
          const SizedBox(height: 18),
          MiuixTextButton(
            '知道了',
            onPressed: _dismissPowerResult,
            colors: MiuixButtonDefaults.buttonColorsPrimary(context),
          ),
        ],
      ),
    );
  }
}

class _ProfileAvatarButton extends StatelessWidget {
  const _ProfileAvatarButton({
    required this.avatarBytes,
    required this.isDark,
    required this.onTap,
  });

  final Uint8List? avatarBytes;
  final bool isDark;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final bytes = avatarBytes;
    return MiuixPressable(
      onPressed: onTap,
      borderRadius: BorderRadius.circular(999),
      feedbackType: MiuixPressFeedbackType.sink,
      sinkAmount: 0.94,
      child: Container(
        width: 34,
        height: 34,
        clipBehavior: Clip.antiAlias,
        decoration: BoxDecoration(
          color: OsmiumColors.profileCircle(isDark),
          shape: BoxShape.circle,
        ),
        child: bytes != null
            ? Image.memory(
                bytes,
                width: 34,
                height: 34,
                fit: BoxFit.cover,
                gaplessPlayback: true,
                errorBuilder: (_, _, _) =>
                    const Icon(Icons.person, size: 20, color: Colors.white),
              )
            : const Icon(Icons.person, size: 20, color: Colors.white),
      ),
    );
  }
}

class _DeviceToolbar extends StatelessWidget {
  const _DeviceToolbar({
    required this.gridMode,
    required this.sortMode,
    required this.onlineOnly,
    required this.onGridModeChanged,
    required this.onSortModeChanged,
    required this.onOnlineOnlyChanged,
  });

  final bool gridMode;
  final _DeviceSortMode sortMode;
  final bool onlineOnly;
  final ValueChanged<bool> onGridModeChanged;
  final ValueChanged<_DeviceSortMode> onSortModeChanged;
  final ValueChanged<bool> onOnlineOnlyChanged;

  @override
  Widget build(BuildContext context) {
    final colors = MiuixTheme.of(context).colors;
    final label = TextStyle(
      fontFamily: OsmiumTheme.fontFamily,
      fontSize: 14,
      fontWeight: FontWeight.w600,
      color: colors.onSurface,
    );

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 2),
      child: Row(
        children: [
          Expanded(
            child: Align(
              alignment: Alignment.centerLeft,
              child: MiuixOverlayIconDropdownMenu.entries(
                collapseOnSelection: true,
                minHeight: 34,
                minWidth: 34,
                entries: [
                  MiuixDropdownEntry(
                    items: [
                      MiuixDropdownItem(
                        text: '全部设备',
                        selected: !onlineOnly,
                        onClick: () => onOnlineOnlyChanged(false),
                      ),
                      MiuixDropdownItem(
                        text: '在线设备',
                        selected: onlineOnly,
                        onClick: () => onOnlineOnlyChanged(true),
                      ),
                    ],
                  ),
                  MiuixDropdownEntry(
                    items: [
                      for (final mode in _DeviceSortMode.values)
                        MiuixDropdownItem(
                          text: mode.label,
                          selected: mode == sortMode,
                          onClick: () => onSortModeChanged(mode),
                        ),
                    ],
                  ),
                ],
                child: Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 6),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Flexible(
                        child: Text.rich(
                          TextSpan(
                            text: onlineOnly ? '在线设备' : '全部设备',
                            children: [
                              TextSpan(
                                text: '  ·  ${sortMode.label}',
                                style: label.copyWith(
                                  fontWeight: FontWeight.w500,
                                  color: colors.onSurfaceVariantSummary,
                                ),
                              ),
                            ],
                          ),
                          style: label,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                      const SizedBox(width: 2),
                      Icon(
                        Icons.unfold_more_rounded,
                        size: 17,
                        color: colors.onSurfaceVariantSummary,
                      ),
                    ],
                  ),
                ),
              ),
            ),
          ),
          const SizedBox(width: 8),
          MiuixOverlayIconDropdownMenu(
            minHeight: 34,
            minWidth: 34,
            entry: MiuixDropdownEntry(
              items: [
                MiuixDropdownItem(
                  text: '列表',
                  selected: !gridMode,
                  icon: const Icon(Icons.view_agenda_outlined, size: 18),
                  onClick: () => onGridModeChanged(false),
                ),
                MiuixDropdownItem(
                  text: '网格',
                  selected: gridMode,
                  icon: const Icon(Icons.grid_view_rounded, size: 18),
                  onClick: () => onGridModeChanged(true),
                ),
              ],
            ),
            child: Icon(
              gridMode ? Icons.grid_view_rounded : Icons.view_agenda_outlined,
              size: 19,
              color: colors.onSurface,
              semanticLabel: '视图',
            ),
          ),
        ],
      ),
    );
  }
}

class _CollapsibleSectionHeader extends StatelessWidget {
  const _CollapsibleSectionHeader({
    required this.title,
    required this.expanded,
    required this.onTap,
  });

  final String title;
  final bool expanded;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final colors = MiuixTheme.of(context).colors;

    return MiuixPressable(
      onPressed: onTap,
      borderRadius: BorderRadius.circular(14),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(2, 8, 4, 10),
        child: Row(
          children: [
            Expanded(
              child: Text(
                title,
                style: TextStyle(
                  fontFamily: OsmiumTheme.fontFamily,
                  fontSize: 17,
                  fontWeight: FontWeight.w600,
                  color: colors.onSurface,
                ),
              ),
            ),
            AnimatedRotation(
              turns: expanded ? 0 : -0.25,
              duration: OsmiumMotion.durationOr(context, OsmiumMotion.small),
              curve: MiuixMotion.standardDecelerate,
              child: Icon(
                Icons.keyboard_arrow_down_rounded,
                size: 22,
                color: colors.onSurfaceVariantSummary,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _DeviceWallpaperCard extends StatelessWidget {
  const _DeviceWallpaperCard({
    super.key,
    required this.device,
    this.wallpaperPath,
    required this.compact,
    required this.powerBusy,
    required this.onConnect,
    required this.onMore,
    required this.onPowerAction,
  });

  final AccountDeviceDto device;
  final String? wallpaperPath;
  final bool compact;
  final bool powerBusy;
  final VoidCallback onConnect;
  final VoidCallback onMore;
  final ValueChanged<String> onPowerAction;

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;
    final online = device.isOnline;
    final controlled = device.isBeingControlled;
    final hasWallpaper = wallpaperPath != null && wallpaperPath!.isNotEmpty;
    final showWakeBtn =
        device.isComputerDevice &&
        device.allowRemotePower &&
        !online &&
        device.wolMacs.isNotEmpty;
    final showOnlinePowerBtns =
        device.isComputerDevice && device.allowRemotePower && online;

    final String idFormatted = DeviceIdentity.formatDeviceId(device.deviceId);
    final String subtitle = !online
        ? '$idFormatted · 离线'
        : (controlled
              ? '$idFormatted · 受控中'
              : (device.osVersion.isNotEmpty && !compact
                    ? '$idFormatted · ${device.osVersion}'
                    : idFormatted));

    final bool shortViewport = MediaQuery.sizeOf(context).height < 500;
    final double cardHeight = compact
        ? (shortViewport ? 126 : 156)
        : (shortViewport ? 128 : 202);

    Widget backdrop = hasWallpaper
        ? Image(
            image: FileImage(File(wallpaperPath!)),
            fit: BoxFit.cover,
            gaplessPlayback: true,
            filterQuality: FilterQuality.medium,
            errorBuilder: (_, _, _) => OsmiumWaveBackdrop(
              isDark: isDark,
              online: online,
              controlled: controlled,
            ),
          )
        : OsmiumWaveBackdrop(
            isDark: isDark,
            online: online,
            controlled: controlled,
          );
    if (!online) {
      backdrop = ColorFiltered(
        colorFilter: OsmiumColors.grayscaleFilter,
        child: backdrop,
      );
    }

    return OsmiumSurfaceCard(
      onPressed: online ? onConnect : onMore,
      onLongPress: onMore,
      child: SizedBox(
        height: cardHeight,
        width: double.infinity,
        child: Stack(
          fit: StackFit.expand,
          children: [
            backdrop,
            if (!online)
              ColoredBox(
                color: (isDark ? Colors.black : Colors.white).withValues(
                  alpha: isDark ? 0.28 : 0.20,
                ),
              ),
            if (showWakeBtn || showOnlinePowerBtns)
              Positioned(
                top: compact ? 8 : 10,
                right: compact ? 8 : 10,
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    if (showWakeBtn)
                      _CardPowerButton(
                        icon: Icons.power_settings_new_rounded,
                        label: '开机',
                        compact: compact,
                        isDark: isDark,
                        accent: true,
                        danger: false,
                        enabled: !powerBusy,
                        onTap: () => onPowerAction('wake'),
                      ),
                    if (showOnlinePowerBtns) ...[
                      _CardPowerButton(
                        icon: Icons.restart_alt_rounded,
                        label: '重启',
                        compact: compact,
                        isDark: isDark,
                        accent: false,
                        danger: false,
                        enabled: !powerBusy,
                        onTap: () => onPowerAction('restart'),
                      ),
                      SizedBox(width: compact ? 5 : 6),
                      _CardPowerButton(
                        icon: Icons.power_settings_new_rounded,
                        label: '关机',
                        compact: compact,
                        isDark: isDark,
                        accent: false,
                        danger: true,
                        enabled: !powerBusy,
                        onTap: () => onPowerAction('shutdown'),
                      ),
                    ],
                  ],
                ),
              ),
            Positioned(
              left: 0,
              right: 0,
              bottom: 0,
              child: ClipRect(
                child: BackdropFilter(
                  filter: OsmiumGlass.filter(18, saturation: online ? 1.6 : 1.0),
                  child: Container(
                    padding: compact
                        ? const EdgeInsets.fromLTRB(12, 9, 4, 10)
                        : const EdgeInsets.fromLTRB(16, 11, 6, 12),
                    decoration: BoxDecoration(
                      gradient: LinearGradient(
                        begin: Alignment.topCenter,
                        end: Alignment.bottomCenter,
                        colors: [
                          online
                              ? OsmiumColors.frostedOverlay(isDark)
                              : OsmiumColors.frostedOverlayOffline(isDark),
                          online
                              ? OsmiumColors.frostedOverlayBottom(isDark)
                              : OsmiumColors.frostedOverlayOfflineBottom(isDark),
                        ],
                      ),
                      border: Border(
                        top: BorderSide(
                          color: OsmiumColors.frostedDivider(isDark),
                          width: 0.8,
                        ),
                      ),
                    ),
                    child: Row(
                      children: [
                        Expanded(
                          child: Column(
                            mainAxisSize: MainAxisSize.min,
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                device.displayName,
                                maxLines: 1,
                                overflow: TextOverflow.ellipsis,
                                style: TextStyle(
                                  fontFamily: OsmiumTheme.fontFamily,
                                  fontSize: compact ? 14 : 16,
                                  fontWeight: FontWeight.w600,
                                  height: 1.25,
                                  color: online
                                      ? colors.onSurface
                                      : colors.onSurface.withValues(
                                          alpha: isDark ? 0.56 : 0.52,
                                        ),
                                ),
                              ),
                              const SizedBox(height: 3),
                              Text(
                                subtitle,
                                maxLines: 1,
                                overflow: TextOverflow.ellipsis,
                                style: OsmiumTheme.monoStyle(
                                  fontSize: compact ? 11 : 12.5,
                                  color: online
                                      ? colors.onSurfaceVariantSummary
                                      : colors.onSurfaceVariantSummary
                                            .withValues(alpha: 0.78),
                                ),
                              ),
                            ],
                          ),
                        ),
                        MiuixIconButton(
                          onPressed: onMore,
                          minWidth: 36,
                          minHeight: 36,
                          child: Icon(
                            Icons.more_horiz_rounded,
                            size: 20,
                            color: online
                                ? colors.onSurfaceVariantSummary
                                : colors.onSurfaceVariantSummary.withValues(
                                    alpha: 0.80,
                                  ),
                            semanticLabel: '更多',
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _CardPowerButton extends StatelessWidget {
  const _CardPowerButton({
    required this.icon,
    required this.label,
    required this.compact,
    required this.isDark,
    required this.accent,
    required this.danger,
    required this.enabled,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final bool compact;
  final bool isDark;
  final bool accent;
  final bool danger;
  final bool enabled;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final colors = MiuixTheme.of(context).colors;
    final Color fg = danger
        ? OsmiumColors.dangerFg(isDark)
        : (accent ? colors.primary : colors.onSurface);
    final Color effectiveFg = enabled ? fg : fg.withValues(alpha: 0.45);
    final Color bg = isDark
        ? Colors.black.withValues(alpha: 0.46)
        : Colors.white.withValues(alpha: 0.82);
    final Color border = isDark
        ? Colors.white.withValues(alpha: 0.16)
        : Colors.black.withValues(alpha: 0.08);
    final radius = BorderRadius.circular(999);

    return ClipRRect(
      borderRadius: radius,
      child: BackdropFilter(
        filter: OsmiumGlass.filter(16, saturation: 1.4),
        child: MiuixPressable(
          onPressed: enabled ? onTap : null,
          borderRadius: radius,
          feedbackType: MiuixPressFeedbackType.sink,
          sinkAmount: 0.94,
          child: Container(
            padding: compact
                ? const EdgeInsets.symmetric(horizontal: 8, vertical: 5)
                : const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
            decoration: BoxDecoration(
              color: bg,
              borderRadius: radius,
              border: Border.all(color: border, width: 0.75),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(
                  icon,
                  size: compact ? 13.5 : 14.5,
                  color: effectiveFg,
                ),
                const SizedBox(width: 4),
                Text(
                  label,
                  style: TextStyle(
                    fontFamily: OsmiumTheme.fontFamily,
                    fontSize: compact ? 11.5 : 12,
                    fontWeight: FontWeight.w600,
                    height: 1.1,
                    color: effectiveFg,
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// Quiet skeleton card shown on the very first device load.
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
      child: OsmiumSurfaceCard(
        child: FadeTransition(
          opacity: _breath,
          child: SizedBox(
            height: 202,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Expanded(child: ColoredBox(color: strong)),
                Padding(
                  padding: const EdgeInsets.fromLTRB(16, 14, 16, 14),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Container(
                        width: 132,
                        height: 14,
                        decoration: BoxDecoration(
                          color: base,
                          borderRadius: BorderRadius.circular(6),
                        ),
                      ),
                      const SizedBox(height: 8),
                      Container(
                        width: 96,
                        height: 11,
                        decoration: BoxDecoration(
                          color: base,
                          borderRadius: BorderRadius.circular(6),
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),
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

    return OsmiumSurfaceCard(
      onPressed: onLogin,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          SizedBox(
            height: 156,
            child: Stack(
              fit: StackFit.expand,
              children: [
                OsmiumWaveBackdrop(isDark: isDark),
                Padding(
                  padding: const EdgeInsets.fromLTRB(20, 20, 20, 18),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    mainAxisAlignment: MainAxisAlignment.end,
                    children: [
                      Container(
                        width: 48,
                        height: 48,
                        padding: const EdgeInsets.all(6),
                        decoration: ShapeDecoration(
                          color: Colors.white.withValues(alpha: 0.22),
                          shape: MiuixSquircleBorder(cornerRadius: 15),
                        ),
                        child: ClipRRect(
                          borderRadius: BorderRadius.circular(11),
                          child: Image.asset(
                            'assets/images/osmium_logo.png',
                            fit: BoxFit.cover,
                          ),
                        ),
                      ),
                      const SizedBox(height: 12),
                      const Text(
                        'Osmium',
                        style: TextStyle(
                          fontFamily: OsmiumTheme.fontFamily,
                          fontSize: 22,
                          fontWeight: FontWeight.w700,
                          height: 1.15,
                          color: Colors.white,
                          shadows: [
                            Shadow(
                              color: Color(0x45000000),
                              blurRadius: 8,
                              offset: Offset(0, 1),
                            ),
                          ],
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
          Padding(
            padding: const EdgeInsets.all(14),
            child: SizedBox(
              width: double.infinity,
              child: MiuixTextButton(
                '登录账号',
                onPressed: onLogin,
                colors: MiuixButtonDefaults.buttonColorsPrimary(context),
              ),
            ),
          ),
        ],
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

    return OsmiumSurfaceCard(
      padding: const EdgeInsets.fromLTRB(20, 32, 20, 24),
      child: Column(
        children: [
          OsmiumIconTile(
            icon: Icons.desktop_windows_rounded,
            size: 56,
            radius: 18,
            background: colors.secondaryContainer,
            foreground: colors.onSecondaryContainer,
          ),
          const SizedBox(height: 14),
          MiuixText('暂无设备', style: theme.textStyles.headline1),
          const SizedBox(height: 18),
          SizedBox(
            width: double.infinity,
            child: MiuixTextButton(
              '通过设备码连接',
              onPressed: onOpenConnect,
              colors: MiuixButtonDefaults.buttonColorsPrimary(context),
            ),
          ),
        ],
      ),
    );
  }
}

class _NoMatchCard extends StatelessWidget {
  const _NoMatchCard({required this.onReset});

  final VoidCallback onReset;

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);

    return OsmiumSurfaceCard(
      padding: const EdgeInsets.fromLTRB(20, 28, 20, 22),
      child: Column(
        children: [
          MiuixText('无匹配设备', style: theme.textStyles.headline2),
          const SizedBox(height: 14),
          MiuixTextButton('重置筛选', onPressed: onReset),
        ],
      ),
    );
  }
}
