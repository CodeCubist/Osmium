class OsmiumConstants {
  OsmiumConstants._();

  static const String defaultSignalingUrl = 'wss://osmium.fufu.skin';
  static const String defaultApiBaseUrl = 'https://osmium.fufu.skin';
  static const String protocolVersion = '1.1.2';
  static const String minimumSupportedVersion = '1.1.1';
  static const String userAgent = 'Osmium-Mobile/1.1.2';

  static bool isVersionSupported(String? raw) {
    if (raw == null) return false;
    final clean = raw.trim().replaceFirst(RegExp(r'^[vV]'), '');
    final m = RegExp(r'^(\d+)\.(\d+)(?:\.(\d+))?').firstMatch(clean);
    if (m == null) return false;
    final maj = int.parse(m.group(1)!);
    final min = int.parse(m.group(2)!);
    final pat = int.parse(m.group(3) ?? '0');
    if (maj != 1) return maj > 1;
    if (min != 1) return min > 1;
    return pat >= 1;
  }

  /// Frame-rate choices supported by the Windows host.
  static const int minStreamFps = 10;
  static const int maxStreamFps = 240;
  static const List<int> fpsPresets = [30, 60, 90, 120, 144, 165, maxStreamFps];
}

/// Mirrors Windows `Osmium.Core.Models.OsmiumErrorCode`.
class OsmiumErrorCode {
  OsmiumErrorCode._();

  static const int ok = 0;
  static const int captchaRequired = 100;
  static const int accountCaptchaRequired = captchaRequired;
  static const int challengePending = 101;
  static const int twoFaRequired = 102;
  static const int payloadTooLarge = 1003;
  static const int invalidEmail = 1004;
  static const int invalidPassword = 1005;
  static const int invalidOtpFormat = 1006;
  static const int invalidUsername = 1007;
  static const int invalidAvatar = 1008;
  static const int oauthProviderInvalid = 1014;
  static const int forbidden = 2001;
  static const int invalidCredentials = 2002;
  static const int emailAlreadyRegistered = 2003;
  static const int accountNotFound = 2004;
  static const int accountLocked = 2005;
  static const int emailMismatch = 2008;
  static const int accountAuthRequired = 2010;
  static const int blacklistedByHost = 2011;
  static const int accountProfileIncomplete = 2012;
  static const int accountEmailRequired = 2013;
  static const int passwordAlreadySet = 2014;
  static const int oauthAlreadyBound = 2015;
  static const int oauthProviderAlreadyBound = 2016;
  static const int twoFaAlreadyEnabled = 2017;
  static const int twoFaNotEnabled = 2018;
  static const int loginKeyInvalid = 2019;
  static const int recoveryEmailNotSet = 2020;
  static const int recoveryEmailAlreadyUsed = 2021;
  static const int recoveryEmailSameAsPrimary = 2022;
  static const int otpExpired = 3007;
  static const int otpInvalid = 3008;
  static const int otpTooManyAttempts = 3009;
  static const int otpCooldown = 3010;
  static const int qrExpired = 3011;
  static const int qrNotFound = 3012;
  static const int qrInvalidSignature = 3013;
  static const int qrStateInvalid = 3014;
  static const int qrConfirmCooldown = 3015;
  static const int qrCancelled = 3016;
  static const int oauthExpired = 3017;
  static const int oauthFailed = 3018;
  static const int twoFaInvalid = 3019;
  static const int twoFaExpired = 3020;
  static const int recoverySessionExpired = 3021;
  static const int emailConfirmTooManyAttempts = 3022;
  static const int rateLimited = 4001;
  static const int serviceQuotaExceeded = 4004;
  static const int connectCooldown = 4005;
  static const int deviceOffline = 4006;
  static const int remoteDisabled = 4007;
  static const int selfConnect = 4008;
  static const int powerDisabled = 4011;
  static const int powerUnsupported = 4012;
  static const int powerCooldown = 4013;
  static const int usernameChangeCooldown = 4014;
  static const int avatarChangeCooldown = 4015;
  static const int emailChangeCooldown = 4016;
  static const int passwordResetCooldown = 4017;
  static const int quickLaunchDisabled = 4018;
  static const int quickLaunchNotFound = 4019;
  static const int quickLaunchLimitExceeded = 4020;
  static const int quickLaunchUnsupported = 4021;
  static const int loginKeyLimitExceeded = 4022;
  static const int versionUnsupported = 4023;
  static const int deviceIdConflict = 4024;
  static const int recoveryEmailChangeCooldown = 4025;
  static const int oauthNotConfigured = 5007;
}

