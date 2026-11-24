import 'package:flutter/material.dart';
import 'package:flutter_miuix/miuix.dart';

import '../../../core/theme/osmium_motion.dart';
import '../../../core/theme/osmium_theme.dart';
import '../../../services/session/remote_session_controller.dart';
import 'session_signals.dart';

/// Staged connection transition & error recovery overlay.
///
/// It crossfades away as the first video frame fades in, keeps the logo
/// breathing while the host negotiates, and swaps status lines instead of
/// snapping between them.
class ConnectTransitionOverlay extends StatefulWidget {
  const ConnectTransitionOverlay({
    super.key,
    required this.signals,
    required this.onCancel,
    required this.onRetry,
  });

  final SessionSignals signals;
  final VoidCallback onCancel;
  final VoidCallback onRetry;

  @override
  State<ConnectTransitionOverlay> createState() =>
      _ConnectTransitionOverlayState();
}

class _ConnectTransitionOverlayState extends State<ConnectTransitionOverlay>
    with TickerProviderStateMixin {
  late final AnimationController _fade;
  late final AnimationController _breathe;
  late final Listenable _visibility;

  @override
  void initState() {
    super.initState();
    _fade = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 280),
      reverseDuration: const Duration(milliseconds: 360),
      value: 1,
    );
    _breathe = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 2200),
    );
    _visibility = Listenable.merge([
      widget.signals.phase,
      widget.signals.firstFrame,
    ])..addListener(_syncVisibility);
  }

  bool _ready = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (_ready) return;
    _ready = true;
    _syncVisibility();
  }

  @override
  void dispose() {
    _visibility.removeListener(_syncVisibility);
    _fade.dispose();
    _breathe.dispose();
    super.dispose();
  }

  void _syncVisibility() {
    if (!mounted || !_ready) return;
    final phase = widget.signals.phase.value;
    final firstFrame = widget.signals.firstFrame.value;
    final show = !(phase == SessionPhase.streaming && firstFrame);
    final reduced = OsmiumMotion.reduced(context);

    if (show) {
      if (_fade.value < 1) {
        if (reduced) {
          _fade.value = 1;
        } else {
          _fade.forward();
        }
      }
      if (!reduced && !_breathe.isAnimating) _breathe.repeat(reverse: true);
    } else {
      _breathe.stop();
      if (_fade.value > 0) {
        if (reduced) {
          _fade.value = 0;
        } else {
          _fade.reverse();
        }
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: _fade,
      builder: (context, child) {
        final v = _fade.value;
        if (v == 0) return const SizedBox.shrink();
        return IgnorePointer(
          ignoring: v < 0.05,
          child: Opacity(opacity: v, child: child),
        );
      },
      child: AnimatedBuilder(
        animation: Listenable.merge([
          widget.signals.phase,
          widget.signals.statusText,
          widget.signals.errorMessage,
          widget.signals.hostName,
          widget.signals.firstFrame,
        ]),
        builder: (context, _) => _buildContent(context),
      ),
    );
  }

  Widget _buildContent(BuildContext context) {
    final phase = widget.signals.phase.value;
    final isError = phase == SessionPhase.failed || phase == SessionPhase.ended;
    final status = phase == SessionPhase.streaming
        ? '等待首帧画面'
        : widget.signals.statusText.value;
    final reduce = OsmiumMotion.reduced(context);

    final logo = Image.asset(
      'assets/images/osmium_logo.png',
      width: 56,
      height: 56,
      fit: BoxFit.contain,
    );

    return Container(
      color: const Color(0xF20B0D12),
      alignment: Alignment.center,
      padding: const EdgeInsets.symmetric(horizontal: 32),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (reduce)
            logo
          else
            ScaleTransition(
              scale: Tween<double>(begin: 1.0, end: 1.035).animate(
                CurvedAnimation(parent: _breathe, curve: Curves.easeInOutSine),
              ),
              child: logo,
            ),
          const SizedBox(height: 18),
          ValueListenableBuilder<String>(
            valueListenable: widget.signals.hostName,
            builder: (context, name, _) => Text(
              name,
              style: const TextStyle(
                fontFamily: OsmiumTheme.fontFamily,
                fontSize: 19,
                fontWeight: FontWeight.w600,
                color: Colors.white,
              ),
            ),
          ),
          const SizedBox(height: 10),
          AnimatedSize(
            duration: OsmiumMotion.durationOr(context, OsmiumMotion.small),
            curve: MiuixMotion.standardDecelerate,
            alignment: Alignment.topCenter,
            child: AnimatedSwitcher(
              duration: OsmiumMotion.durationOr(context, OsmiumMotion.small),
              switchInCurve: MiuixMotion.standardDecelerate,
              switchOutCurve: MiuixMotion.standardAccelerate,
              transitionBuilder: (child, animation) => FadeTransition(
                opacity: animation,
                child: ScaleTransition(
                  scale: Tween<double>(begin: 0.97, end: 1).animate(animation),
                  child: child,
                ),
              ),
              child: isError
                  ? _ErrorBody(
                      key: const ValueKey('error'),
                      message: widget.signals.errorMessage.value ?? '连接已断开',
                      onCancel: widget.onCancel,
                      onRetry: widget.onRetry,
                    )
                  : _ProgressBody(
                      key: const ValueKey('progress'),
                      status: status,
                      onCancel: widget.onCancel,
                    ),
            ),
          ),
        ],
      ),
    );
  }
}

