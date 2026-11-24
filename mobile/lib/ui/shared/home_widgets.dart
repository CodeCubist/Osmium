import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_miuix/miuix.dart';

import '../../core/theme/osmium_motion.dart';
import '../../core/theme/osmium_theme.dart';

/// Hero gradients used by the home surfaces.
class OsmiumBannerGradients {
  OsmiumBannerGradients._();

  static List<Color> brand(bool isDark) => isDark
      ? const [Color(0xFF173B78), Color(0xFF1D52A6), Color(0xFF2565CC)]
      : const [Color(0xFF2468E6), Color(0xFF3482FF), Color(0xFF5E9DFF)];

  static List<Color> device({
    required bool isDark,
    required bool online,
    required bool controlled,
  }) {
    if (controlled) {
      return isDark
          ? const [Color(0xFF5A3410), Color(0xFF7D4A12), Color(0xFF9E5E16)]
          : const [Color(0xFFE07A10), Color(0xFFF08C1E), Color(0xFFF9A23C)];
    }
    if (!online) {
      return isDark
          ? const [Color(0xFF3A3A3C), Color(0xFF323234), Color(0xFF2A2A2C)]
          : const [Color(0xFF94949A), Color(0xFF85858B), Color(0xFF76767C)];
    }
    return brand(isDark);
  }
}

/// Unified crisp top bar shared across Devices, Connect, and Me tabs.
class OsmiumTopHeader extends StatelessWidget {
  const OsmiumTopHeader({
    super.key,
    required this.title,
    this.leading,
    this.actions = const [],
    this.bottom,
    this.scrolled = false,
  });

  final String title;
  final Widget? leading;
  final List<Widget> actions;
  final Widget? bottom;
  final bool scrolled;

  static const double barHeight = 56;

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;
    final topPad = MediaQuery.paddingOf(context).top;

    final bar = AnimatedContainer(
      duration: OsmiumMotion.durationOr(context, OsmiumMotion.small),
      curve: MiuixMotion.standardDecelerate,
      padding: EdgeInsets.only(top: topPad),
      decoration: BoxDecoration(
        color: scrolled
            ? colors.background.withValues(alpha: 0.56)
            : colors.background,
        border: Border(
          bottom: BorderSide(
            color: scrolled
                ? OsmiumColors.cardBorder(isDark)
                : Colors.transparent,
            width: 0.75,
          ),
        ),
      ),
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 720),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              SizedBox(
                height: barHeight,
                child: Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 16),
                  child: Row(
                    children: [
                      if (leading != null) ...[
                        leading!,
                        const SizedBox(width: 12),
                      ],
                      Expanded(
                        child: Text(
                          title,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: TextStyle(
                            fontFamily: OsmiumTheme.fontFamily,
                            fontSize: 22,
                            fontWeight: FontWeight.w700,
                            height: 1.2,
                            color: colors.onSurface,
                          ),
                        ),
                      ),
                      for (final action in actions) action,
                    ],
                  ),
                ),
              ),
              ?bottom,
            ],
          ),
        ),
      ),
    );

    if (!scrolled) return bar;
    return ClipRect(
      child: BackdropFilter(filter: OsmiumGlass.filter(24), child: bar),
    );
  }
}

/// Compact input geometry shared across forms and dialogs.
class OsmiumFieldDefaults {
  OsmiumFieldDefaults._();

  static const double cornerRadius = MiuixTextFieldDefaults.cornerRadius;

  /// Compact margin for placeholder-style single-line inputs (~40–42dp tall).
  static const EdgeInsets compactMargin = EdgeInsets.symmetric(
    horizontal: 12,
    vertical: 9,
  );

  /// Compact margin for floating-label single-line inputs (~44–46dp tall).
  static const EdgeInsets floatingMargin = EdgeInsets.symmetric(
    horizontal: 12,
    vertical: 11,
  );
}

