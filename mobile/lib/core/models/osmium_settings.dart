enum ControlMode {
  /// Default: direct screen tap-to-click with precision drag/long-press right-click
  directTouch,

  /// Precision virtual cursor / trackpad mode with micro-deceleration & magnifier
  precisionMouse,
}

enum OsdLevel { off, compact, standard, detailed }

enum ThemePreference { system, light, dark, monetSystem }

enum VideoCodecPref { auto, h264, hevc, av1 }

class ConnectionHistoryEntry {
  const ConnectionHistoryEntry({
    required this.deviceId,
    required this.deviceName,
    this.savedAccessCode = '',
    required this.lastConnectedAtMs,
    this.sameAccount = false,
  });

  final String deviceId;
  final String deviceName;
  final String savedAccessCode;
  final int lastConnectedAtMs;
  final bool sameAccount;

  Map<String, dynamic> toJson() => {
    'deviceId': deviceId,
    'deviceName': deviceName,
    'savedAccessCode': savedAccessCode,
    'lastConnectedAtMs': lastConnectedAtMs,
    'sameAccount': sameAccount,
  };

  factory ConnectionHistoryEntry.fromJson(Map<String, dynamic> json) {
    return ConnectionHistoryEntry(
      deviceId: (json['deviceId'] ?? '').toString(),
      deviceName: (json['deviceName'] ?? '').toString(),
      savedAccessCode: (json['savedAccessCode'] ?? '').toString(),
      lastConnectedAtMs: (json['lastConnectedAtMs'] as num?)?.toInt() ?? 0,
      sameAccount: json['sameAccount'] == true,
    );
  }
}

class OsmiumSettings {
  const OsmiumSettings({
    this.controlMode = ControlMode.directTouch,
    this.cursorSensitivity = 1.15,
    this.precisionMagnifierEnabled = true,
    this.enableHaptics = true,
    this.desiredWidth = 1920,
    this.desiredHeight = 1080,
    this.desiredFps = 60,
    this.desiredBitrateKbps = 12000,
    this.codecPref = VideoCodecPref.auto,
    this.enableHdr = false,
    this.enableYuv444 = false,
    this.enableAudio = true,
    this.enableEncryption = true,
    this.osdLevel = OsdLevel.compact,
    this.themePreference = ThemePreference.system,
    this.sharedClipboardEnabled = true,
    this.fileTransferEnabled = true,
    this.keyboardOpacity = 0.78,
  });

  final ControlMode controlMode;
  final double cursorSensitivity;
  final bool precisionMagnifierEnabled;
  final bool enableHaptics;
  final int desiredWidth;
  final int desiredHeight;
  final int desiredFps;
  final int desiredBitrateKbps;
  final VideoCodecPref codecPref;
  final bool enableHdr;
  final bool enableYuv444;
  final bool enableAudio;
  final bool enableEncryption;
  final OsdLevel osdLevel;
  final ThemePreference themePreference;
  final bool sharedClipboardEnabled;
  final bool fileTransferEnabled;
  final double keyboardOpacity;

