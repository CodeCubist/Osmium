import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_miuix/miuix.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/device_identity.dart';
import '../../core/models/osmium_settings.dart';
import '../../core/theme/osmium_motion.dart';
import '../../core/theme/osmium_theme.dart';
import '../../services/session/remote_session_controller.dart';
import '../../state/app_controller.dart';
import '../shared/home_widgets.dart';
import '../shared/session_launcher.dart';
import '../widgets/osmium_motion_widgets.dart';

class ConnectTab extends ConsumerStatefulWidget {
  const ConnectTab({
    super.key,
    required this.onConnect,
    required this.bottomInset,
  });

  final SessionLauncher onConnect;
  final double bottomInset;

  @override
  ConsumerState<ConnectTab> createState() => _ConnectTabState();
}

class _ConnectTabState extends ConsumerState<ConnectTab> {
  final _scrollBehavior = MiuixExitUntilCollapsedScrollBehavior();
  final _deviceIdCtrl = TextEditingController();
  final _accessCodeCtrl = TextEditingController();
  final _deviceIdShake = GlobalKey<OsmiumShakeState>();
  final _connectButtonKey = GlobalKey();
  final Map<String, GlobalKey> _historyKeys = {};
  bool _viewOnly = false;
  bool _obscureAccess = true;
  bool _formattingDeviceId = false;
  bool _formattingAccessCode = false;
  bool _launching = false;
  String? _error;

  GlobalKey _historyKey(String deviceId) =>
      _historyKeys.putIfAbsent(deviceId, GlobalKey.new);

  @override
  void initState() {
    super.initState();
    _deviceIdCtrl.addListener(_formatDeviceIdField);
    _accessCodeCtrl.addListener(_normalizeAccessCodeField);
  }

  @override
  void dispose() {
    _deviceIdCtrl.removeListener(_formatDeviceIdField);
    _accessCodeCtrl.removeListener(_normalizeAccessCodeField);
    _deviceIdCtrl.dispose();
    _accessCodeCtrl.dispose();
    super.dispose();
  }

  void _formatDeviceIdField() {
    if (_formattingDeviceId) return;
    final digits = DeviceIdentity.normalizeDeviceId(_deviceIdCtrl.text);
    final clipped = digits.length > 9 ? digits.substring(0, 9) : digits;
    final formatted = DeviceIdentity.formatDeviceId(clipped);
    if (_deviceIdCtrl.text != formatted) {
      _formattingDeviceId = true;
      _deviceIdCtrl.value = TextEditingValue(
        text: formatted,
        selection: TextSelection.collapsed(offset: formatted.length),
      );
      _formattingDeviceId = false;
    }
    if (!mounted) return;
    setState(() {
      if (_error != null && clipped.length == 9) _error = null;
    });
  }

  void _normalizeAccessCodeField() {
    if (_formattingAccessCode) return;
    final current = _accessCodeCtrl.text;
    final normalized = current.toUpperCase();
    if (normalized != current) {
      _formattingAccessCode = true;
      final offset = _accessCodeCtrl.selection.baseOffset.clamp(
        0,
        normalized.length,
      );
      _accessCodeCtrl.value = TextEditingValue(
        text: normalized,
        selection: TextSelection.collapsed(offset: offset),
      );
      _formattingAccessCode = false;
    }
    if (!mounted) return;
    setState(() {});
  }

  Future<void> _pasteDeviceId() async {
    final data = await Clipboard.getData(Clipboard.kTextPlain);
    final text = data?.text ?? '';
    final digits = DeviceIdentity.normalizeDeviceId(text);
    if (digits.isNotEmpty) {
      final clipped = digits.length > 9 ? digits.substring(0, 9) : digits;
      _deviceIdCtrl.text = DeviceIdentity.formatDeviceId(clipped);
    }
  }

  void _connectNow() {
    if (_launching) return;
    final digits = DeviceIdentity.normalizeDeviceId(_deviceIdCtrl.text);
    if (digits.length != 9) {
      HapticFeedback.mediumImpact();
      _deviceIdShake.currentState?.shake();
      setState(() => _error = '请输入 9 位设备码');
      return;
    }
    final app = ref.read(appControllerProvider);
    final matched = app.accountDevices
        .where((d) => d.deviceId == digits)
        .firstOrNull;
    final accessCode = DeviceIdentity.normalizeAccessCode(_accessCodeCtrl.text);
    final sameAccount = matched != null && accessCode.isEmpty;

    _launch(
      SessionConnectRequest(
        targetDeviceId: digits,
        targetDeviceName:
            matched?.displayName ?? DeviceIdentity.formatDeviceId(digits),
        accessCode: accessCode,
        sameAccount: sameAccount,
        viewOnly: _viewOnly,
      ),
      sourceKey: _connectButtonKey,
    );
  }