class WinKeySpec {
  const WinKeySpec({
    required this.vk,
    required this.scan,
    this.isExtended = false,
  });

  final int vk;
  final int scan;
  final bool isExtended;
}

/// Standard Windows Virtual-Key + Hardware ScanCode table matching Osmium Host injector.
class WinKeys {
  WinKeys._();

  static const esc = WinKeySpec(vk: 0x1B, scan: 0x01);
  static const tab = WinKeySpec(vk: 0x09, scan: 0x0F);
  static const capsLock = WinKeySpec(vk: 0x14, scan: 0x3A);
  static const shift = WinKeySpec(vk: 0x10, scan: 0x2A);
  static const lShift = WinKeySpec(vk: 0xA0, scan: 0x2A);
  static const rShift = WinKeySpec(vk: 0xA1, scan: 0x36);
  static const ctrl = WinKeySpec(vk: 0x11, scan: 0x1D);
  static const lCtrl = WinKeySpec(vk: 0xA2, scan: 0x1D);
  static const rCtrl = WinKeySpec(vk: 0xA3, scan: 0x1D, isExtended: true);
  static const alt = WinKeySpec(vk: 0x12, scan: 0x38);
  static const lAlt = WinKeySpec(vk: 0xA4, scan: 0x38);
  static const rAlt = WinKeySpec(vk: 0xA5, scan: 0x38, isExtended: true);
  static const lWin = WinKeySpec(vk: 0x5B, scan: 0x5B, isExtended: true);
  static const space = WinKeySpec(vk: 0x20, scan: 0x39);
  static const enter = WinKeySpec(vk: 0x0D, scan: 0x1C);
  static const backspace = WinKeySpec(vk: 0x08, scan: 0x0E);
  static const delete = WinKeySpec(vk: 0x2E, scan: 0x53, isExtended: true);
  static const insert = WinKeySpec(vk: 0x2D, scan: 0x52, isExtended: true);
  static const home = WinKeySpec(vk: 0x24, scan: 0x47, isExtended: true);
  static const end = WinKeySpec(vk: 0x23, scan: 0x4F, isExtended: true);
  static const pageUp = WinKeySpec(vk: 0x21, scan: 0x49, isExtended: true);
  static const pageDown = WinKeySpec(vk: 0x22, scan: 0x51, isExtended: true);
  static const printScreen = WinKeySpec(vk: 0x2C, scan: 0x37, isExtended: true);

  static const arrowUp = WinKeySpec(vk: 0x26, scan: 0x48, isExtended: true);
  static const arrowDown = WinKeySpec(vk: 0x28, scan: 0x50, isExtended: true);
  static const arrowLeft = WinKeySpec(vk: 0x25, scan: 0x4B, isExtended: true);
  static const arrowRight = WinKeySpec(vk: 0x27, scan: 0x4D, isExtended: true);

  static const f1 = WinKeySpec(vk: 0x70, scan: 0x3B);
  static const f2 = WinKeySpec(vk: 0x71, scan: 0x3C);
  static const f3 = WinKeySpec(vk: 0x72, scan: 0x3D);
  static const f4 = WinKeySpec(vk: 0x73, scan: 0x3E);
  static const f5 = WinKeySpec(vk: 0x74, scan: 0x3F);
  static const f6 = WinKeySpec(vk: 0x75, scan: 0x40);
  static const f7 = WinKeySpec(vk: 0x76, scan: 0x41);
  static const f8 = WinKeySpec(vk: 0x77, scan: 0x42);
  static const f9 = WinKeySpec(vk: 0x78, scan: 0x43);
  static const f10 = WinKeySpec(vk: 0x79, scan: 0x44);
  static const f11 = WinKeySpec(vk: 0x7A, scan: 0x57);
  static const f12 = WinKeySpec(vk: 0x7B, scan: 0x58);