/// Crisp hairline-bordered surface card unified across all tabs.
class OsmiumSurfaceCard extends StatelessWidget {
  const OsmiumSurfaceCard({
    super.key,
    required this.child,
    this.cornerRadius = 18,
    this.padding = EdgeInsets.zero,
    this.color,
    this.onPressed,
    this.onLongPress,
    this.clipBehavior = Clip.antiAlias,
  });

  final Widget child;
  final double cornerRadius;
  final EdgeInsetsGeometry padding;
  final Color? color;
  final VoidCallback? onPressed;
  final VoidCallback? onLongPress;
  final Clip clipBehavior;

  @override
  Widget build(BuildContext context) {
    final theme = MiuixTheme.of(context);
    final colors = theme.colors;
    final isDark = theme.brightness == Brightness.dark;
    final radius = BorderRadius.circular(cornerRadius);
    final surface = color ?? colors.surfaceContainer;

    Widget content = Padding(padding: padding, child: child);
    if (onPressed != null || onLongPress != null) {
      content = MiuixPressable(
        onPressed: onPressed,
        onLongPress: onLongPress,
        borderRadius: radius,
        feedbackType: MiuixPressFeedbackType.sink,
        sinkAmount: 0.985,
        child: content,
      );
    }

    return DecoratedBox(
      decoration: BoxDecoration(
        color: surface,
        borderRadius: radius,
        border: Border.all(
          color: OsmiumColors.cardBorder(isDark),
          width: 0.9,
        ),
        boxShadow: [
          BoxShadow(
            color: OsmiumColors.cardShadow(isDark),
            blurRadius: 10,
            offset: const Offset(0, 2),
          ),
        ],
      ),
      child: ClipRRect(
        borderRadius: radius,
        clipBehavior: clipBehavior,
        child: content,
      ),
    );
  }
}

/// Crystalline translucent frosted-glass container with specular rim border.
class OsmiumGlassSurface extends StatelessWidget {
  const OsmiumGlassSurface({
    super.key,
    required this.child,
    this.cornerRadius = 24,
    this.padding = EdgeInsets.zero,
    this.blurSigma = 22,
    this.onPressed,
    this.borderColor,
    this.topColor,
    this.bottomColor,
  });

  final Widget child;
  final double cornerRadius;
  final EdgeInsetsGeometry padding;
  final double blurSigma;
  final VoidCallback? onPressed;
  final Color? borderColor;
  final Color? topColor;
  final Color? bottomColor;

  @override
  Widget build(BuildContext context) {
    final isDark = MiuixTheme.of(context).brightness == Brightness.dark;
    final radius = BorderRadius.circular(cornerRadius);

    Widget inner = Padding(padding: padding, child: child);
    if (onPressed != null) {
      inner = MiuixPressable(
        onPressed: onPressed,
        borderRadius: radius,
        feedbackType: MiuixPressFeedbackType.sink,
        sinkAmount: 0.985,
        child: inner,
      );
    }

    return DecoratedBox(
      decoration: BoxDecoration(
        borderRadius: radius,
        boxShadow: [
          BoxShadow(
            color: isDark
                ? const Color(0x40000000)
                : const Color(0x140E2140),
            blurRadius: 24,
            offset: const Offset(0, 8),
          ),
        ],
      ),
      child: ClipRRect(
        borderRadius: radius,
        child: BackdropFilter(
          filter: OsmiumGlass.filter(blurSigma),
          child: DecoratedBox(
            decoration: BoxDecoration(
              borderRadius: radius,
              gradient: LinearGradient(
                begin: Alignment.topLeft,
                end: Alignment.bottomRight,
                colors: [
                  topColor ?? OsmiumColors.glassSurfaceTop(isDark),
                  bottomColor ?? OsmiumColors.glassSurfaceBottom(isDark),
                ],
              ),
              border: Border.all(
                color: borderColor ?? OsmiumColors.glassBorder(isDark),
                width: 1,
              ),
            ),
            child: inner,
          ),
        ),
      ),
    );
  }
}

/// Default device artwork shown before a remote desktop wallpaper has been
/// cached: a soft glow field crossed by a twisted silk band of fine strands.
class OsmiumWaveBackdrop extends StatelessWidget {
  const OsmiumWaveBackdrop({
    super.key,
    required this.isDark,
    this.online = true,
    this.controlled = false,
  });

