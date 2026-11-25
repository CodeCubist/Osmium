import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/models/account_models.dart';
import '../core/models/device_identity.dart';
import '../core/models/osmium_settings.dart';
import '../services/account/account_service.dart';
import '../services/engine/osmium_engine.dart';
import '../services/engine/platform_bridge.dart';
import '../services/network/wake_on_lan_service.dart';
import '../services/signaling/signaling_client.dart';
import '../services/storage/device_wallpaper_store.dart';
import '../services/storage/secure_storage_service.dart';
import '../services/storage/settings_repository.dart';

final secureStorageProvider = Provider<SecureStorageService>(
  (ref) => SecureStorageService(),
);
final settingsRepositoryProvider = Provider<SettingsRepository>(
  (ref) => SettingsRepository(),
);
final deviceWallpaperStoreProvider = Provider<DeviceWallpaperStore>(
  (ref) => DeviceWallpaperStore(),
);
final osmiumEngineProvider = Provider<OsmiumEngine>((ref) => OsmiumEngine());
final platformBridgeProvider = Provider<PlatformBridge>(
  (ref) => PlatformBridge(),
);

/// The sign-in saved on disk, read before the first frame in `main`.
final savedAccountSessionProvider = Provider<AccountSessionData?>(
  (ref) => null,
);

final accountServiceProvider = Provider<AccountService>((ref) {
  final svc = AccountService(
    ref.read(secureStorageProvider),
    savedSession: ref.read(savedAccountSessionProvider),
  );
  ref.onDispose(svc.dispose);
  return svc;
});

final signalingClientProvider = Provider<SignalingClient>((ref) {
  final client = SignalingClient();
  ref.onDispose(client.dispose);
  return client;
});

class AppController extends ChangeNotifier {
  AppController({
    required this.settingsRepo,
    required this.accountService,
    required this.signaling,
    DeviceWallpaperStore? wallpaperStore,
  }) : wallpaperStore = wallpaperStore ?? DeviceWallpaperStore(),
       _account = accountService.session,
       // A saved sign-in shows device placeholders until the list arrives.
       _refreshingDevices = accountService.isLoggedIn;

  final SettingsRepository settingsRepo;
  final AccountService accountService;
  final SignalingClient signaling;
  final DeviceWallpaperStore wallpaperStore;

  bool _initialized = false;
  bool get initialized => _initialized;

  DeviceIdentity _identity = const DeviceIdentity(
    deviceId: '000000000',
    deviceName: 'Osmium Mobile',
  );
  DeviceIdentity get identity => _identity;

  OsmiumSettings _settings = const OsmiumSettings();
  OsmiumSettings get settings => _settings;

  List<ConnectionHistoryEntry> _history = const [];
  List<ConnectionHistoryEntry> get history => _history;

  AccountSessionData? _account;
  AccountSessionData? get account => _account;
  bool get isLoggedIn => accountService.isLoggedIn;
  bool get isProfileComplete => _account?.isProfileComplete ?? false;

  OAuthBindingsData? _oauthBindings;
  OAuthBindingsData? get oauthBindings => _oauthBindings;
  bool _oauthBindingsLoading = false;
  bool get oauthBindingsLoading => _oauthBindingsLoading;
  bool _oauthBindingsFailed = false;
  bool get oauthBindingsFailed => _oauthBindingsFailed;

  List<AccountDeviceDto> _accountDevices = const [];
  List<AccountDeviceDto> get accountDevices => _accountDevices;

  Map<String, String> _deviceWallpapers = const {};
  String? wallpaperPathFor(String deviceId) =>
      _deviceWallpapers[DeviceIdentity.normalizeDeviceId(deviceId)];

  bool _refreshingDevices;
  bool get refreshingDevices => _refreshingDevices;

  SignalingConnectionState _signalingState =
      SignalingConnectionState.disconnected;
  SignalingConnectionState get signalingState => _signalingState;

  StreamSubscription<SignalingConnectionState>? _sigStateSub;
  StreamSubscription<List<AccountDeviceDto>>? _sigDevicesSub;
  StreamSubscription<void>? _sigUnreachableSub;
  StreamSubscription<AccountSessionData?>? _acctSub;

  // Startup-only alert: once signaling has registered, later drops just show
  // as an offline state, like the Windows client.
  bool _signalingEverConnected = false;
  bool _unreachableAlertArmed = true;
  final _serverUnreachableCtrl = StreamController<void>.broadcast();

