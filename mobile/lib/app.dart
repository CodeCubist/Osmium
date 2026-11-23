import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_miuix/miuix.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'core/models/osmium_settings.dart';
import 'core/theme/osmium_motion.dart';
import 'core/theme/osmium_theme.dart';
import 'state/app_controller.dart';
import 'ui/shell/home_shell.dart';
import 'ui/widgets/osmium_page_transitions.dart';

class OsmiumApp extends ConsumerWidget {
  const OsmiumApp({super.key});

  static const PageTransitionsTheme _pageTransitions = PageTransitionsTheme(
    builders: {
      TargetPlatform.android: OsmiumPageTransitionsBuilder(),
      TargetPlatform.iOS: OsmiumPageTransitionsBuilder(),
      TargetPlatform.macOS: OsmiumPageTransitionsBuilder(),
      TargetPlatform.windows: OsmiumPageTransitionsBuilder(),
      TargetPlatform.linux: OsmiumPageTransitionsBuilder(),
      TargetPlatform.fuchsia: OsmiumPageTransitionsBuilder(),
    },
  );

  ThemeMode _resolveThemeMode(ThemePreference pref) {
    return switch (pref) {
      ThemePreference.light => ThemeMode.light,
      ThemePreference.dark => ThemeMode.dark,
      ThemePreference.system || ThemePreference.monetSystem => ThemeMode.system,
    };
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final pref = ref.watch(
      appControllerProvider.select((c) => c.settings.themePreference),
    );
    final themeMode = _resolveThemeMode(pref);
    OsmiumHaptics.enabled = ref.watch(
      appControllerProvider.select((c) => c.settings.enableHaptics),
    );
    final lightColors = OsmiumTheme.lightColors();
    final darkColors = OsmiumTheme.darkColors();
    final textStyles = OsmiumTheme.textStyles();

    return MaterialApp(
      title: 'Osmium',
      debugShowCheckedModeBanner: false,
      themeMode: themeMode,
      theme: ThemeData(
        useMaterial3: true,
        brightness: Brightness.light,
        fontFamily: OsmiumTheme.fontFamily,
        scaffoldBackgroundColor: lightColors.surface,
        colorScheme: ColorScheme.fromSeed(
          seedColor: OsmiumColors.brandLight,
          brightness: Brightness.light,
        ),
        pageTransitionsTheme: _pageTransitions,
      ),
      darkTheme: ThemeData(
        useMaterial3: true,
        brightness: Brightness.dark,
        fontFamily: OsmiumTheme.fontFamily,
        scaffoldBackgroundColor: darkColors.surface,
        colorScheme: ColorScheme.fromSeed(
          seedColor: OsmiumColors.brandDark,
          brightness: Brightness.dark,
        ),
        pageTransitionsTheme: _pageTransitions,
      ),
      builder: (context, child) {
        final isDark = Theme.of(context).brightness == Brightness.dark;
        final miuixData = MiuixThemeData(
          colors: isDark ? darkColors : lightColors,
          textStyles: textStyles,
          brightness: isDark ? Brightness.dark : Brightness.light,
          fontWeightAdjustment: MediaQuery.boldTextOf(context) ? 100 : 0,
        );
        return AnnotatedRegion<SystemUiOverlayStyle>(
          value: SystemUiOverlayStyle(
            statusBarColor: Colors.transparent,
            statusBarIconBrightness: isDark
                ? Brightness.light
                : Brightness.dark,
            systemNavigationBarColor: Colors.transparent,
            systemNavigationBarIconBrightness: isDark
                ? Brightness.light
                : Brightness.dark,
            systemNavigationBarContrastEnforced: false,
          ),
          child: MiuixTheme(
            data: miuixData,
            child: Material(
              type: MaterialType.transparency,
              child: DefaultTextStyle(
                style: TextStyle(
                  fontFamily: OsmiumTheme.fontFamily,
                  color: miuixData.colors.onSurface,
                  fontSize: 16,
                ),
                child: child ?? const SizedBox.shrink(),
              ),
            ),
          ),
        );
      },
      home: const HomeShell(),
    );
  }
}