  final bool isDark;
  final bool online;
  final bool controlled;

  @override
  Widget build(BuildContext context) {
    return RepaintBoundary(
      child: CustomPaint(
        painter: _OsmiumWavePainter(
          isDark: isDark,
          online: online,
          controlled: controlled,
        ),
        child: const SizedBox.expand(),
      ),
    );
  }
}

class _BackdropPalette {
  const _BackdropPalette({
    required this.base,
    required this.glow,
    required this.glowAlt,
    required this.wave,
    required this.strand,
    required this.strandAlpha,
    required this.sheenAlpha,
  });

  final List<Color> base;
  final Color glow;
  final Color glowAlt;
  final Color wave;
  final Color strand;
  final double strandAlpha;
  final double sheenAlpha;

  static _BackdropPalette of({
    required bool isDark,
    required bool online,
    required bool controlled,
  }) {
    if (!online) {
      return isDark
          ? const _BackdropPalette(
              base: [Color(0xFF1A1A1C), Color(0xFF232325), Color(0xFF2D2D30)],
              glow: Color(0xFF66666B),
              glowAlt: Color(0xFF48484C),
              wave: Color(0xFF75757A),
              strand: Color(0xFFB8B8BE),
              strandAlpha: 0.24,
              sheenAlpha: 0.03,
            )
          : const _BackdropPalette(
              base: [Color(0xFFF1F1F2), Color(0xFFE3E3E5), Color(0xFFD2D2D5)],
              glow: Color(0xFFA7A7AC),
              glowAlt: Color(0xFFD5D5D8),
              wave: Color(0xFF919196),
              strand: Color(0xFFFFFFFF),
              strandAlpha: 0.70,
              sheenAlpha: 0.26,
            );
    }
    if (controlled) {
      return isDark
          ? const _BackdropPalette(
              base: [Color(0xFF1C130B), Color(0xFF33200F), Color(0xFF4F2F12)],
              glow: Color(0xFFE88A30),
              glowAlt: Color(0xFFB8501E),
              wave: Color(0xFFF2A04A),
              strand: Color(0xFFFFD2A0),
              strandAlpha: 0.42,
              sheenAlpha: 0.05,
            )
          : const _BackdropPalette(
              base: [Color(0xFFFFF6EC), Color(0xFFFFE4C6), Color(0xFFFCC892)],
              glow: Color(0xFFFF9C40),
              glowAlt: Color(0xFFFFD470),
              wave: Color(0xFFF08A2A),
              strand: Color(0xFFFFFFFF),
              strandAlpha: 0.78,
              sheenAlpha: 0.30,
            );
    }
    return isDark
        ? const _BackdropPalette(
            base: [Color(0xFF0A1229), Color(0xFF0F2048), Color(0xFF15326C)],
            glow: Color(0xFF3274FF),
            glowAlt: Color(0xFF2AB4F0),
            wave: Color(0xFF4F8DFF),
            strand: Color(0xFFA8CAFF),
            strandAlpha: 0.42,
            sheenAlpha: 0.05,
          )
        : const _BackdropPalette(
            base: [Color(0xFFEFF5FF), Color(0xFFD4E5FF), Color(0xFFA9CBFF)],
            glow: Color(0xFF4C8DFF),
            glowAlt: Color(0xFF7FD6FF),
            wave: Color(0xFF2F78F2),
            strand: Color(0xFFFFFFFF),
            strandAlpha: 0.80,
            sheenAlpha: 0.30,
          );
  }
}

class _OsmiumWavePainter extends CustomPainter {
  const _OsmiumWavePainter({
    required this.isDark,
    required this.online,
    required this.controlled,
  });

  final bool isDark;
  final bool online;
  final bool controlled;

