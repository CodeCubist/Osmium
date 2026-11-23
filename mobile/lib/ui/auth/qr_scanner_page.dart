import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_miuix/miuix.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../../core/models/account_models.dart';
import '../../core/theme/osmium_motion.dart';
import 'qr_login_confirm_page.dart';

class QrScannerPage extends StatefulWidget {
  const QrScannerPage({super.key});

  @override
  State<QrScannerPage> createState() => _QrScannerPageState();
}

class _QrScannerPageState extends State<QrScannerPage> {
  final MobileScannerController _controller = MobileScannerController(
    formats: const [BarcodeFormat.qrCode],
    detectionSpeed: DetectionSpeed.normal,
    autoStart: true,
  );

  bool _handling = false;
  String? _invalidNotice;
  Timer? _noticeTimer;

  @override
  void dispose() {
    _noticeTimer?.cancel();
    _controller.dispose();
    super.dispose();
  }

  Future<void> _onDetect(BarcodeCapture capture) async {
    if (_handling || !mounted) return;
    String? raw;
    for (final barcode in capture.barcodes) {
      final candidate = barcode.rawValue?.trim();
      if (candidate != null && candidate.isNotEmpty) {
        raw = candidate;
        break;
      }
    }
    if (raw == null) return;

    final parsed = QrParsedRequest.tryParseInApp(raw);
    if (parsed == null) {
      _showInvalidNotice('请扫描 Osmium 电脑端登录二维码');
      return;
    }

    _handling = true;
    OsmiumHaptics.confirm();
    unawaited(_controller.stop());

    if (!mounted) return;
    await Navigator.of(context).pushReplacement<bool, bool>(
      MaterialPageRoute(
        builder: (_) => QrLoginConfirmPage(request: parsed),
      ),
    );
  }

  void _showInvalidNotice(String message) {
    if (!mounted) return;
    if (_invalidNotice != message) {
      OsmiumHaptics.selection();
      setState(() => _invalidNotice = message);
    }
    _noticeTimer?.cancel();
    _noticeTimer = Timer(const Duration(seconds: 2), () {
      if (mounted) setState(() => _invalidNotice = null);
    });
  }

  @override
  Widget build(BuildContext context) {
    final media = MediaQuery.of(context);
    final colors = MiuixTheme.of(context).colors;

    return Scaffold(
      backgroundColor: Colors.black,
      body: Stack(
        fit: StackFit.expand,
        children: [
          MobileScanner(
            controller: _controller,
            onDetect: _onDetect,
            errorBuilder: (context, error) {
              return Center(
                child: Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 32),
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      const Icon(
                        Icons.videocam_off_rounded,
                        size: 40,
                        color: Colors.white70,
                      ),
                      const SizedBox(height: 14),
                      Text(
                        error.errorCode ==
                                MobileScannerErrorCode.permissionDenied
                            ? '需要相机权限以扫描二维码'
                            : '无法启动相机',
                        style: const TextStyle(
                          color: Colors.white,
                          fontSize: 15,
                        ),
                        textAlign: TextAlign.center,
                      ),
                    ],
                  ),
                ),
              );
            },
          ),
          IgnorePointer(
            child: CustomPaint(
              painter: _ScannerOverlayPainter(accentColor: colors.primary),
            ),
          ),
          Positioned(
            top: media.padding.top + 8,
            left: 12,
            right: 12,
            child: Row(
              children: [
                _OverlayIconButton(
                  icon: Icons.arrow_back_rounded,
                  onTap: () => Navigator.of(context).pop(),
                ),
                const Spacer(),
                ValueListenableBuilder<MobileScannerState>(
                  valueListenable: _controller,
                  builder: (context, state, _) {
                    final torchOn = state.torchState == TorchState.on;
                    final available =
                        state.torchState != TorchState.unavailable;
                    if (!available) return const SizedBox.shrink();
                    return _OverlayIconButton(
                      icon: torchOn
                          ? Icons.flash_on_rounded
                          : Icons.flash_off_rounded,
                      active: torchOn,
                      onTap: () {
                        OsmiumHaptics.selection();
                        _controller.toggleTorch();
                      },
                    );
                  },
                ),
              ],
            ),
          ),
          Positioned(
            left: 24,
            right: 24,
            bottom: media.padding.bottom + 56,
            child: AnimatedOpacity(
              opacity: _invalidNotice != null ? 1 : 0,
              duration: const Duration(milliseconds: 180),
              child: Center(
                child: Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 16,
                    vertical: 10,
                  ),
                  decoration: BoxDecoration(
                    color: Colors.black.withValues(alpha: 0.72),
                    borderRadius: BorderRadius.circular(20),
                  ),
                  child: Text(
                    _invalidNotice ?? '',
                    style: const TextStyle(
                      color: Colors.white,
                      fontSize: 13,
                      fontWeight: FontWeight.w500,
                    ),
                  ),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _OverlayIconButton extends StatelessWidget {
  const _OverlayIconButton({
    required this.icon,
    required this.onTap,
    this.active = false,
  });

  final IconData icon;
  final VoidCallback onTap;
  final bool active;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        width: 44,
        height: 44,
        decoration: BoxDecoration(
          color: active
              ? Colors.white.withValues(alpha: 0.92)
              : Colors.black.withValues(alpha: 0.45),
          shape: BoxShape.circle,
        ),
        child: Icon(
          icon,
          size: 22,
          color: active ? Colors.black : Colors.white,
        ),
      ),
    );
  }
}