  /// Fires when the server can't be reached on launch (three failed tries in
  /// a row, before any successful connect). Re-armed by [retrySignaling].
  Stream<void> get serverUnreachable => _serverUnreachableCtrl.stream;

  void retrySignaling() {
    _unreachableAlertArmed = true;
    signaling.retryNow();
  }

  Future<void> initialize() async {
    if (_initialized) return;

    _identity = await settingsRepo.loadOrCreateIdentity();
    _settings = await settingsRepo.loadSettings();
    _history = await settingsRepo.loadHistory();
    _deviceWallpapers = Map.unmodifiable(await wallpaperStore.loadAll());
    _account = await accountService.initialize();

    _sigStateSub = signaling.stateStream.listen((state) {
      _signalingState = state;
      if (state == SignalingConnectionState.connected) {
        _signalingEverConnected = true;
      }
      notifyListeners();
    });

    _sigUnreachableSub = signaling.unreachableStream.listen((_) {
      if (_signalingEverConnected || !_unreachableAlertArmed) return;
      _unreachableAlertArmed = false;
      _serverUnreachableCtrl.add(null);
    });

    _sigDevicesSub = signaling.accountDevicesStream.listen((devices) {
      _accountDevices = devices
          .where((d) => d.deviceId != _identity.deviceId)
          .toList();
      notifyListeners();
    });

    _acctSub = accountService.sessionStream.listen((session) {
      final prevUserId = _account?.userId;
      _account = session;
      signaling.updateAuthToken(accountService.currentLtoken);
      if (session != null) {
        refreshAccountDevices();
        if (_oauthBindings == null || prevUserId != session.userId) {
          unawaited(refreshOAuthBindings());
        }
      } else {
        _accountDevices = const [];
        _oauthBindings = null;
        _oauthBindingsLoading = false;
        _oauthBindingsFailed = false;
      }
      notifyListeners();
    });

    signaling.start(
      deviceId: _identity.deviceId,
      deviceName: _identity.deviceName,
      ltoken: accountService.currentLtoken,
    );

    _initialized = true;
    notifyListeners();

    if (accountService.isLoggedIn) {
      unawaited(_restoreAccount());
    } else if (_refreshingDevices) {
      _refreshingDevices = false;
      notifyListeners();
    }
  }

  /// Signed in from the saved session: rotate the ltoken, then refresh the
  /// profile and device list, keeping the placeholders up the whole time.
  Future<void> _restoreAccount() async {
    _refreshingDevices = true;
    notifyListeners();
    try {
      await accountService.rotateLtoken();
    } finally {
      _refreshingDevices = false;
    }
    if (accountService.isLoggedIn) {
      await Future.wait([
        refreshAccountProfile(),
        refreshAccountDevices(),
        refreshOAuthBindings(),
      ]);
    } else {
      notifyListeners();
    }
  }