  // Edges of the silk band in unit space. They cross at different x, so the
  // interpolated strands fold over along a short line instead of a point.
  static const List<Offset> _bandA = [
    Offset(-0.08, 0.98),
    Offset(0.30, 1.00),
    Offset(0.50, 0.04),
    Offset(1.08, 0.14),
  ];
  static const List<Offset> _bandB = [
    Offset(-0.08, 0.30),
    Offset(0.52, 0.12),
    Offset(0.80, 0.90),
    Offset(1.08, 0.60),
  ];
  static const int _strands = 26;

  @override
  void paint(Canvas canvas, Size size) {
    final p = _BackdropPalette.of(
      isDark: isDark,
      online: online,
      controlled: controlled,
    );
    final w = size.width;
    final h = size.height;
    final rect = Offset.zero & size;

    canvas.drawRect(
      rect,
      Paint()
        ..shader = LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: p.base,
        ).createShader(rect),
    );

    _glow(canvas, rect, Offset(w * 0.84, h * 0.10), w * 0.78,
        p.glow.withValues(alpha: isDark ? 0.55 : 0.50));
    _glow(canvas, rect, Offset(w * 0.06, h * 1.0), w * 0.62,
        p.glowAlt.withValues(alpha: isDark ? 0.32 : 0.55));

    // Low back wave giving the lower half some colour mass.
    final crest = Path()
      ..moveTo(0, h * 0.72)
      ..cubicTo(w * 0.30, h * 0.54, w * 0.56, h * 0.96, w, h * 0.50);
    final wave = Path.from(crest)
      ..lineTo(w, h)
      ..lineTo(0, h)
      ..close();
    canvas.drawPath(
      wave,
      Paint()
        ..shader = LinearGradient(
          begin: Alignment.topCenter,
          end: Alignment.bottomCenter,
          colors: [
            p.wave.withValues(alpha: isDark ? 0.30 : 0.22),
            p.wave.withValues(alpha: 0),
          ],
        ).createShader(Rect.fromLTRB(0, h * 0.5, w, h)),
    );
    canvas.drawPath(
      crest,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1
        ..shader = LinearGradient(
          colors: [
            p.strand.withValues(alpha: 0),
            p.strand.withValues(alpha: p.strandAlpha * 0.7),
            p.strand.withValues(alpha: 0),
          ],
        ).createShader(rect),
    );

    final strand = Paint()..style = PaintingStyle.stroke;
    for (var i = 0; i < _strands; i++) {
      final t = i / (_strands - 1);
      final swell = math.sin(math.pi * t);
      final pts = [
        for (var k = 0; k < 4; k++) Offset.lerp(_bandA[k], _bandB[k], t)!,
      ];
      final path = Path()
        ..moveTo(w * pts[0].dx, h * pts[0].dy)
        ..cubicTo(
          w * pts[1].dx,
          h * pts[1].dy,
          w * pts[2].dx,
          h * pts[2].dy,
          w * pts[3].dx,
          h * pts[3].dy,
        );
      final alpha = p.strandAlpha * (0.16 + 0.84 * swell);
      strand
        ..strokeWidth = 0.55 + 0.45 * swell
        ..shader = LinearGradient(
          colors: [
            p.strand.withValues(alpha: 0),
            p.strand.withValues(alpha: alpha),
            p.strand.withValues(alpha: alpha * 0.55),
          ],
          stops: const [0, 0.5, 1],
        ).createShader(rect);
      canvas.drawPath(path, strand);
    }

    canvas.drawRect(
      rect,
      Paint()
        ..shader = LinearGradient(
          begin: Alignment.topCenter,
          end: Alignment.center,
          colors: [
            Colors.white.withValues(alpha: p.sheenAlpha),
            Colors.white.withValues(alpha: 0),
          ],
        ).createShader(rect),
    );
  }

  static void _glow(
    Canvas canvas,
    Rect rect,
    Offset center,
    double radius,
    Color color,
  ) {
    canvas.drawRect(
      rect,
      Paint()
        ..shader = RadialGradient(
          colors: [
            color,
            color.withValues(alpha: color.a * 0.35),
            color.withValues(alpha: 0),
          ],
          stops: const [0, 0.45, 1],
        ).createShader(Rect.fromCircle(center: center, radius: radius)),
    );
  }

  @override
  bool shouldRepaint(covariant _OsmiumWavePainter oldDelegate) {
    return oldDelegate.isDark != isDark ||
        oldDelegate.online != online ||
        oldDelegate.controlled != controlled;
  }
}