class _ScannerOverlayPainter extends CustomPainter {
  const _ScannerOverlayPainter({required this.accentColor});

  final Color accentColor;

  @override
  void paint(Canvas canvas, Size size) {
    final boxSize = (size.shortestSide * 0.66).clamp(220.0, 290.0);
    final rect = Rect.fromCenter(
      center: Offset(size.width / 2, size.height * 0.46),
      width: boxSize,
      height: boxSize,
    );
    const radius = Radius.circular(22);
    final rrect = RRect.fromRectAndRadius(rect, radius);

    final maskPath = Path()
      ..addRect(Offset.zero & size)
      ..addRRect(rrect)
      ..fillType = PathFillType.evenOdd;
    canvas.drawPath(
      maskPath,
      Paint()..color = Colors.black.withValues(alpha: 0.56),
    );

    final borderPaint = Paint()
      ..color = Colors.white.withValues(alpha: 0.22)
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.2;
    canvas.drawRRect(rrect, borderPaint);

    final cornerPaint = Paint()
      ..color = accentColor
      ..style = PaintingStyle.stroke
      ..strokeWidth = 3.5
      ..strokeCap = StrokeCap.round;

    const len = 26.0;
    const r = 22.0;

    // Top-left
    canvas.drawPath(
      Path()
        ..moveTo(rect.left, rect.top + len)
        ..lineTo(rect.left, rect.top + r)
        ..arcToPoint(Offset(rect.left + r, rect.top), radius: radius)
        ..lineTo(rect.left + len, rect.top),
      cornerPaint,
    );
    // Top-right
    canvas.drawPath(
      Path()
        ..moveTo(rect.right - len, rect.top)
        ..lineTo(rect.right - r, rect.top)
        ..arcToPoint(Offset(rect.right, rect.top + r), radius: radius)
        ..lineTo(rect.right, rect.top + len),
      cornerPaint,
    );
    // Bottom-left
    canvas.drawPath(
      Path()
        ..moveTo(rect.left, rect.bottom - len)
        ..lineTo(rect.left, rect.bottom - r)
        ..arcToPoint(
          Offset(rect.left + r, rect.bottom),
          radius: radius,
          clockwise: false,
        )
        ..lineTo(rect.left + len, rect.bottom),
      cornerPaint,
    );
    // Bottom-right
    canvas.drawPath(
      Path()
        ..moveTo(rect.right - len, rect.bottom)
        ..lineTo(rect.right - r, rect.bottom)
        ..arcToPoint(
          Offset(rect.right, rect.bottom - r),
          radius: radius,
          clockwise: false,
        )
        ..lineTo(rect.right, rect.bottom - len),
      cornerPaint,
    );
  }

  @override
  bool shouldRepaint(covariant _ScannerOverlayPainter oldDelegate) =>
      oldDelegate.accentColor != accentColor;
}