  Future<AccountApiResult<AccountSessionData>> refreshAccountProfile() async {
    if (!accountService.isLoggedIn) {
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    final res = await accountService.fetchAccountStatus();
    if (res.ok && res.data != null) {
      _account = res.data;
      notifyListeners();
    }
    unawaited(refreshOAuthBindings());
    return res;
  }

  Future<AccountApiResult<OAuthBindingsData>> refreshOAuthBindings() async {
    if (!accountService.isLoggedIn) {
      _oauthBindings = null;
      _oauthBindingsLoading = false;
      _oauthBindingsFailed = false;
      return const AccountApiResult(
        ok: false,
        httpStatusCode: 401,
        message: '未登录',
      );
    }
    _oauthBindingsLoading = true;
    _oauthBindingsFailed = false;
    notifyListeners();
    try {
      final res = await accountService.fetchOAuthBindings();
      if (res.ok && res.data != null) {
        _oauthBindings = res.data;
        _oauthBindingsFailed = false;
      } else if (_oauthBindings == null) {
        _oauthBindingsFailed = true;
      }
      return res;
    } finally {
      _oauthBindingsLoading = false;
      notifyListeners();
    }
  }

  Future<AccountApiResult<OAuthStartData>> startOAuthBind(String provider) {
    return accountService.startOAuthBind(provider: provider);
  }

  Future<AccountApiResult<Map<String, dynamic>>> unbindOAuthProvider(
    String provider,
  ) async {
    final res = await accountService.unbindOAuthProvider(provider: provider);
    if (res.ok) {
      await refreshOAuthBindings();
    }
    return res;
  }

  Future<AccountApiResult<AccountSessionData>> updateAccountProfile({
    String? username,
    String? avatarData,
    bool clearAvatar = false,
  }) async {
    final res = await accountService.updateProfile(
      username: username,
      avatarData: avatarData,
      clearAvatar: clearAvatar,
    );
    if (res.ok && res.data != null) {
      _account = res.data;
      notifyListeners();
    }
    return res;
  }

  Future<AccountApiResult<Map<String, dynamic>>> startChangeEmailSendCode({
    String? password,
    required String newEmail,
  }) {
    return accountService.startChangeEmailSendCode(
      password: password,
      newEmail: newEmail,
    );
  }

  Future<AccountApiResult<AccountSessionData>> confirmChangeEmail({
    String? password,
    required String newEmail,
    required String code,
  }) async {
    final res = await accountService.confirmChangeEmail(
      password: password,
      newEmail: newEmail,
      code: code,
    );
    if (res.ok && res.data != null) {
      _account = res.data;
      notifyListeners();
    }
    return res;
  }

  Future<AccountApiResult<Map<String, dynamic>>> startSetPasswordSendCode() {
    return accountService.startSetPasswordSendCode();
  }

  Future<AccountApiResult<AccountSessionData>> confirmSetPassword({
    required String code,
    required String newPassword,
  }) async {
    final res = await accountService.confirmSetPassword(
      code: code,
      newPassword: newPassword,
    );
    if (res.ok && res.data != null) {
      _account = res.data;
      notifyListeners();
      unawaited(refreshAccountDevices());
    }
    return res;
  }

  Future<void> updateSettings(OsmiumSettings next) async {
    _settings = next;
    notifyListeners();
    await settingsRepo.saveSettings(next);
  }

  Future<void> renameSelf(String nextName) async {
    final trimmed = nextName.trim();
    if (trimmed.isEmpty) return;
    _identity = _identity.copyWith(deviceName: trimmed);
    await settingsRepo.saveDeviceName(trimmed);
    signaling.updateDeviceName(trimmed);
    notifyListeners();
  }

  Future<void> refreshAccountDevices() async {
    if (!accountService.isLoggedIn || _refreshingDevices) return;
    _refreshingDevices = true;
    notifyListeners();
    try {
      final res = await accountService.fetchDevices();
      if (res.ok && res.data != null) {
        _accountDevices = res.data!
            .where((d) => d.deviceId != _identity.deviceId)
            .toList();
      }
    } finally {
      _refreshingDevices = false;
      notifyListeners();
    }
  }

  Future<bool> renameRemoteDevice(String deviceId, String customName) async {
    final res = await accountService.renameDevice(deviceId, customName);
    if (res.ok) {
      await refreshAccountDevices();
    }
    return res.ok;
  }

  Future<bool> removeRemoteDevice(String deviceId) async {
    final res = await accountService.removeDevice(deviceId);
    if (res.ok) {
      final normalized = DeviceIdentity.normalizeDeviceId(deviceId);
      _accountDevices = _accountDevices
          .where((d) => d.deviceId != deviceId)
          .toList();
      final stillInHistory = _history.any(
        (h) => DeviceIdentity.normalizeDeviceId(h.deviceId) == normalized,
      );
      if (!stillInHistory && _deviceWallpapers.containsKey(normalized)) {
        final next = Map<String, String>.from(_deviceWallpapers)
          ..remove(normalized);
        _deviceWallpapers = Map.unmodifiable(next);
        unawaited(wallpaperStore.deleteWallpaper(normalized));
      }
      notifyListeners();
    }
    return res.ok;
  }

  Future<void> saveDeviceWallpaper(String deviceId, Uint8List bytes) async {
    final normalized = DeviceIdentity.normalizeDeviceId(deviceId);
    if (normalized.length != 9) return;
    final savedPath = await wallpaperStore.saveWallpaper(normalized, bytes);
    if (savedPath == null) return;
    final next = Map<String, String>.from(_deviceWallpapers)
      ..[normalized] = savedPath;
    _deviceWallpapers = Map.unmodifiable(next);
    notifyListeners();
  }

  void forceDisconnectDevice(String deviceId) {
    signaling.sendForceDisconnect(deviceId);
  }

  Future<({bool ok, String message})> executeRemotePowerAction(
    AccountDeviceDto device,
    String action,
  ) async {
    final normAction = action.trim().toLowerCase();
    if (!accountService.isLoggedIn) {
      return (ok: false, message: '请先登录同一账户后再执行远程电源操作');
    }
    if (!isProfileComplete) {
      return (ok: false, message: '为了保障设备安全，请先在「我的」中绑定邮箱并设置登录密码，完成后即可使用远程开关机功能');
    }
    if (!device.isComputerDevice) {
      return (ok: false, message: '仅支持对同账户电脑设备执行远程电源操作');
    }

    final res = await accountService.sendDevicePowerCommand(
      deviceId: device.deviceId,
      action: normAction,
    );
    if (!res.ok || res.data == null) {
      return (
        ok: false,
        message: res.message.isNotEmpty ? res.message : '远程电源操作失败',
      );
    }

    final data = res.data!;
    final name = device.displayName;
    if (normAction == 'wake') {
      if (data.alreadyOnline) {
        return (ok: true, message: '「$name」当前已处于在线状态，无需唤醒。');
      }
      final macs = data.wolMacs.isNotEmpty ? data.wolMacs : device.wolMacs;
      final broadcasts = data.wolBroadcasts.isNotEmpty
          ? data.wolBroadcasts
          : device.wolBroadcasts;
      if (macs.isNotEmpty) {
        await WakeOnLanService.sendMagicPackets(
          macAddresses: macs,
          targetBroadcasts: broadcasts,
          ports: const [9, 7],
          repeatCount: 3,
        );
      }
      if (data.relayedCount > 0) {
        return (
          ok: true,
          message:
              '已向「$name」发送远程开机信号（通过本机局域网及${data.relayedCount}台在线设备协助唤醒），请等待15-60秒。',
        );
      }
      return (
        ok: true,
        message:
            '已通过本机局域网向「$name」发送远程开机信号。若不在同一Wi-Fi/局域网，需目标局域网内有同账号在线电脑协助唤醒。',
      );
    }

    if (normAction == 'restart') {
      final delay = data.delaySeconds > 0 ? data.delaySeconds : 3;
      return (ok: true, message: '已向「$name」发送远程重启指令，目标电脑将在约$delay秒后重启。');
    }

    final delay = data.delaySeconds > 0 ? data.delaySeconds : 3;
    return (ok: true, message: '已向「$name」发送远程关机指令，目标电脑将在约$delay秒后关机。');
  }

  Future<void> recordConnectionHistory({
    required String deviceId,
    required String deviceName,
    String savedAccessCode = '',
    bool sameAccount = false,
  }) async {
    _history = await settingsRepo.upsertHistory(
      ConnectionHistoryEntry(
        deviceId: DeviceIdentity.normalizeDeviceId(deviceId),
        deviceName: deviceName.isNotEmpty
            ? deviceName
            : DeviceIdentity.formatDeviceId(deviceId),
        savedAccessCode: savedAccessCode,
        lastConnectedAtMs: DateTime.now().millisecondsSinceEpoch,
        sameAccount: sameAccount,
      ),
    );
    notifyListeners();
  }

  Future<void> removeHistoryEntry(String deviceId) async {
    _history = await settingsRepo.removeHistory(deviceId);
    notifyListeners();
  }

  Future<void> logout({bool revokeAll = false}) async {
    await accountService.logout(
      deviceId: _identity.deviceId,
      revokeAll: revokeAll,
    );
    _accountDevices = const [];
    _oauthBindings = null;
    _oauthBindingsLoading = false;
    _oauthBindingsFailed = false;
    notifyListeners();
  }

  @override
  void dispose() {
    _sigStateSub?.cancel();
    _sigDevicesSub?.cancel();
    _sigUnreachableSub?.cancel();
    _acctSub?.cancel();
    _serverUnreachableCtrl.close();
    super.dispose();
  }
}

final appControllerProvider = ChangeNotifierProvider<AppController>((ref) {
  return AppController(
    settingsRepo: ref.read(settingsRepositoryProvider),
    accountService: ref.read(accountServiceProvider),
    signaling: ref.read(signalingClientProvider),
    wallpaperStore: ref.read(deviceWallpaperStoreProvider),
  );
});