/// A clipped hero banner with the brand gradient and a restrained abstract
/// artwork layer, optionally backed by a cached remote desktop wallpaper.
class OsmiumBanner extends StatelessWidget {
  const OsmiumBanner({
    super.key,
    required this.colors,
    required this.height,
    required this.child,
    this.padding = const EdgeInsets.all(20),
    this.artwork = true,
    this.backgroundImage,
  });

  final List<Color> colors;
  final double height;
  final Widget child;
  final EdgeInsetsGeometry padding;
  final bool artwork;
  final ImageProvider? backgroundImage;

  @override
  Widget build(BuildContext context) {
    final image = backgroundImage;
    return SizedBox(
      height: height,
      width: double.infinity,
      child: Stack(
        fit: StackFit.expand,
        children: [
          DecoratedBox(
            decoration: BoxDecoration(
              gradient: LinearGradient(
                begin: Alignment.topLeft,
                end: Alignment.bottomRight,
                colors: colors,
              ),
            ),
          ),
          if (image != null)
            Positioned.fill(
              child: IgnorePointer(
                child: Image(
                  image: image,
                  fit: BoxFit.cover,
                  gaplessPlayback: true,
                  filterQuality: FilterQuality.medium,
                  frameBuilder:
                      (context, imageChild, frame, wasSynchronouslyLoaded) {
                        final photo = Stack(
                          fit: StackFit.expand,
                          children: [
                            imageChild,
                            const DecoratedBox(
                              decoration: BoxDecoration(
                                gradient: LinearGradient(
                                  begin: Alignment.centerLeft,
                                  end: Alignment.centerRight,
                                  stops: [0.0, 0.58, 1.0],
                                  colors: [
                                    Color(0x96000000),
                                    Color(0x52000000),
                                    Color(0x28000000),
                                  ],
                                ),
                              ),
                            ),
                          ],
                        );
                        if (wasSynchronouslyLoaded) return photo;
                        return AnimatedOpacity(
                          opacity: frame == null ? 0 : 1,
                          duration: OsmiumMotion.durationOr(
                            context,
                            OsmiumMotion.small,
                          ),
                          curve: MiuixMotion.standardDecelerate,
                          child: photo,
                        );
                      },
                  errorBuilder: (_, _, _) => artwork
                      ? const CustomPaint(painter: _OsmiumBannerArtwork())
                      : const SizedBox.shrink(),
                ),
              ),
            )
          else if (artwork)
            const Positioned.fill(
              child: IgnorePointer(
                child: CustomPaint(painter: _OsmiumBannerArtwork()),
              ),
            ),
          Padding(padding: padding, child: child),
        ],
      ),
    );
  }
}

class _OsmiumBannerArtwork extends CustomPainter {
  const _OsmiumBannerArtwork();

  @override
  void paint(Canvas canvas, Size size) {
    final fill = Paint()..color = Colors.white.withValues(alpha: 0.07);
    final stroke = Paint()
      ..color = Colors.white.withValues(alpha: 0.14)
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.25;

    final c1 = Offset(size.width * 0.88, size.height * 0.22);
    final r1 = size.height * 0.62;
    canvas.drawCircle(c1, r1, fill);
    canvas.drawCircle(c1, r1 * 0.72, stroke);

    final c2 = Offset(size.width * 0.74, size.height * 1.08);
    canvas.drawCircle(c2, size.height * 0.58, fill);
  }

  @override
  bool shouldRepaint(covariant CustomPainter oldDelegate) => false;
}

/// Compact pill for status tags (`免密`, `已锁定`, etc.).
class OsmiumStatusPill extends StatelessWidget {
  const OsmiumStatusPill({
    super.key,
    required this.label,
    required this.background,
    required this.foreground,
    this.icon,
    this.fontSize = 11,
  });

