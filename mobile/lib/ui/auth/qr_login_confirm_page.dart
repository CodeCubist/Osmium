import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_miuix/miuix.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/constants/osmium_constants.dart';
import '../../core/models/account_models.dart';
import '../../core/models/device_identity.dart';
import '../../core/theme/osmium_motion.dart';
import '../../core/theme/osmium_theme.dart';
import '../../state/app_controller.dart';
import '../shared/home_widgets.dart';
import 'captcha_webview_page.dart';

enum _QrFlowStage { verifying, awaitingConfirm, submitting, confirmed, error }

class QrLoginConfirmPage extends ConsumerStatefulWidget {
  const QrLoginConfirmPage({super.key, required this.request});

  final QrParsedRequest request;

  @override
  ConsumerState<QrLoginConfirmPage> createState() => _QrLoginConfirmPageState();
}

class _QrLoginConfirmPageState extends ConsumerState<QrLoginConfirmPage> {
  static const int _minConfirmCooldownSec = 3;

  _QrFlowStage _stage = _QrFlowStage.verifying;
  QrConfirmTicket? _ticket;
  String? _error;

  DateTime? _unlockAt;
  int _cooldownSec = _minConfirmCooldownSec;
  Timer? _cooldownTimer;
  bool _resolvedOnServer = false;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _runVerificationHandshake();
    });
  }

  @override
  void dispose() {
    _cooldownTimer?.cancel();
    final ticket = _ticket;
    if (ticket != null && !_resolvedOnServer) {
      _resolvedOnServer = true;
      final svc = ref.read(accountServiceProvider);
      unawaited(
        svc.confirmMobileQrLogin(
          qid: ticket.qrId,
          confirmToken: ticket.confirmToken,
          approve: false,
        ),
      );
    }
    super.dispose();
  }

  Future<void> _runVerificationHandshake() async {
    if (!mounted) return;
    setState(() {
      _stage = _QrFlowStage.verifying;
      _error = null;
    });

    final svc = ref.read(accountServiceProvider);

    // 1. Register mobile QR scan with custom header (in_app / external).
    final regRes = await svc.registerMobileQrLogin(widget.request);
    if (!mounted) return;
    if (!regRes.ok || regRes.data == null) {
      _fail(regRes.message.isNotEmpty ? regRes.message : '扫码注册失败');
      return;
    }

    // 2. Request the QR URL with app-verify headers to receive the Geetest challenge.
    final verifyRes = await svc.requestQrVerifyChallenge(regRes.data!);
    if (!mounted) return;

    final Map<String, dynamic> ticketMap;
    if (verifyRes.needsCaptcha && verifyRes.challenge != null) {
      final solved = await Navigator.of(context).push<Map<String, dynamic>>(
        MaterialPageRoute(
          builder: (_) => CaptchaWebViewPage(
            challenge: verifyRes.challenge!,
            accountService: svc,
          ),
        ),
      );
      if (!mounted) return;
      if (solved == null) {
        Navigator.of(context).pop(false);
        return;
      }
      ticketMap = solved;
    } else if (verifyRes.ok && verifyRes.data != null) {
      ticketMap = verifyRes.data!;
    } else {
      _fail(verifyRes.message.isNotEmpty ? verifyRes.message : '安全验证发起失败');
      return;
    }

    final ticket = QrConfirmTicket.fromJson(ticketMap);
    if (ticket.qrId.isEmpty || ticket.confirmToken.isEmpty) {
      _fail('服务端返回的确认凭据无效');
      return;
    }

    final nowMs = DateTime.now().millisecondsSinceEpoch;
    final fromServerUnlock = ticket.confirmUnlockAtMs > nowMs
        ? ((ticket.confirmUnlockAtMs - nowMs) / 1000).ceil()
        : 0;
    final initialCooldown = [
      _minConfirmCooldownSec,
      ticket.confirmDelaySec,
      fromServerUnlock,
    ].reduce((a, b) => a > b ? a : b);

    OsmiumHaptics.selection();
    setState(() {
      _ticket = ticket;
      _stage = _QrFlowStage.awaitingConfirm;
    });
    _startCooldown(initialCooldown);
  }

  void _startCooldown(int seconds) {
    _cooldownTimer?.cancel();
    final safeSec = seconds < 1 ? 1 : seconds;
    _unlockAt = DateTime.now().add(Duration(seconds: safeSec));
    setState(() => _cooldownSec = safeSec);

    _cooldownTimer = Timer.periodic(const Duration(milliseconds: 200), (timer) {
      if (!mounted) {
        timer.cancel();
        return;
      }
      final remainingMs = _unlockAt!.difference(DateTime.now()).inMilliseconds;
      if (remainingMs <= 0) {
        timer.cancel();
        setState(() => _cooldownSec = 0);
      } else {
        final nextSec = (remainingMs / 1000).ceil();
        if (nextSec != _cooldownSec) {
          setState(() => _cooldownSec = nextSec);
        }
      }
    });
  }

  void _fail(String message) {
    if (!mounted) return;
    _cooldownTimer?.cancel();
    setState(() {
      _stage = _QrFlowStage.error;
      _error = message;
    });
  }

  Future<void> _confirm() async {
    final ticket = _ticket;
    if (ticket == null ||
        _cooldownSec > 0 ||
        _stage != _QrFlowStage.awaitingConfirm) {
      return;
    }
    OsmiumHaptics.confirm();
    setState(() {
      _stage = _QrFlowStage.submitting;
      _error = null;
    });

    final svc = ref.read(accountServiceProvider);
    final res = await svc.confirmMobileQrLogin(
      qid: ticket.qrId,
      confirmToken: ticket.confirmToken,
      approve: true,
    );
    if (!mounted) return;

    if (res.ok) {
      _resolvedOnServer = true;
      _cooldownTimer?.cancel();
      OsmiumHaptics.confirm();
      setState(() => _stage = _QrFlowStage.confirmed);
      await Future<void>.delayed(const Duration(milliseconds: 900));
      if (mounted) Navigator.of(context).pop(true);
      return;
    }

    if (res.code == OsmiumErrorCode.qrConfirmCooldown ||
        res.httpStatusCode == 425) {
      final retryAfter = (res.data?['retry_after'] as num?)?.toInt() ?? 2;
      setState(() => _stage = _QrFlowStage.awaitingConfirm);
      _startCooldown(retryAfter);
      return;
    }

    _resolvedOnServer = true;
    _fail(res.message.isNotEmpty ? res.message : '确认登录失败');
  }

  Future<void> _cancel() async {
    final ticket = _ticket;
    if (ticket != null && !_resolvedOnServer) {
      _resolvedOnServer = true;
      _cooldownTimer?.cancel();
      final svc = ref.read(accountServiceProvider);
      await svc.confirmMobileQrLogin(
        qid: ticket.qrId,
        confirmToken: ticket.confirmToken,
        approve: false,
      );
    }
    if (mounted) Navigator.of(context).pop(false);
  }

  static String _formatLoginTime(DateTime? dt) {
    if (dt == null) return '-';
    String two(int n) => n.toString().padLeft(2, '0');
    return '${dt.year}-${two(dt.month)}-${two(dt.day)} '
        '${two(dt.hour)}:${two(dt.minute)}:${two(dt.second)}';
  }

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;

    return MiuixScaffold(
      topBar: MiuixSmallTopAppBar(
        title: '扫码登录',
        navigationIcon: IconButton(
          icon: Icon(Icons.close_rounded, color: colors.onSurface),
          onPressed: _cancel,
        ),
      ),
      content: (contentPadding) {
        return Padding(
          padding: EdgeInsets.fromLTRB(
            20,
            contentPadding.top + 16,
            20,
            contentPadding.bottom + 24,
          ),
          child: Center(
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 420),
              child: switch (_stage) {
                _QrFlowStage.verifying => _buildVerifying(theme, colors),
                _QrFlowStage.awaitingConfirm || _QrFlowStage.submitting =>
                  _buildConfirmation(theme, colors, isDark),
                _QrFlowStage.confirmed => _buildConfirmed(
                  theme,
                  colors,
                  isDark,
                ),
                _QrFlowStage.error => _buildError(theme, colors, isDark),
              },
            ),
          ),
        );
      },
    );
  }

  Widget _buildVerifying(MiuixThemeData theme, MiuixColors colors) {
    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        SizedBox(
          width: 32,
          height: 32,
          child: CircularProgressIndicator(
            strokeWidth: 2.6,
            color: colors.primary,
          ),
        ),
        const SizedBox(height: 16),
        MiuixText(
          '正在验证二维码…',
          style: theme.textStyles.body1,
          color: colors.onSurfaceVariantSummary,
        ),
      ],
    );
  }

  Widget _buildConfirmation(
    MiuixThemeData theme,
    MiuixColors colors,
    bool isDark,
  ) {
    final ticket = _ticket!;
    final pc = ticket.pcInfo;
    final locked = _cooldownSec > 0;
    final submitting = _stage == _QrFlowStage.submitting;
    final canConfirm = !locked && !submitting;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const Spacer(flex: 1),
        Column(
          children: [
            Container(
              width: 68,
              height: 68,
              decoration: BoxDecoration(
                color: colors.primary.withValues(alpha: isDark ? 0.18 : 0.10),
                borderRadius: BorderRadius.circular(20),
              ),
              child: Icon(
                Icons.desktop_windows_rounded,
                size: 34,
                color: colors.primary,
              ),
            ),
            const SizedBox(height: 16),
            MiuixText(
              pc.deviceName.isNotEmpty ? pc.deviceName : 'Osmium PC',
              style: theme.textStyles.title3.copyWith(
                fontWeight: FontWeight.w600,
              ),
              color: colors.onSurface,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
            const SizedBox(height: 8),
            Wrap(
              spacing: 8,
              runSpacing: 6,
              alignment: WrapAlignment.center,
              children: [
                if (pc.osVersion.isNotEmpty)
                  OsmiumStatusPill(
                    label: pc.osVersion,
                    background: colors.secondaryContainer,
                    foreground: colors.onSurfaceVariantSummary,
                  ),
                if (ticket.isExternal)
                  OsmiumStatusPill(
                    label: '外部扫码',
                    background: OsmiumColors.warnBg(isDark),
                    foreground: OsmiumColors.warnFg(isDark),
                  ),
              ],
            ),
          ],
        ),
        const SizedBox(height: 28),
        MiuixCard(
          insideMargin: const EdgeInsets.symmetric(horizontal: 18, vertical: 6),
          child: Column(
            children: [
              _InfoRow(
                label: '登录 IP',
                value: pc.ipMasked.isNotEmpty ? pc.ipMasked : '-',
                mono: true,
              ),
              const MiuixHorizontalDivider(),
              _InfoRow(
                label: '归属地',
                value: pc.ipLocation.isNotEmpty ? pc.ipLocation : '未知位置',
              ),
              const MiuixHorizontalDivider(),
              _InfoRow(
                label: '登录时间',
                value: _formatLoginTime(pc.loginDateTime),
                mono: true,
              ),
              if (pc.deviceId.isNotEmpty) ...[
                const MiuixHorizontalDivider(),
                _InfoRow(
                  label: '设备码',
                  value: DeviceIdentity.formatDeviceId(pc.deviceId),
                  mono: true,
                ),
              ],
            ],
          ),
        ),
        const Spacer(flex: 2),
        SizedBox(
          width: double.infinity,
          child: MiuixButton(
            enabled: canConfirm,
            onPressed: canConfirm ? _confirm : null,
            minHeight: 44,
            colors: MiuixButtonColors(
              color: colors.primary,
              disabledColor: colors.disabledSecondaryVariant,
              contentColor: colors.onPrimary,
              disabledContentColor: colors.disabledOnSecondaryVariant,
            ),
            child: submitting
                ? SizedBox(
                    width: 20,
                    height: 20,
                    child: CircularProgressIndicator(
                      strokeWidth: 2.2,
                      color: colors.onPrimary,
                    ),
                  )
                : Text(
                    locked ? '确认登录 (${_cooldownSec}s)' : '确认登录',
                    style: theme.textStyles.button.copyWith(
                      fontWeight: FontWeight.w600,
                    ),
                  ),
          ),
        ),
        const SizedBox(height: 10),
        SizedBox(
          width: double.infinity,
          child: MiuixTextButton(
            '取消登录',
            enabled: !submitting,
            onPressed: submitting ? null : _cancel,
            minHeight: 44,
          ),
        ),
      ],
    );
  }

  Widget _buildConfirmed(
    MiuixThemeData theme,
    MiuixColors colors,
    bool isDark,
  ) {
    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        Container(
          width: 64,
          height: 64,
          decoration: BoxDecoration(
            color: OsmiumColors.onlineBg(isDark),
            shape: BoxShape.circle,
          ),
          child: Icon(
            Icons.check_rounded,
            size: 34,
            color: OsmiumColors.onlineFg(isDark),
          ),
        ),
        const SizedBox(height: 16),
        MiuixText(
          '已确认登录',
          style: theme.textStyles.title4.copyWith(fontWeight: FontWeight.w600),
          color: colors.onSurface,
        ),
      ],
    );
  }

  Widget _buildError(MiuixThemeData theme, MiuixColors colors, bool isDark) {
    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        Container(
          width: 60,
          height: 60,
          decoration: BoxDecoration(
            color: OsmiumColors.dangerBg(isDark),
            shape: BoxShape.circle,
          ),
          child: Icon(
            Icons.error_outline_rounded,
            size: 30,
            color: OsmiumColors.dangerFg(isDark),
          ),
        ),
        const SizedBox(height: 16),
        MiuixText(
          _error ?? '扫码登录失败',
          style: theme.textStyles.body1,
          color: colors.onSurface,
          textAlign: TextAlign.center,
        ),
        const SizedBox(height: 22),
        SizedBox(
          width: 180,
          child: MiuixTextButton(
            '返回',
            onPressed: () => Navigator.of(context).pop(false),
            minHeight: 44,
            colors: MiuixButtonDefaults.buttonColorsPrimary(context),
          ),
        ),
      ],
    );
  }
}

class _InfoRow extends StatelessWidget {
  const _InfoRow({required this.label, required this.value, this.mono = false});

  final String label;
  final String value;
  final bool mono;

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 14),
      child: Row(
        children: [
          MiuixText(
            label,
            style: theme.textStyles.body2,
            color: colors.onSurfaceVariantSummary,
          ),
          const SizedBox(width: 16),
          Expanded(
            child: Text(
              value,
              textAlign: TextAlign.end,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: mono
                  ? OsmiumTheme.monoStyle(fontSize: 14, color: colors.onSurface)
                  : theme.textStyles.body2.copyWith(
                      color: colors.onSurface,
                      fontWeight: FontWeight.w500,
                    ),
            ),
          ),
        ],
      ),
    );
  }
}
