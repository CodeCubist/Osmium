import 'dart:ui';

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

  static const Color cardBorderLight = Color(0x16000000);
  static const Color cardBorderDark = Color(0x20FFFFFF);
  static const Color cardShadowLight = Color(0x0B000000);
  static const Color cardShadowDark = Color(0x28000000);

  // Translucent crystalline frosted-glass tokens.
  static const Color frostedOverlayLight = Color(0x5CF8FAFF);
  static const Color frostedOverlayBottomLight = Color(0x80F3F7FD);
  static const Color frostedOverlayDark = Color(0x5212161F);
  static const Color frostedOverlayBottomDark = Color(0x7810141D);
  static const Color frostedOverlayOfflineLight = Color(0x66F2F2F3);
  static const Color frostedOverlayOfflineBottomLight = Color(0x8CE8E8EA);
  static const Color frostedOverlayOfflineDark = Color(0x5E18181A);
  static const Color frostedOverlayOfflineBottomDark = Color(0x82131315);
  static const Color frostedDividerLight = Color(0x9EFFFFFF);
  static const Color frostedDividerDark = Color(0x2EFFFFFF);

  /// Luminance grayscale filter for desaturating offline device artwork and
  /// cached wallpapers.
  static const ColorFilter grayscaleFilter = ColorFilter.matrix(<double>[
    0.2126, 0.7152, 0.0722, 0, 0, //
    0.2126, 0.7152, 0.0722, 0, 0, //
    0.2126, 0.7152, 0.0722, 0, 0, //
    0, 0, 0, 1, 0,
  ]);

  static const Color glassSurfaceTopLight = Color(0x61FFFFFF);
  static const Color glassSurfaceBottomLight = Color(0x42F4F8FF);
  static const Color glassSurfaceTopDark = Color(0x57181E2B);
  static const Color glassSurfaceBottomDark = Color(0x3D111621);
  static const Color glassBorderLight = Color(0xB3FFFFFF);
  static const Color glassBorderDark = Color(0x29FFFFFF);

  // Session dark smoked-glass HUD tokens (translucent, never muddy/opaque).
  static const Color sessionGlassFill = Color(0x5C0D111A);
  static const Color sessionGlassRailFill = Color(0xB3090C13);
  static const Color sessionGlassPanelFill = Color(0x8C1D2534);
  static const Color sessionGlassBorder = Color(0x2EFFFFFF);
  static const Color sessionGlassHairline = Color(0x18FFFFFF);

  static const Color profileCircleLight = Color(0xFF1565C0);
  static const Color profileCircleDark = Color(0xFF277AF7);

  static Color onlineBg(bool isDark) => isDark ? onlineBgDark : onlineBgLight;
  static Color onlineFg(bool isDark) => isDark ? onlineFgDark : onlineFgLight;
  static Color directBg(bool isDark) => isDark ? directBgDark : directBgLight;
  static Color directFg(bool isDark) => isDark ? directFgDark : directFgLight;
  static Color warnBg(bool isDark) => isDark ? warnBgDark : warnBgLight;
  static Color warnFg(bool isDark) => isDark ? warnFgDark : warnFgLight;
  static Color dangerBg(bool isDark) => isDark ? dangerBgDark : dangerBgLight;
  static Color dangerFg(bool isDark) => isDark ? dangerFgDark : dangerFgLight;
  static Color cardBorder(bool isDark) =>
      isDark ? cardBorderDark : cardBorderLight;
  static Color cardShadow(bool isDark) =>
      isDark ? cardShadowDark : cardShadowLight;
  static Color frostedOverlay(bool isDark) =>
      isDark ? frostedOverlayDark : frostedOverlayLight;
  static Color frostedOverlayBottom(bool isDark) =>
      isDark ? frostedOverlayBottomDark : frostedOverlayBottomLight;
  static Color frostedOverlayOffline(bool isDark) =>
      isDark ? frostedOverlayOfflineDark : frostedOverlayOfflineLight;
  static Color frostedOverlayOfflineBottom(bool isDark) => isDark
      ? frostedOverlayOfflineBottomDark
      : frostedOverlayOfflineBottomLight;
  static Color frostedDivider(bool isDark) =>
      isDark ? frostedDividerDark : frostedDividerLight;
  static Color glassSurfaceTop(bool isDark) =>
      isDark ? glassSurfaceTopDark : glassSurfaceTopLight;
  static Color glassSurfaceBottom(bool isDark) =>
      isDark ? glassSurfaceBottomDark : glassSurfaceBottomLight;
  static Color glassBorder(bool isDark) =>
      isDark ? glassBorderDark : glassBorderLight;
  static Color profileCircle(bool isDark) =>
      isDark ? profileCircleDark : profileCircleLight;
}

/// Cached backdrop filter for glass surfaces: Gaussian blur plus a saturation
/// lift so a thin tint stays crisp and translucent instead of washing grey.
class OsmiumGlass {
  OsmiumGlass._();

  static final Map<(double, double), ImageFilter> _cache =
      <(double, double), ImageFilter>{};

  static ImageFilter filter(double sigma, {double saturation = 1.6}) =>
      _cache.putIfAbsent((sigma, saturation), () {
        const lr = 0.2126, lg = 0.7152, lb = 0.0722;
        final s = saturation, i = 1 - s;
        return ImageFilter.compose(
          outer: ColorFilter.matrix(<double>[
            lr * i + s, lg * i, lb * i, 0, 0, //
            lr * i, lg * i + s, lb * i, 0, 0, //
            lr * i, lg * i, lb * i + s, 0, 0, //
            0, 0, 0, 1, 0,
          ]),
          inner: ImageFilter.blur(
            sigmaX: sigma,
            sigmaY: sigma,
            tileMode: TileMode.clamp,
          ),
        );
      });
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