class _ProgressBody extends StatelessWidget {
  const _ProgressBody({
    super.key,
    required this.status,
    required this.onCancel,
  });

  final String status;
  final VoidCallback onCancel;

  @override
  Widget build(BuildContext context) {
    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        const SizedBox(
          width: 22,
          height: 22,
          child: CircularProgressIndicator(
            strokeWidth: 2.2,
            valueColor: AlwaysStoppedAnimation(OsmiumColors.accentOnGlass),
          ),
        ),
        const SizedBox(height: 12),
        AnimatedSwitcher(
          duration: OsmiumMotion.durationOr(context, OsmiumMotion.quick),
          switchInCurve: MiuixMotion.standardDecelerate,
          switchOutCurve: MiuixMotion.standardAccelerate,
          transitionBuilder: (child, animation) => FadeTransition(
            opacity: animation,
            child: SlideTransition(
              position: Tween<Offset>(
                begin: const Offset(0, 0.3),
                end: Offset.zero,
              ).animate(animation),
              child: child,
            ),
          ),
          child: Text(
            status,
            key: ValueKey(status),
            style: const TextStyle(
              fontFamily: OsmiumTheme.fontFamily,
              fontSize: 13.5,
              color: Colors.white70,
            ),
          ),
        ),
        const SizedBox(height: 28),
        MiuixButton(
          onPressed: onCancel,
          colors: MiuixButtonColors(
            color: Colors.white.withValues(alpha: 0.12),
            disabledColor: Colors.white10,
            contentColor: Colors.white,
            disabledContentColor: Colors.white38,
          ),
          cornerRadius: 10,
          child: const Text('取消'),
        ),
      ],
    );
  }
}

class _ErrorBody extends StatelessWidget {
  const _ErrorBody({
    super.key,
    required this.message,
    required this.onCancel,
    required this.onRetry,
  });

  final String message;
  final VoidCallback onCancel;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          message,
          textAlign: TextAlign.center,
          style: const TextStyle(
            fontFamily: OsmiumTheme.fontFamily,
            fontSize: 14,
            color: OsmiumColors.dangerFgDark,
          ),
        ),
        const SizedBox(height: 24),
        Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            MiuixButton(
              onPressed: onCancel,
              colors: MiuixButtonColors(
                color: Colors.white.withValues(alpha: 0.12),
                disabledColor: Colors.white10,
                contentColor: Colors.white,
                disabledContentColor: Colors.white38,
              ),
              cornerRadius: 10,
              child: const Text('返回'),
            ),
            const SizedBox(width: 12),
            MiuixButton(
              onPressed: onRetry,
              colors: const MiuixButtonColors(
                color: OsmiumColors.accentOnGlass,
                disabledColor: Colors.white12,
                contentColor: Color(0xFF002438),
                disabledContentColor: Colors.white38,
              ),
              cornerRadius: 10,
              child: const Text('重连'),
            ),
          ],
        ),
      ],
    );
  }
}
