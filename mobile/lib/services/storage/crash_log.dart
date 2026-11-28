import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:path_provider/path_provider.dart';

class CrashLog {
  static Future<void> initialize() async {
    late final File log;
    try {
      final directory = await getApplicationSupportDirectory();
      log = File('${directory.path}/flutter-crash.log');
    } catch (_) {
      return;
    }

    final previousFlutterHandler = FlutterError.onError;
    FlutterError.onError = (details) {
      _write(log, 'FlutterError', details.exception, details.stack);
      previousFlutterHandler?.call(details);
    };

    final dispatcher = PlatformDispatcher.instance;
    final previousPlatformHandler = dispatcher.onError;
    dispatcher.onError = (error, stack) {
      _write(log, 'PlatformDispatcher', error, stack);
      return previousPlatformHandler?.call(error, stack) ?? false;
    };
  }

  static void _write(File log, String source, Object error, StackTrace? stack) {
    try {
      log.writeAsStringSync(
        '[${DateTime.now().toIso8601String()}] $source\n$error\n${stack ?? ''}\n',
        flush: true,
      );
    } catch (_) {
      // Preserve the original exception handling if the log cannot be written.
    }
  }
}