  void _connectHistory(ConnectionHistoryEntry entry) {
    if (_launching) return;
    _deviceIdCtrl.text = DeviceIdentity.formatDeviceId(entry.deviceId);
    _accessCodeCtrl.text = entry.savedAccessCode;
    _launch(
      SessionConnectRequest(
        targetDeviceId: entry.deviceId,
        targetDeviceName: entry.deviceName,
        accessCode: entry.savedAccessCode,
        sameAccount: entry.sameAccount,
        viewOnly: _viewOnly,
      ),
      sourceKey: _historyKey(entry.deviceId),
    );
  }

  /// Crossfades the pressed control into a spinner, then hands off to the
  /// container-transform route. The tab resets once the session covers it.
  void _launch(SessionConnectRequest request, {GlobalKey? sourceKey}) {
    setState(() => _launching = true);
    widget.onConnect(request, sourceKey: sourceKey);
    Future<void>.delayed(OsmiumMotion.containerTransform).then((_) {
      if (mounted) setState(() => _launching = false);
    });
  }

  @override
  Widget build(BuildContext context) {
    final app = ref.watch(appControllerProvider);
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;
    final keyboardInset = MediaQuery.viewInsetsOf(context).bottom;
    final scrollBottom = keyboardInset > 0 ? 20.0 : widget.bottomInset + 24;

    final digits = DeviceIdentity.normalizeDeviceId(_deviceIdCtrl.text);
    final matched = digits.length == 9
        ? app.accountDevices.where((d) => d.deviceId == digits).firstOrNull
        : null;
    final accessCodeEmpty = DeviceIdentity.normalizeAccessCode(
      _accessCodeCtrl.text,
    ).isEmpty;
    final sameAccount = matched != null && accessCodeEmpty;
    final encryptionOn = app.settings.enableEncryption;

    return MiuixScaffold(
      topBar: MiuixTopAppBar(
        title: '连接',
        blurred: true,
        scrollBehavior: _scrollBehavior,
      ),
      content: (contentPadding) {
        return Padding(
          padding: EdgeInsets.only(bottom: keyboardInset),
          child: MiuixScrollBehaviorListener(
            behavior: _scrollBehavior,
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
                    MiuixCard(
                      cornerRadius: 26,
                      insideMargin: const EdgeInsets.fromLTRB(18, 18, 18, 18),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          OsmiumShake(
                            key: _deviceIdShake,
                            child: MiuixTextField(
                              controller: _deviceIdCtrl,
                              label: '9 位设备码',
                              singleLine: true,
                              keyboardType: TextInputType.number,
                              textInputAction: TextInputAction.next,
                              insideMargin: const EdgeInsets.symmetric(
                                horizontal: 14,
                                vertical: 18,
                              ),
                              textStyle: OsmiumTheme.monoStyle(
                                fontSize: 24,
                                fontWeight: FontWeight.w600,
                                letterSpacing: 2.0,
                                color: colors.onSurface,
                              ),
                              trailingIcon: Row(
                                mainAxisSize: MainAxisSize.min,
                                children: [
                                  MiuixIconButton(
                                    onPressed: _pasteDeviceId,
                                    minWidth: 34,
                                    minHeight: 34,
                                    child: Icon(
                                      Icons.content_paste_rounded,
                                      size: 18,
                                      color: colors.onSurfaceVariantSummary,
                                    ),
                                  ),
                                  if (_deviceIdCtrl.text.isNotEmpty)
                                    MiuixIconButton(
                                      onPressed: () {
                                        _deviceIdCtrl.clear();
                                        OsmiumHaptics.selection();
                                      },
                                      minWidth: 34,
                                      minHeight: 34,
                                      child: Icon(
                                        Icons.close_rounded,
                                        size: 18,
                                        color: colors.onSurfaceVariantSummary,
                                      ),
                                    ),
                                ],
                              ),
                            ),
                          ),
                          const SizedBox(height: 12),
                          MiuixTextField(
                            controller: _accessCodeCtrl,
                            label: '访问密码',
                            singleLine: true,
                            obscureText: _obscureAccess,
                            textCapitalization: TextCapitalization.characters,
                            textInputAction: TextInputAction.done,
                            onSubmitted: (_) => _connectNow(),
                            textStyle: OsmiumTheme.monoStyle(
                              fontSize: 16,
                              fontWeight: FontWeight.w500,
                              letterSpacing: 1.2,
                              color: colors.onSurface,
                            ),
                            trailingIcon: MiuixIconButton(
                              onPressed: () {
                                setState(
                                  () => _obscureAccess = !_obscureAccess,
                                );
                                OsmiumHaptics.selection();
                              },
                              minWidth: 34,
                              minHeight: 34,
                              child: Icon(
                                _obscureAccess
                                    ? Icons.visibility_off_outlined
                                    : Icons.visibility_outlined,
                                size: 18,
                                color: colors.onSurfaceVariantSummary,
                              ),
                            ),
                          ),
                          const SizedBox(height: 14),
                          Row(
                            children: [
                              _ViewOnlyChip(
                                selected: _viewOnly,
                                onTap: () {
                                  OsmiumHaptics.selection();
                                  setState(() => _viewOnly = !_viewOnly);
                                },
                              ),
                              const Spacer(),
                              if (sameAccount) ...[
                                OsmiumStatusPill(
                                  label: '免密',
                                  icon: Icons.lock_open_rounded,
                                  background: colors.tertiaryContainer,
                                  foreground: colors.onTertiaryContainer,
                                ),
                                const SizedBox(width: 8),
                              ],
                              Tooltip(
                                message: encryptionOn ? '端到端加密已开启' : '端到端加密已关闭',
                                child: OsmiumIconTile(
                                  icon: encryptionOn
                                      ? Icons.lock_rounded
                                      : Icons.lock_open_rounded,
                                  size: 34,
                                  radius: 11,
                                  background: encryptionOn
                                      ? colors.tertiaryContainer
                                      : OsmiumColors.warnBg(isDark),
                                  foreground: encryptionOn
                                      ? colors.onTertiaryContainer
                                      : OsmiumColors.warnFg(isDark),
                                ),
                              ),
                            ],
                          ),
                          OsmiumAnimatedReveal(
                            visible: _error != null,
                            child: Padding(
                              padding: const EdgeInsets.only(top: 12),
                              child: Container(
                                padding: const EdgeInsets.symmetric(
                                  horizontal: 12,
                                  vertical: 8,
                                ),
                                decoration: ShapeDecoration(
                                  color: OsmiumColors.dangerBg(isDark),
                                  shape: MiuixSquircleBorder(cornerRadius: 12),
                                ),
                                child: MiuixText(
                                  _error ?? '请输入 9 位设备码',
                                  style: theme.textStyles.body2,
                                  color: OsmiumColors.dangerFg(isDark),
                                ),
                              ),
                            ),
                          ),
                          const SizedBox(height: 16),
                          SizedBox(
                            key: _connectButtonKey,
                            width: double.infinity,
                            child: MiuixButton(
                              onPressed: _connectNow,
                              colors: MiuixButtonDefaults.buttonColorsPrimary(
                                context,
                              ),
                              cornerRadius: 16,
                              minWidth: 0,
                              minHeight: 52,
                              child: AnimatedSwitcher(
                                duration: OsmiumMotion.durationOr(
                                  context,
                                  OsmiumMotion.small,
                                ),
                                switchInCurve: MiuixMotion.standardDecelerate,
                                switchOutCurve: MiuixMotion.standardAccelerate,
                                transitionBuilder: (child, animation) =>
                                    FadeTransition(
                                      opacity: animation,
                                      child: ScaleTransition(
                                        scale: Tween<double>(
                                          begin: 0.9,
                                          end: 1,
                                        ).animate(animation),
                                        child: child,
                                      ),
                                    ),
                                child: _launching
                                    ? SizedBox(
                                        key: const ValueKey('launching'),
                                        width: 20,
                                        height: 20,
                                        child: CircularProgressIndicator(
                                          strokeWidth: 2.2,
                                          color: colors.onPrimary,
                                        ),
                                      )
                                    : Row(
                                        key: const ValueKey('idle'),
                                        mainAxisSize: MainAxisSize.min,
                                        children: [
                                          Icon(
                                            Icons.bolt_rounded,
                                            size: 19,
                                            color: colors.onPrimary,
                                          ),
                                          const SizedBox(width: 6),
                                          const Text(
                                            '立即连接',
                                            style: TextStyle(
                                              fontSize: 16,
                                              fontWeight: FontWeight.w600,
                                            ),
                                          ),
                                        ],
                                      ),
                              ),
                            ),
                          ),
                        ],
                      ),
                    ),
                    if (app.history.isNotEmpty) ...[
                      const OsmiumSectionHeader('最近连接'),
                      MiuixCard(
                        cornerRadius: 20,
                        insideMargin: EdgeInsets.zero,
                        child: Column(
                          children: [
                            for (int i = 0; i < app.history.length; i++) ...[
                              if (i > 0) const OsmiumCardDivider(),
                              _HistoryRow(
                                key: _historyKey(app.history[i].deviceId),
                                entry: app.history[i],
                                onTap: () => _connectHistory(app.history[i]),
                                onDelete: () => app.removeHistoryEntry(
                                  app.history[i].deviceId,
                                ),
                              ),
                            ],
                          ],
                        ),
                      ),
                    ],
                  ],
                ),
              ),
            ),
          ),
        );
      },
    );
  }
}

