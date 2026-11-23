import 'dart:io';
import 'dart:typed_data';

import 'package:path_provider/path_provider.dart';

import '../../core/models/device_identity.dart';

/// Persists the last received desktop wallpaper for each connected PC locally
/// under `<appSupport>/device_wallpapers/<deviceId>_<timestamp>.<ext>`.
///
/// Every save writes a fresh timestamped file (then removes older files for the
/// same device) so Flutter's [FileImage] cache always picks up updated
/// wallpapers without stale frames.
class DeviceWallpaperStore {
  DeviceWallpaperStore({
    Future<Directory> Function()? baseDirectoryProvider,
    int Function()? nowMillis,
  }) : _baseDirectoryProvider =
           baseDirectoryProvider ?? getApplicationSupportDirectory,
       _nowMillis = nowMillis ?? _defaultNowMillis;

  static const int maxWallpaperBytes = 4 * 1024 * 1024;
  static const String _subDirName = 'device_wallpapers';

  final Future<Directory> Function() _baseDirectoryProvider;
  final int Function() _nowMillis;
  int _lastSaveStamp = 0;

  static int _defaultNowMillis() => DateTime.now().millisecondsSinceEpoch;

  Future<Directory> _ensureDir() async {
    final base = await _baseDirectoryProvider();
    final dir = Directory('${base.path}${Platform.pathSeparator}$_subDirName');
    if (!await dir.exists()) {
      await dir.create(recursive: true);
    }
    return dir;
  }

  /// Scans local cached wallpapers and returns `{normalizedDeviceId: filePath}`.
  Future<Map<String, String>> loadAll() async {
    final result = <String, String>{};
    final latestStamps = <String, int>{};

    try {
      final base = await _baseDirectoryProvider();
      final dir = Directory(
        '${base.path}${Platform.pathSeparator}$_subDirName',
      );
      if (!await dir.exists()) return result;

      await for (final entity in dir.list(followLinks: false)) {
        if (entity is! File) continue;
        final name = _basename(entity.path);
        final parsed = _parseFileName(name);
        if (parsed == null) continue;

        final stat = await entity.stat();
        if (stat.size <= 0 || stat.size > maxWallpaperBytes) continue;

        final (deviceId, stamp) = parsed;
        final prevStamp = latestStamps[deviceId];
        if (prevStamp == null || stamp >= prevStamp) {
          latestStamps[deviceId] = stamp;
          result[deviceId] = entity.path;
        }
      }
    } catch (_) {}

    return result;
  }

  /// Validates and atomically saves [bytes] for [deviceId], returning the saved
  /// file path or `null` if the device ID or image bytes are invalid.
  Future<String?> saveWallpaper(String deviceId, Uint8List bytes) async {
    final normalizedId = DeviceIdentity.normalizeDeviceId(deviceId);
    if (normalizedId.length != 9) return null;
    if (bytes.isEmpty || bytes.length > maxWallpaperBytes) return null;

    final ext = _detectImageExtension(bytes);
    if (ext == null) return null;

    try {
      final dir = await _ensureDir();
      var stamp = _nowMillis();
      if (stamp <= _lastSaveStamp) {
        stamp = _lastSaveStamp + 1;
      }
      _lastSaveStamp = stamp;

      final fileName = '${normalizedId}_$stamp.$ext';
      final targetPath = '${dir.path}${Platform.pathSeparator}$fileName';
      final tmpFile = File('$targetPath.tmp');
      await tmpFile.writeAsBytes(bytes, flush: true);
      final savedFile = await tmpFile.rename(targetPath);

      await _deleteOtherFiles(dir, normalizedId, keepPath: savedFile.path);
      return savedFile.path;
    } catch (_) {
      return null;
    }
  }

  Future<void> deleteWallpaper(String deviceId) async {
    final normalizedId = DeviceIdentity.normalizeDeviceId(deviceId);
    if (normalizedId.length != 9) return;
    try {
      final base = await _baseDirectoryProvider();
      final dir = Directory(
        '${base.path}${Platform.pathSeparator}$_subDirName',
      );
      if (!await dir.exists()) return;
      await _deleteOtherFiles(dir, normalizedId);
    } catch (_) {}
  }

  Future<void> _deleteOtherFiles(
    Directory dir,
    String normalizedId, {
    String? keepPath,
  }) async {
    await for (final entity in dir.list(followLinks: false)) {
      if (entity is! File) continue;
      if (keepPath != null && entity.path == keepPath) continue;
      final parsed = _parseFileName(_basename(entity.path));
      if (parsed != null && parsed.$1 == normalizedId) {
        try {
          await entity.delete();
        } catch (_) {}
      }
    }
  }

  static String _basename(String path) {
    final slash = path.lastIndexOf('/');
    final backslash = path.lastIndexOf('\\');
    final idx = slash > backslash ? slash : backslash;
    return idx >= 0 ? path.substring(idx + 1) : path;
  }

  static (String, int)? _parseFileName(String name) {
    if (name.endsWith('.tmp')) return null;
    final dot = name.lastIndexOf('.');
    if (dot <= 0) return null;
    final ext = name.substring(dot + 1).toLowerCase();
    if (ext != 'jpg' && ext != 'jpeg' && ext != 'png' && ext != 'webp') {
      return null;
    }
    final stem = name.substring(0, dot);
    final underscore = stem.indexOf('_');
    if (underscore > 0) {
      final id = DeviceIdentity.normalizeDeviceId(stem.substring(0, underscore));
      final stamp = int.tryParse(stem.substring(underscore + 1));
      if (id.length == 9 && stamp != null) {
        return (id, stamp);
      }
      return null;
    }
    final id = DeviceIdentity.normalizeDeviceId(stem);
    return id.length == 9 ? (id, 0) : null;
  }

  static String? _detectImageExtension(Uint8List bytes) {
    if (bytes.length >= 3 &&
        bytes[0] == 0xFF &&
        bytes[1] == 0xD8 &&
        bytes[2] == 0xFF) {
      return 'jpg';
    }
    if (bytes.length >= 8 &&
        bytes[0] == 0x89 &&
        bytes[1] == 0x50 &&
        bytes[2] == 0x4E &&
        bytes[3] == 0x47 &&
        bytes[4] == 0x0D &&
        bytes[5] == 0x0A &&
        bytes[6] == 0x1A &&
        bytes[7] == 0x0A) {
      return 'png';
    }
    if (bytes.length >= 12 &&
        bytes[0] == 0x52 &&
        bytes[1] == 0x49 &&
        bytes[2] == 0x46 &&
        bytes[3] == 0x46 &&
        bytes[8] == 0x57 &&
        bytes[9] == 0x45 &&
        bytes[10] == 0x42 &&
        bytes[11] == 0x50) {
      return 'webp';
    }
    return null;
  }
}
