import 'dart:async';
import 'dart:io';

import 'package:flutter/services.dart';

class NativeSurfaceInfo {
  const NativeSurfaceInfo({
    required this.textureId,
    required this.surfaceHandleId,
  });

  final int textureId;
  final int surfaceHandleId;
}

sealed class NativeInputEvent {
  const NativeInputEvent();
}

class NativeSurfaceChangedEvent extends NativeInputEvent {
  const NativeSurfaceChangedEvent(this.surfaceHandleId);
  final int surfaceHandleId;
}

class NativePointerDeltaEvent extends NativeInputEvent {
  const NativePointerDeltaEvent(this.dx, this.dy);
  final double dx;
  final double dy;
}

class NativePointerButtonEvent extends NativeInputEvent {
  const NativePointerButtonEvent(this.button, this.isDown);
  final int button;
  final bool isDown;
}

class NativePointerWheelEvent extends NativeInputEvent {
  const NativePointerWheelEvent(this.dx, this.dy);
  final int dx;
  final int dy;
}

class NativeGamepadEvent extends NativeInputEvent {
  const NativeGamepadEvent({
    required this.controllerIndex,
    required this.connected,
    required this.buttonsMask,
    required this.leftTrigger,
    required this.rightTrigger,
    required this.leftThumbX,
    required this.leftThumbY,
    required this.rightThumbX,
    required this.rightThumbY,
  });

  final int controllerIndex;
  final bool connected;
  final int buttonsMask;
  final int leftTrigger;
  final int rightTrigger;
  final int leftThumbX;
  final int leftThumbY;
  final int rightThumbX;
  final int rightThumbY;
}

class PlatformBridge {
  static const _methodChannel = MethodChannel('com.osmium.mobile/native');
  static const _eventChannel = EventChannel('com.osmium.mobile/input_events');
  static const _deepLinkChannel = EventChannel('com.osmium.mobile/deep_links');

  Stream<NativeInputEvent>? _events;
  Stream<String>? _deepLinks;

  Stream<String> get deepLinks {
    if (!Platform.isAndroid) {
      return const Stream.empty();
    }
    _deepLinks ??= _deepLinkChannel
        .receiveBroadcastStream()
        .where((e) => e is String && e.isNotEmpty)
        .cast<String>();
    return _deepLinks!;
  }

  Future<String?> getInitialDeepLink() async {
    if (!Platform.isAndroid) return null;
    try {
      final res = await _methodChannel.invokeMethod<String>(
        'getInitialDeepLink',
      );
      if (res == null || res.isEmpty) return null;
      return res;
    } catch (_) {
      return null;
    }
  }

  Future<bool> openExternalUrl(String url) async {
    if (!Platform.isAndroid || url.trim().isEmpty) return false;
    try {
      return await _methodChannel.invokeMethod<bool>('openExternalUrl', {
            'url': url.trim(),
          }) ??
          false;
    } catch (_) {
      return false;
    }
  }

  Stream<NativeInputEvent> get events {
    if (!Platform.isAndroid) {
      return const Stream.empty();
    }
    _events ??= _eventChannel
        .receiveBroadcastStream()
        .map(_parseEvent)
        .where((e) => e != null)
        .cast<NativeInputEvent>();
    return _events!;
  }