class _ViewOnlyChip extends StatelessWidget {
  const _ViewOnlyChip({required this.selected, required this.onTap});

  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final colors = MiuixTheme.of(context).colors;
    final background = selected ? colors.primary : colors.secondaryContainer;
    final foreground = selected
        ? colors.onPrimary
        : colors.onSecondaryContainer;

    final reduced = OsmiumMotion.reduced(context);

    return MiuixPressable(
      onPressed: onTap,
      borderRadius: BorderRadius.circular(999),
      feedbackType: MiuixPressFeedbackType.sink,
      sinkAmount: 0.96,
      child: TweenAnimationBuilder<double>(
        // Re-keying on the state makes the playful spring replay per toggle.
        key: ValueKey(selected),
        tween: Tween<double>(begin: reduced ? 1 : 0.92, end: 1),
        duration: reduced ? Duration.zero : const Duration(milliseconds: 300),
        curve: SpringCurve(damping: 0.55, response: 0.3),
        builder: (context, scale, child) =>
            Transform.scale(scale: scale, child: child),
        child: AnimatedContainer(
          duration: OsmiumMotion.durationOr(context, OsmiumMotion.small),
          curve: MiuixMotion.standardDecelerate,
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
          decoration: ShapeDecoration(
            color: background,
            shape: const StadiumBorder(),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(
                selected ? Icons.visibility_rounded : Icons.visibility_outlined,
                size: 16,
                color: foreground,
              ),
              const SizedBox(width: 6),
              Text(
                '仅观看',
                style: TextStyle(
                  fontFamily: OsmiumTheme.fontFamily,
                  fontSize: 13,
                  fontWeight: FontWeight.w600,
                  color: foreground,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _HistoryRow extends StatefulWidget {
  const _HistoryRow({
    super.key,
    required this.entry,
    required this.onTap,
    required this.onDelete,
  });

  final ConnectionHistoryEntry entry;
  final VoidCallback onTap;
  final VoidCallback onDelete;

  @override
  State<_HistoryRow> createState() => _HistoryRowState();
}

class _HistoryRowState extends State<_HistoryRow> {
  bool _removing = false;

  void _delete() {
    if (_removing) return;
    OsmiumHaptics.selection();
    setState(() => _removing = true);
  }

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final entry = widget.entry;

    return OsmiumCollapse(
      collapsed: _removing,
      onCollapsed: widget.onDelete,
      child: MiuixPressable(
        onPressed: widget.onTap,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 12, 8, 12),
          child: Row(
            children: [
              OsmiumIconTile(
                icon: Icons.desktop_windows_rounded,
                size: 40,
                radius: 12,
                background: colors.secondaryContainer,
                foreground: colors.onSecondaryContainer,
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    MiuixText(
                      entry.deviceName,
                      style: theme.textStyles.body1,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                    const SizedBox(height: 3),
                    Row(
                      children: [
                        Flexible(
                          child: Text(
                            DeviceIdentity.formatDeviceId(entry.deviceId),
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: OsmiumTheme.monoStyle(
                              fontSize: 12,
                              color: colors.onSurfaceVariantSummary,
                            ),
                          ),
                        ),
                        if (entry.sameAccount) ...[
                          const SizedBox(width: 8),
                          OsmiumStatusPill(
                            label: '免密',
                            icon: Icons.lock_open_rounded,
                            background: colors.tertiaryContainer,
                            foreground: colors.onTertiaryContainer,
                            fontSize: 10,
                          ),
                        ],
                      ],
                    ),
                  ],
                ),
              ),
              Tooltip(
                message: '删除记录',
                child: MiuixIconButton(
                  onPressed: _delete,
                  minWidth: 36,
                  minHeight: 36,
                  child: Icon(
                    Icons.close_rounded,
                    size: 18,
                    color: colors.onSurfaceVariantSummary,
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
