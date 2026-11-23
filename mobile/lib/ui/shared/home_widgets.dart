import 'package:flutter/material.dart';
import 'package:flutter_miuix/miuix.dart';

import '../../core/theme/osmium_motion.dart';
import '../../core/theme/osmium_theme.dart';

/// Hero gradients used by the home surfaces: HyperOS blue around the theme
/// primary, neutral greys with no blue cast. Dark mode keeps them deeper so a
/// banner never outshines the black page.
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

/// A clipped hero banner with the brand gradient and a restrained abstract
/// artwork layer. The caller supplies the content and the outer shape clips it.
class OsmiumBanner extends StatelessWidget {
  const OsmiumBanner({
    super.key,
    required this.colors,
    required this.height,
    required this.child,
    this.padding = const EdgeInsets.all(20),
    this.artwork = true,
  });

  final List<Color> colors;
  final double height;
  final Widget child;
  final EdgeInsetsGeometry padding;
  final bool artwork;

  @override
  Widget build(BuildContext context) {
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
          if (artwork)
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
    final line = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.1
      ..color = Colors.white.withValues(alpha: 0.10);

    final orbit = Offset(size.width * 0.86, size.height * 0.02);
    for (var i = 1; i <= 4; i++) {
      canvas.drawCircle(orbit, 22.0 + i * 24.0, line);
    }

    final sweep = Path()
      ..moveTo(size.width * 0.38, size.height * 1.08)
      ..quadraticBezierTo(
        size.width * 0.62,
        size.height * 0.56,
        size.width * 1.04,
        size.height * 0.26,
      );
    canvas.drawPath(sweep, line);

    final dot = Paint()..color = Colors.white.withValues(alpha: 0.18);
    for (var row = 0; row < 3; row++) {
      for (var col = 0; col < 4; col++) {
        canvas.drawCircle(
          Offset(size.width * 0.62 + col * 18, size.height * 0.54 + row * 16),
          1.6,
          dot,
        );
      }
    }
  }

  @override
  bool shouldRepaint(covariant _OsmiumBannerArtwork oldDelegate) => false;
}

/// Compact status badge with an optional leading icon and tabular numerals.
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

/// Squircle icon tile shared by device rows, history rows and empty states.
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

/// Section label aligned with the 16dp page margin (MiuixSmallTitle's default
/// 28dp inset makes headings drift away from the cards).
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
                fontSize: 14,
                fontWeight: FontWeight.w600,
                color: colors.onBackgroundVariant,
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