  NativeInputEvent? _parseEvent(dynamic raw) {
    if (raw is! Map) return null;
    final kind = raw['kind']?.toString() ?? '';
    switch (kind) {
      case 'surface_changed':
        return NativeSurfaceChangedEvent(
          (raw['surfaceHandleId'] as num?)?.toInt() ?? 0,
        );
      case 'pointer_delta':
        return NativePointerDeltaEvent(
          (raw['dx'] as num?)?.toDouble() ?? 0.0,
          (raw['dy'] as num?)?.toDouble() ?? 0.0,
        );
      case 'pointer_button':
        return NativePointerButtonEvent(
          (raw['button'] as num?)?.toInt() ?? 1,
          raw['isDown'] == true,
        );
      case 'pointer_wheel':
        return NativePointerWheelEvent(
          (raw['dx'] as num?)?.toInt() ?? 0,
          (raw['dy'] as num?)?.toInt() ?? 0,
        );
      case 'gamepad':
        return NativeGamepadEvent(
          controllerIndex: (raw['controllerIndex'] as num?)?.toInt() ?? 0,
          connected: ((raw['connected'] as num?)?.toInt() ?? 0) != 0,
          buttonsMask: (raw['buttonsMask'] as num?)?.toInt() ?? 0,
          leftTrigger: (raw['leftTrigger'] as num?)?.toInt() ?? 0,
          rightTrigger: (raw['rightTrigger'] as num?)?.toInt() ?? 0,
          leftThumbX: (raw['leftThumbX'] as num?)?.toInt() ?? 0,
          leftThumbY: (raw['leftThumbY'] as num?)?.toInt() ?? 0,
          rightThumbX: (raw['rightThumbX'] as num?)?.toInt() ?? 0,
          rightThumbY: (raw['rightThumbY'] as num?)?.toInt() ?? 0,
        );
      default:
        return null;
    }
  }

  Future<NativeSurfaceInfo?> createSurface({
    int width = 1920,
    int height = 1080,
  }) async {
    if (!Platform.isAndroid) return null;
    try {
      final res = await _methodChannel.invokeMapMethod<String, dynamic>(
        'createSurface',
        {'width': width, 'height': height},
      );
      if (res == null) return null;
      return NativeSurfaceInfo(
        textureId: (res['textureId'] as num?)?.toInt() ?? -1,
        surfaceHandleId: (res['surfaceHandleId'] as num?)?.toInt() ?? 0,
      );
    } catch (_) {
      return null;
    }
  }

  Future<void> resizeSurface({required int width, required int height}) async {
    if (!Platform.isAndroid) return;
    try {
      await _methodChannel.invokeMethod('resizeSurface', {
        'width': width,
        'height': height,
      });
    } catch (_) {}
  }

  Future<int> rebindSurface() async {
    if (!Platform.isAndroid) return 0;
    try {
      return await _methodChannel.invokeMethod<int>('rebindSurface') ?? 0;
    } catch (_) {
      return 0;
    }
  }

  Future<void> destroySurface() async {
    if (!Platform.isAndroid) return;
    try {
      await _methodChannel.invokeMethod('destroySurface');
    } catch (_) {}
  }

  Future<bool> setPointerCapture(bool enabled) async {
    if (!Platform.isAndroid) return false;
    try {
      return await _methodChannel.invokeMethod<bool>('setPointerCapture', {
            'enabled': enabled,
          }) ??
          false;
    } catch (_) {
      return false;
    }
  }

  Future<void> setImmersiveMode(bool enabled) async {
    if (!Platform.isAndroid) return;
    try {
      await _methodChannel.invokeMethod('setImmersiveMode', {
        'enabled': enabled,
      });
    } catch (_) {}
  }

  /// 0 = follow the system rotation setting, 1 = portrait, 2 = landscape.
  Future<void> setOrientationLock(int mode) async {
    if (!Platform.isAndroid) return;
    try {
      await _methodChannel.invokeMethod('setOrientationLock', {'mode': mode});
    } catch (_) {}
  }

  /// Center-crops and compresses raw image bytes into a 128x128 JPEG data URI
  /// within the server's avatar size limit (<= 48 KiB target, <= 64 KiB max).
  Future<String?> compressAvatar(Uint8List bytes) async {
    if (!Platform.isAndroid || bytes.isEmpty) return null;
    try {
      final res = await _methodChannel.invokeMethod<String>('compressAvatar', {
        'bytes': bytes,
      });
      if (res == null || res.isEmpty) return null;
      return res;
    } catch (_) {
      return null;
    }
  }
}
