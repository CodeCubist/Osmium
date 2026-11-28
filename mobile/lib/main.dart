import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'app.dart';
import 'services/storage/crash_log.dart';
import 'services/storage/secure_storage_service.dart';
import 'state/app_controller.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  await CrashLog.initialize();
  await SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
  // Read the saved sign-in before the first frame, so a returning user opens
  // signed in while the token and device list refresh behind it.
  final session = await SecureStorageService().loadAccountSession().timeout(
    const Duration(seconds: 2),
    onTimeout: () => null,
  );
  runApp(
    ProviderScope(
      overrides: [savedAccountSessionProvider.overrideWithValue(session)],
      child: const OsmiumApp(),
    ),
  );
}
