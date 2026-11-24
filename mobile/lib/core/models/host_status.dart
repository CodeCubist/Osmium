import 'dart:typed_data';
import 'dart:ui' show Rect;

class HostDisplayDto {
  const HostDisplayDto({
    required this.index,
    required this.width,
    required this.height,
    required this.refreshRate,
    required this.isPrimary,
    required this.name,
    required this.deviceName,
  });

  final int index;
  final int width;
  final int height;
  final int refreshRate;
  final bool isPrimary;
  final String name;
  final String deviceName;

  factory HostDisplayDto.fromJson(Map<String, dynamic> json) {
    return HostDisplayDto(
      index: (json['index'] as num?)?.toInt() ?? 0,
      width: (json['width'] as num?)?.toInt() ?? 1920,
      height: (json['height'] as num?)?.toInt() ?? 1080,
      refreshRate: (json['refreshRate'] as num?)?.toInt() ?? 60,
      isPrimary: json['isPrimary'] == true,
      name: (json['name'] ?? '').toString(),
      deviceName: (json['deviceName'] ?? '').toString(),
    );
  }
}

class HostSessionStatusDto {
  const HostSessionStatusDto({
    this.activeDisplayIndex = 0,
    this.nativeWidth = 1920,
    this.nativeHeight = 1080,
    this.streamWidth = 1920,
    this.streamHeight = 1080,
    this.fps = 60,
    this.hostHdrEnabled = false,
    this.hdr = false,
    this.yuv444 = false,
    this.mouseMode = 0,
    this.virtualSpeakerActive = false,
    this.virtualMicActive = false,
    this.screenLocked = false,
    this.touchInput = false,
    this.displays = const [],
  });

  final int activeDisplayIndex;
  final int nativeWidth;
  final int nativeHeight;
  final int streamWidth;
  final int streamHeight;
  final int fps;
  final bool hostHdrEnabled;
  final bool hdr;
  final bool yuv444;
  final int mouseMode;
  final bool virtualSpeakerActive;
  final bool virtualMicActive;
  final bool screenLocked;

  /// The host injects Windows touch input (protocol `InputTouch`); older
  /// hosts only take mouse events.
  final bool touchInput;
  final List<HostDisplayDto> displays;

  factory HostSessionStatusDto.fromJson(Map<String, dynamic> json) {
    final rawDisplays = json['displays'];
    final displays = <HostDisplayDto>[];
    if (rawDisplays is List) {
      for (final item in rawDisplays) {
        if (item is Map<String, dynamic>) {
          displays.add(HostDisplayDto.fromJson(item));
        }
      }
    }
    return HostSessionStatusDto(
      activeDisplayIndex: (json['activeDisplayIndex'] as num?)?.toInt() ?? 0,
      nativeWidth: (json['nativeWidth'] as num?)?.toInt() ?? 1920,
      nativeHeight: (json['nativeHeight'] as num?)?.toInt() ?? 1080,
      streamWidth: (json['streamWidth'] as num?)?.toInt() ?? 1920,
      streamHeight: (json['streamHeight'] as num?)?.toInt() ?? 1080,
      fps: (json['fps'] as num?)?.toInt() ?? 60,
      hostHdrEnabled: json['hostHdrEnabled'] == true,
      hdr: json['hdr'] == true,
      yuv444: json['yuv444'] == true,
      mouseMode: (json['mouseMode'] as num?)?.toInt() ?? 0,
      virtualSpeakerActive: json['virtualSpeakerActive'] == true,
      virtualMicActive: json['virtualMicActive'] == true,
      screenLocked: json['screenLocked'] == true,
      touchInput: json['touchInput'] == true,
      displays: displays,
    );
  }
}

/// Which control has keyboard focus on the host; protocol
/// `HostTextFocusPayload`, native event 6.
class HostTextFocus {
  const HostTextFocus({this.active = false, this.serial = 0, this.caret});

  static const none = HostTextFocus();

  /// An editable control has keyboard focus.
  final bool active;

  /// Moves whenever focus lands on another control.
  final int serial;

  /// The caret (or the field), normalized to the streamed display.
  final Rect? caret;

  static HostTextFocus? fromBytes(Uint8List bytes) {
    if (bytes.length < 20) return null;
    final data = ByteData.sublistView(bytes);
    return HostTextFocus(
      active: data.getUint8(0) != 0,
      serial: data.getUint16(2, Endian.little),
      caret: data.getUint8(1) != 0
          ? Rect.fromLTWH(
              data.getFloat32(4, Endian.little),
              data.getFloat32(8, Endian.little),
              data.getFloat32(12, Endian.little),
              data.getFloat32(16, Endian.little),
            )
          : null,
    );
  }

  @override
  bool operator ==(Object other) =>
      other is HostTextFocus &&
      other.active == active &&
      other.serial == serial &&
      other.caret == caret;

  @override
  int get hashCode => Object.hash(active, serial, caret);
}

/// Protocol `TouchPhase` order, offset by one on the wire.
enum TouchPhase { down, move, up, cancel }

/// One finger in an `InputTouch` frame; [x] and [y] are normalized to the
/// streamed display.
class TouchContact {
  const TouchContact({
    required this.id,
    required this.phase,
    required this.x,
    required this.y,
  });

  static const maxContacts = 10;
  static const byteSize = 12;

  /// 0..9, stable while the finger stays down.
  final int id;
  final TouchPhase phase;
  final double x;
  final double y;
}

class SessionStatsSnapshot {
  const SessionStatsSnapshot({
    this.fps = 0,
    this.rttMs = 0,
    this.bitrateKbps = 0,
    this.packetLossPercent = 0.0,
    this.decodeTimeMs = 0.0,
    this.videoWidth = 0,
    this.videoHeight = 0,
    this.codecId = 0,
    this.colorFlags = 0,
    this.transportRoute = 0,
    this.receivedFrames = 0,
    this.droppedFrames = 0,
    this.firstFrameRendered = false,
    this.sessionActive = false,
  });

  final int fps;
  final int rttMs;
  final int bitrateKbps;
  final double packetLossPercent;
  final double decodeTimeMs;
  final int videoWidth;
  final int videoHeight;
  final int codecId;
  final int colorFlags;
  final int transportRoute;
  final int receivedFrames;
  final int droppedFrames;
  final bool firstFrameRendered;
  final bool sessionActive;

  String get routeLabel => switch (transportRoute) {
    1 => 'LAN',
    2 => 'IPv6',
    3 => 'P2P',
    4 => 'Relay',
    _ => '--',
  };

  String get codecLabel => switch (codecId) {
    1 => 'H.264',
    2 => 'HEVC',
    3 => 'AV1',
    _ => '--',
  };

  bool get isHdr => (colorFlags & 0x01) != 0;
  bool get isYuv444 => (colorFlags & 0x02) != 0;
}