  final String label;
  final Color background;
  final Color foreground;
  final IconData? icon;
  final double fontSize;

  @override
  Widget build(BuildContext context) {
    final duration = OsmiumMotion.durationOr(context, OsmiumMotion.small);
    final textStyle = TextStyle(
      fontFamily: OsmiumTheme.fontFamily,
      fontSize: fontSize,
      height: 1.1,
      fontWeight: FontWeight.w600,
      color: foreground,
      fontFeatures: OsmiumTheme.tabularFeatures,
    );

    return Semantics(
      container: true,
      excludeSemantics: true,
      label: label,
      child: AnimatedContainer(
        duration: duration,
        curve: MiuixMotion.standardDecelerate,
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
        decoration: ShapeDecoration(
          color: background,
          shape: const StadiumBorder(),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (icon != null) ...[
              TweenAnimationBuilder<Color?>(
                tween: ColorTween(end: foreground),
                duration: duration,
                curve: MiuixMotion.standardDecelerate,
                builder: (context, color, _) =>
                    Icon(icon, size: fontSize + 2, color: color),
              ),
              const SizedBox(width: 5),
            ],
            AnimatedSwitcher(
              duration: duration,
              switchInCurve: MiuixMotion.standardDecelerate,
              switchOutCurve: MiuixMotion.standardAccelerate,
              transitionBuilder: (child, animation) => FadeTransition(
                opacity: animation,
                child: ScaleTransition(
                  scale: Tween<double>(begin: 0.92, end: 1).animate(animation),
                  child: child,
                ),
              ),
              child: AnimatedDefaultTextStyle(
                key: ValueKey(label),
                duration: duration,
                curve: MiuixMotion.standardDecelerate,
                style: textStyle,
                child: Text(label),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// Squircle icon tile shared by rows and empty states.
class OsmiumIconTile extends StatelessWidget {
  const OsmiumIconTile({
    super.key,
    required this.icon,
    required this.background,
    required this.foreground,
    this.size = 44,
    this.radius = 14,
  });

  final IconData icon;
  final Color background;
  final Color foreground;
  final double size;
  final double radius;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: size,
      height: size,
      alignment: Alignment.center,
      decoration: ShapeDecoration(
        color: background,
        shape: MiuixSquircleBorder(cornerRadius: radius),
      ),
      child: Icon(icon, size: size * 0.46, color: foreground),
    );
  }
}

/// Clean section heading aligned with the 16dp page margin.
class OsmiumSectionHeader extends StatelessWidget {
  const OsmiumSectionHeader(
    this.title, {
    super.key,
    this.trailing,
    this.padding = const EdgeInsets.fromLTRB(4, 18, 4, 8),
  });

  final String title;
  final Widget? trailing;
  final EdgeInsetsGeometry padding;

  @override
  Widget build(BuildContext context) {
    final colors = MiuixTheme.of(context).colors;
    return Padding(
      padding: padding,
      child: Row(
        children: [
          Expanded(
            child: Text(
              title,
              style: TextStyle(
                fontFamily: OsmiumTheme.fontFamily,
                fontSize: 15,
                fontWeight: FontWeight.w600,
                color: colors.onSurface,
              ),
            ),
          ),
          ?trailing,
        ],
      ),
    );
  }
}

class OsmiumCardDivider extends StatelessWidget {
  const OsmiumCardDivider({super.key});

  @override
  Widget build(BuildContext context) {
    return const Padding(
      padding: EdgeInsets.symmetric(horizontal: 16),
      child: MiuixHorizontalDivider(),
    );
  }
}

/// Small muted glyph used as a preference start action.
class OsmiumPrefIcon extends StatelessWidget {
  const OsmiumPrefIcon(this.icon, {super.key});

  final IconData icon;

  @override
  Widget build(BuildContext context) {
    final colors = MiuixTheme.of(context).colors;
    return Padding(
      padding: const EdgeInsets.only(right: 12),
      child: Icon(icon, size: 20, color: colors.onSurfaceVariantSummary),
    );
  }
}
