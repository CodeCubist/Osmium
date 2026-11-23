import 'package:flutter/material.dart';
import 'package:flutter_miuix/miuix.dart';

/// HyperOS palette, the one Miuix components are drawn for: neutral surfaces
/// and a single blue accent, so custom pieces never fight the library.
class OsmiumColors {
  OsmiumColors._();

  static const Color brandLight = Color(0xFF3482FF);
  static const Color brandDark = Color(0xFF277AF7);

  /// Accent for text, strokes and fills on the always-dark session glass.
  static const Color accentOnGlass = Color(0xFF5DAAFF);

  static const Color onlineBgLight = Color(0xFFE6F6EC);
  static const Color onlineFgLight = Color(0xFF13934A);
  static const Color onlineBgDark = Color(0xFF16301F);
  static const Color onlineFgDark = Color(0xFF4CD07D);

  static const Color directBgLight = Color(0xFFEAF2FF);
  static const Color directFgLight = Color(0xFF3482FF);
  static const Color directBgDark = Color(0xFF1E2C44);
  static const Color directFgDark = Color(0xFF5DAAFF);

  static const Color warnBgLight = Color(0xFFFFF3E0);
  static const Color warnFgLight = Color(0xFFD26B00);
  static const Color warnBgDark = Color(0xFF382813);
  static const Color warnFgDark = Color(0xFFFFB347);

  static const Color dangerBgLight = Color(0xFFFDEEEC);
  static const Color dangerFgLight = Color(0xFFE94634);
  static const Color dangerBgDark = Color(0xFF3A1714);
  static const Color dangerFgDark = Color(0xFFFF6E61);

  static Color onlineBg(bool isDark) => isDark ? onlineBgDark : onlineBgLight;
  static Color onlineFg(bool isDark) => isDark ? onlineFgDark : onlineFgLight;
  static Color directBg(bool isDark) => isDark ? directBgDark : directBgLight;
  static Color directFg(bool isDark) => isDark ? directFgDark : directFgLight;
  static Color warnBg(bool isDark) => isDark ? warnBgDark : warnBgLight;
  static Color warnFg(bool isDark) => isDark ? warnFgDark : warnFgLight;
  static Color dangerBg(bool isDark) => isDark ? dangerBgDark : dangerBgLight;
  static Color dangerFg(bool isDark) => isDark ? dangerFgDark : dangerFgLight;
}

class OsmiumTheme {
  OsmiumTheme._();

  static const String fontFamily = 'MiSans';

  static const List<FontFeature> tabularFeatures = [
    FontFeature.tabularFigures(),
  ];

  static MiuixColors lightColors() => lightColorScheme();

  static MiuixColors darkColors() => darkColorScheme();

  static MiuixTextStyles textStyles() => const MiuixTextStyles(
    main: TextStyle(
      fontFamily: fontFamily,
      fontSize: 16,
      fontWeight: FontWeight.w500,
    ),
    paragraph: TextStyle(
      fontFamily: fontFamily,
      fontSize: 15,
      height: 1.35,
      fontWeight: FontWeight.w400,
    ),
    body1: TextStyle(
      fontFamily: fontFamily,
      fontSize: 15,
      fontWeight: FontWeight.w500,
    ),
    body2: TextStyle(
      fontFamily: fontFamily,
      fontSize: 13,
      fontWeight: FontWeight.w400,
    ),
    button: TextStyle(
      fontFamily: fontFamily,
      fontSize: 16,
      fontWeight: FontWeight.w600,
    ),
    footnote1: TextStyle(
      fontFamily: fontFamily,
      fontSize: 12,
      fontWeight: FontWeight.w500,
    ),
    footnote2: TextStyle(
      fontFamily: fontFamily,
      fontSize: 11,
      fontWeight: FontWeight.w500,
    ),
    headline1: TextStyle(
      fontFamily: fontFamily,
      fontSize: 17,
      fontWeight: FontWeight.w600,
    ),
    headline2: TextStyle(
      fontFamily: fontFamily,
      fontSize: 15,
      fontWeight: FontWeight.w600,
    ),
    subtitle: TextStyle(
      fontFamily: fontFamily,
      fontSize: 13,
      fontWeight: FontWeight.w600,
    ),
    title1: TextStyle(
      fontFamily: fontFamily,
      fontSize: 28,
      fontWeight: FontWeight.w700,
      height: 1.2,
    ),
    title2: TextStyle(
      fontFamily: fontFamily,
      fontSize: 22,
      fontWeight: FontWeight.w700,
      height: 1.25,
    ),
    title3: TextStyle(
      fontFamily: fontFamily,
      fontSize: 18,
      fontWeight: FontWeight.w600,
      height: 1.25,
    ),
    title4: TextStyle(
      fontFamily: fontFamily,
      fontSize: 16,
      fontWeight: FontWeight.w600,
      height: 1.25,
    ),
  );

  static TextStyle monoStyle({
    required double fontSize,
    FontWeight fontWeight = FontWeight.w500,
    Color? color,
    double? letterSpacing,
  }) {
    return TextStyle(
      fontFamily: fontFamily,
      fontSize: fontSize,
      fontWeight: fontWeight,
      color: color,
      letterSpacing: letterSpacing,
      fontFeatures: tabularFeatures,
    );
  }
}