  OsmiumSettings copyWith({
    ControlMode? controlMode,
    double? cursorSensitivity,
    bool? precisionMagnifierEnabled,
    bool? enableHaptics,
    int? desiredWidth,
    int? desiredHeight,
    int? desiredFps,
    int? desiredBitrateKbps,
    VideoCodecPref? codecPref,
    bool? enableHdr,
    bool? enableYuv444,
    bool? enableAudio,
    bool? enableEncryption,
    OsdLevel? osdLevel,
    ThemePreference? themePreference,
    bool? sharedClipboardEnabled,
    bool? fileTransferEnabled,
    double? keyboardOpacity,
  }) {
    return OsmiumSettings(
      controlMode: controlMode ?? this.controlMode,
      cursorSensitivity: cursorSensitivity ?? this.cursorSensitivity,
      precisionMagnifierEnabled:
          precisionMagnifierEnabled ?? this.precisionMagnifierEnabled,
      enableHaptics: enableHaptics ?? this.enableHaptics,
      desiredWidth: desiredWidth ?? this.desiredWidth,
      desiredHeight: desiredHeight ?? this.desiredHeight,
      desiredFps: desiredFps ?? this.desiredFps,
      desiredBitrateKbps: desiredBitrateKbps ?? this.desiredBitrateKbps,
      codecPref: codecPref ?? this.codecPref,
      enableHdr: enableHdr ?? this.enableHdr,
      enableYuv444: enableYuv444 ?? this.enableYuv444,
      enableAudio: enableAudio ?? this.enableAudio,
      enableEncryption: enableEncryption ?? this.enableEncryption,
      osdLevel: osdLevel ?? this.osdLevel,
      themePreference: themePreference ?? this.themePreference,
      sharedClipboardEnabled:
          sharedClipboardEnabled ?? this.sharedClipboardEnabled,
      fileTransferEnabled: fileTransferEnabled ?? this.fileTransferEnabled,
      keyboardOpacity: keyboardOpacity ?? this.keyboardOpacity,
    );
  }

  Map<String, dynamic> toJson() => {
    'controlMode': controlMode.index,
    'cursorSensitivity': cursorSensitivity,
    'precisionMagnifierEnabled': precisionMagnifierEnabled,
    'enableHaptics': enableHaptics,
    'desiredWidth': desiredWidth,
    'desiredHeight': desiredHeight,
    'desiredFps': desiredFps,
    'desiredBitrateKbps': desiredBitrateKbps,
    'codecPref': codecPref.index,
    'enableHdr': enableHdr,
    'enableYuv444': enableYuv444,
    'enableAudio': enableAudio,
    'enableEncryption': enableEncryption,
    'osdLevel': osdLevel.index,
    'themePreference': themePreference.index,
    'sharedClipboardEnabled': sharedClipboardEnabled,
    'fileTransferEnabled': fileTransferEnabled,
    'keyboardOpacity': keyboardOpacity,
  };

  factory OsmiumSettings.fromJson(Map<String, dynamic> json) {
    T enumAt<T>(List<T> values, dynamic idx, T fallback) {
      final i = (idx as num?)?.toInt();
      if (i == null || i < 0 || i >= values.length) return fallback;
      return values[i];
    }

    return OsmiumSettings(
      controlMode: enumAt(
        ControlMode.values,
        json['controlMode'],
        ControlMode.directTouch,
      ),
      cursorSensitivity:
          (json['cursorSensitivity'] as num?)?.toDouble() ?? 1.15,
      precisionMagnifierEnabled: json['precisionMagnifierEnabled'] != false,
      enableHaptics: json['enableHaptics'] != false,
      desiredWidth: (json['desiredWidth'] as num?)?.toInt() ?? 1920,
      desiredHeight: (json['desiredHeight'] as num?)?.toInt() ?? 1080,
      desiredFps: (json['desiredFps'] as num?)?.toInt() ?? 60,
      desiredBitrateKbps:
          (json['desiredBitrateKbps'] as num?)?.toInt() ?? 12000,
      codecPref: enumAt(
        VideoCodecPref.values,
        json['codecPref'],
        VideoCodecPref.auto,
      ),
      enableHdr: json['enableHdr'] == true,
      enableYuv444: json['enableYuv444'] == true,
      enableAudio: json['enableAudio'] != false,
      enableEncryption: json['enableEncryption'] != false,
      osdLevel: enumAt(OsdLevel.values, json['osdLevel'], OsdLevel.compact),
      themePreference: enumAt(
        ThemePreference.values,
        json['themePreference'],
        ThemePreference.system,
      ),
      sharedClipboardEnabled: json['sharedClipboardEnabled'] != false,
      fileTransferEnabled: json['fileTransferEnabled'] != false,
      keyboardOpacity: (json['keyboardOpacity'] as num?)?.toDouble() ?? 0.78,
    );
  }
}