  static const Map<String, WinKeySpec> byLabel = {
    '1': WinKeySpec(vk: 0x31, scan: 0x02),
    '2': WinKeySpec(vk: 0x32, scan: 0x03),
    '3': WinKeySpec(vk: 0x33, scan: 0x04),
    '4': WinKeySpec(vk: 0x34, scan: 0x05),
    '5': WinKeySpec(vk: 0x35, scan: 0x06),
    '6': WinKeySpec(vk: 0x36, scan: 0x07),
    '7': WinKeySpec(vk: 0x37, scan: 0x08),
    '8': WinKeySpec(vk: 0x38, scan: 0x09),
    '9': WinKeySpec(vk: 0x39, scan: 0x0A),
    '0': WinKeySpec(vk: 0x30, scan: 0x0B),
    '-': WinKeySpec(vk: 0xBD, scan: 0x0C),
    '=': WinKeySpec(vk: 0xBB, scan: 0x0D),
    '`': WinKeySpec(vk: 0xC0, scan: 0x29),
    'Q': WinKeySpec(vk: 0x51, scan: 0x10),
    'W': WinKeySpec(vk: 0x57, scan: 0x11),
    'E': WinKeySpec(vk: 0x45, scan: 0x12),
    'R': WinKeySpec(vk: 0x52, scan: 0x13),
    'T': WinKeySpec(vk: 0x54, scan: 0x14),
    'Y': WinKeySpec(vk: 0x59, scan: 0x15),
    'U': WinKeySpec(vk: 0x55, scan: 0x16),
    'I': WinKeySpec(vk: 0x49, scan: 0x17),
    'O': WinKeySpec(vk: 0x4F, scan: 0x18),
    'P': WinKeySpec(vk: 0x50, scan: 0x19),
    '[': WinKeySpec(vk: 0xDB, scan: 0x1A),
    ']': WinKeySpec(vk: 0xDD, scan: 0x1B),
    '\\': WinKeySpec(vk: 0xDC, scan: 0x2B),
    'A': WinKeySpec(vk: 0x41, scan: 0x1E),
    'S': WinKeySpec(vk: 0x53, scan: 0x1F),
    'D': WinKeySpec(vk: 0x44, scan: 0x20),
    'F': WinKeySpec(vk: 0x46, scan: 0x21),
    'G': WinKeySpec(vk: 0x47, scan: 0x22),
    'H': WinKeySpec(vk: 0x48, scan: 0x23),
    'J': WinKeySpec(vk: 0x4A, scan: 0x24),
    'K': WinKeySpec(vk: 0x4B, scan: 0x25),
    'L': WinKeySpec(vk: 0x4C, scan: 0x26),
    ';': WinKeySpec(vk: 0xBA, scan: 0x27),
    '\'': WinKeySpec(vk: 0xDE, scan: 0x28),
    'Z': WinKeySpec(vk: 0x5A, scan: 0x2C),
    'X': WinKeySpec(vk: 0x58, scan: 0x2D),
    'C': WinKeySpec(vk: 0x43, scan: 0x2E),
    'V': WinKeySpec(vk: 0x56, scan: 0x2F),
    'B': WinKeySpec(vk: 0x42, scan: 0x30),
    'N': WinKeySpec(vk: 0x4E, scan: 0x31),
    'M': WinKeySpec(vk: 0x4D, scan: 0x32),
    ',': WinKeySpec(vk: 0xBC, scan: 0x33),
    '.': WinKeySpec(vk: 0xBE, scan: 0x34),
    '/': WinKeySpec(vk: 0xBF, scan: 0x35),
  };
}
