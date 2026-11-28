import 'dart:async';
import 'dart:math';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../core/constants/osmium_constants.dart';
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

  List<QuickLaunchAppDto> _quickLaunchApps = const [];
  List<QuickLaunchAppDto> get quickLaunchApps => _quickLaunchApps;

  bool _refreshingQuickLaunch = false;
  bool get refreshingQuickLaunch => _refreshingQuickLaunch;

  Map<String, String> _deviceWallpapers = const {};
  String? wallpaperPathFor(String deviceId) =>
      _deviceWallpapers[DeviceIdentity.normalizeDeviceId(deviceId)];

  bool _refreshingDevices;
  bool get refreshingDevices => _refreshingDevices;
  bool _devicesFetchInFlight = false;
  bool _pendingDevicesRefresh = false;
  bool _appInForeground = true;
  int _activeTabIndex = 0;
  bool _rotatingWsAuth = false;
  Timer? _ltokenRefreshTimer;
  Timer? _foregroundPresenceTimer;

  SignalingConnectionState _signalingState =
      SignalingConnectionState.disconnected;
  SignalingConnectionState get signalingState => _signalingState;

  StreamSubscription<SignalingConnectionState>? _sigStateSub;
  StreamSubscription<bool>? _sigAccountAuthSub;
  StreamSubscription<List<AccountDeviceDto>>? _sigDevicesSub;
  StreamSubscription<void>? _sigUnreachableSub;
  StreamSubscription<void>? _sigQuickLaunchSyncSub;
  StreamSubscription<(String, String?)>? _sigDeviceIdSub;
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
        if (accountService.isLoggedIn) {
          unawaited(refreshAccountDevices(silent: true));
        }
      }
      notifyListeners();
    });

    _sigAccountAuthSub = signaling.accountAuthStatusStream.listen((authed) {
      if (!authed && accountService.isLoggedIn && !_rotatingWsAuth) {
        unawaited(_ensureFreshLtokenForSignaling());
      } else if (authed && accountService.isLoggedIn) {
        unawaited(refreshAccountDevices(silent: true));
      }
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

    _sigQuickLaunchSyncSub = signaling.quickLaunchSyncStream.listen((_) {
      if (accountService.isLoggedIn) {
        unawaited(refreshQuickLaunchApps());
      }
    });

    _sigDeviceIdSub = signaling.deviceIdReassignedStream.listen((evt) {
      final (newId, newSecret) = evt;
      unawaited(_adoptAssignedDeviceIdentity(newId, newSecret));
    });

    _acctSub = accountService.sessionStream.listen((session) {
      final prevUserId = _account?.userId;
      _account = session;
      if (session != null && session.assignedDeviceId != null) {
        final normAssigned = DeviceIdentity.normalizeDeviceId(
          session.assignedDeviceId!,
        );
        if (normAssigned.length >= 6 &&
            normAssigned.length <= 12 &&
            normAssigned != _identity.deviceId) {
          unawaited(_adoptAssignedDeviceIdentity(normAssigned, null));
        }
      }
      signaling.updateAuthToken(accountService.currentLtoken);
      if (session != null) {
        refreshAccountDevices();
        unawaited(refreshQuickLaunchApps());
        if (_oauthBindings == null || prevUserId != session.userId) {
          unawaited(refreshOAuthBindings());
        }
      } else {
        _accountDevices = const [];
        _quickLaunchApps = const [];
        _oauthBindings = null;
        _oauthBindingsLoading = false;
        _oauthBindingsFailed = false;
      }
      notifyListeners();
    });

    signaling.start(
      deviceId: _identity.deviceId,
      deviceName: _identity.deviceName,
      deviceSecret: _identity.deviceSecret,
      ltoken: accountService.currentLtoken,
    );

    _ltokenRefreshTimer?.cancel();
    _ltokenRefreshTimer = Timer.periodic(const Duration(minutes: 6), (_) {
      if (!accountService.isLoggedIn) return;
      final sess = accountService.session;
      if (sess == null || sess.isLtokenExpiringSoon || !signaling.isAccountAuthenticated) {
        unawaited(_ensureFreshLtokenForSignaling());
      }
    });

    _foregroundPresenceTimer?.cancel();
    _foregroundPresenceTimer = Timer.periodic(const Duration(seconds: 25), (_) {
      if (!_appInForeground || !accountService.isLoggedIn) return;
      signaling.ensureAlive();
      if (_activeTabIndex == 0) {
        unawaited(refreshAccountDevices(silent: true));
      }
    });

    _initialized = true;
    notifyListeners();

    if (accountService.isLoggedIn) {
      unawaited(_restoreAccount());
    } else if (_refreshingDevices) {
      _refreshingDevices = false;
      notifyListeners();
    }
  }

  Future<void> _ensureFreshLtokenForSignaling() async {
    if (_rotatingWsAuth || !accountService.isLoggedIn) return;
    _rotatingWsAuth = true;
    try {
      final res = await accountService.rotateLtoken();
      if (res.ok && accountService.currentLtoken.isNotEmpty) {
        signaling.updateAuthToken(accountService.currentLtoken);
        unawaited(refreshAccountDevices(silent: true));
      }
    } finally {
      _rotatingWsAuth = false;
    }
  }

  void setAppLifecycleState(bool inForeground) {
    _appInForeground = inForeground;
  }

  void setActiveTab(int index) {
    _activeTabIndex = index;
    if (index == 0 && accountService.isLoggedIn) {
      signaling.ensureAlive();
      unawaited(refreshAccountDevices(silent: true));
    }
  }

  Future<void> onAppResumed() async {
    _appInForeground = true;
    if (!_initialized) return;
    signaling.ensureAlive(forceProbe: true);
    if (!accountService.isLoggedIn) return;
    final sess = accountService.session;
    if (sess == null || sess.isLtokenExpiringSoon || !signaling.isAccountAuthenticated) {
      await _ensureFreshLtokenForSignaling();
    } else {
      await refreshAccountDevices(silent: true);
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
        refreshQuickLaunchApps(),
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
    String? currentEmail,
    required String newEmail,
  }) {
    return accountService.startChangeEmailSendCode(
      password: password,
      currentEmail: currentEmail,
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

  Future<AccountApiResult<Map<String, dynamic>>> startBindRecoveryEmailSendCode({
    required String password,
    required String currentEmail,
    required String recoveryEmail,
  }) {
    return accountService.startBindRecoveryEmailSendCode(
      password: password,
      currentEmail: currentEmail,
      recoveryEmail: recoveryEmail,
    );
  }

  Future<AccountApiResult<AccountSessionData>> confirmBindRecoveryEmail({
    required String password,
    required String recoveryEmail,
    required String code,
  }) async {
    final res = await accountService.confirmBindRecoveryEmail(
      password: password,
      recoveryEmail: recoveryEmail,
      code: code,
    );
    if (res.ok && res.data != null) {
      _account = res.data;
      notifyListeners();
    }
    return res;
  }

  Future<AccountApiResult<Map<String, dynamic>>> startRemoveRecoveryEmailSendCode({
    required String password,
    required String confirmEmail,
  }) {
    return accountService.startRemoveRecoveryEmailSendCode(
      password: password,
      confirmEmail: confirmEmail,
    );
  }

  Future<AccountApiResult<AccountSessionData>> confirmRemoveRecoveryEmail({
    required String password,
    required String code,
  }) async {
    final res = await accountService.confirmRemoveRecoveryEmail(
      password: password,
      code: code,
    );
    if (res.ok && res.data != null) {
      _account = res.data;
      notifyListeners();
    }
    return res;
  }

  Future<AccountApiResult<Map<String, dynamic>>> startSetPasswordSendCode({
    required String confirmEmail,
  }) {
    return accountService.startSetPasswordSendCode(confirmEmail: confirmEmail);
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
    if (accountService.isLoggedIn && _identity.deviceId.isNotEmpty) {
      unawaited(accountService.renameDevice(_identity.deviceId, trimmed));
    }
  }

  Future<void> refreshAccountDevices({bool silent = false}) async {
    if (!accountService.isLoggedIn) return;
    if (_devicesFetchInFlight) {
      _pendingDevicesRefresh = true;
      return;
    }
    _devicesFetchInFlight = true;
    final showLoader = !silent || _accountDevices.isEmpty;
    if (showLoader && !_refreshingDevices) {
      _refreshingDevices = true;
      notifyListeners();
    }
    try {
      final res = await accountService.fetchDevices();
      if (res.ok && res.data != null) {
        _accountDevices = res.data!
            .where((d) => d.deviceId != _identity.deviceId)
            .toList();
      }
    } finally {
      _devicesFetchInFlight = false;
      _refreshingDevices = false;
      notifyListeners();
      if (_pendingDevicesRefresh) {
        _pendingDevicesRefresh = false;
        unawaited(refreshAccountDevices(silent: true));
      }
    }
  }

  Future<void> refreshQuickLaunchApps() async {
    if (!accountService.isLoggedIn || _refreshingQuickLaunch) return;
    _refreshingQuickLaunch = true;
    notifyListeners();
    try {
      final res = await accountService.fetchQuickLaunchApps();
      if (res.ok && res.data != null) {
        _quickLaunchApps = res.data!;
      }
    } finally {
      _refreshingQuickLaunch = false;
      notifyListeners();
    }
  }

  AccountDeviceDto? findAccountDevice(String deviceId) {
    final norm = DeviceIdentity.normalizeDeviceId(deviceId);
    for (final d in _accountDevices) {
      if (DeviceIdentity.normalizeDeviceId(d.deviceId) == norm) return d;
    }
    return null;
  }

  String _formatQuickLaunchError(int code, String reason) {
    switch (reason.toUpperCase()) {
      case 'DIRECTORY_NOT_FOUND':
      case 'PATH_NOT_FOUND':
        return '目标计算机上不存在该目录，请检查路径是否正确';
      case 'BINARY_NOT_FOUND':
      case 'NO_EXECUTABLE_IN_DIR':
      case 'NOT_VALID_EXE':
      case 'INVALID_EXECUTABLE':
        return '目标目录中未找到有效的可执行二进制文件';
      case 'PROCESS_EXITED_EARLY':
        return '程序启动后已提前退出，未检测到前台窗口';
      case 'FOREGROUND_TIMEOUT':
      case 'LAUNCH_TIMEOUT':
        return '等待程序窗口抵达前台超时，请检查目标计算机状态';
      case 'HOST_BUSY':
        return '目标计算机当前正在进行其他远程会话，请稍后再试';
      case 'ACCESS_DENIED':
        return '无权访问目标计算机上的该目录';
    }
    switch (code) {
      case OsmiumErrorCode.deviceOffline:
        return '目标计算机当前离线，无法校验并启动程序';
      case OsmiumErrorCode.forbidden:
        return '目标设备不属于当前登录账户，无法操作';
      case OsmiumErrorCode.quickLaunchDisabled:
        return '目标计算机未开启快速启动功能';
      case OsmiumErrorCode.remoteDisabled:
        return '目标计算机未允许远程控制';
      case OsmiumErrorCode.quickLaunchNotFound:
        return '目标目录中未找到有效的可执行二进制文件';
      case OsmiumErrorCode.selfConnect:
        return '无法对当前设备发起远程控制';
      case OsmiumErrorCode.accountLocked:
        return '账号已锁定，请先解锁';
      case OsmiumErrorCode.accountAuthRequired:
        return '请先登录同一账户后再使用快速启动';
      case OsmiumErrorCode.rateLimited:
        return '操作过于频繁，请稍后再试';
    }
    return '启动失败，请检查网络与目标计算机状态后重试';
  }

  Future<({bool ok, String message, String deviceName, bool cancelled})>
  launchQuickApp(
    QuickLaunchAppDto app, {
    required void Function(String stage) onStage,
    required ValueNotifier<bool> cancelled,
  }) async {
    if (!accountService.isLoggedIn) {
      return (
        ok: false,
        message: '请先登录同一账户后再使用快速启动',
        deviceName: app.deviceName,
        cancelled: false,
      );
    }
    if (_signalingState != SignalingConnectionState.connected) {
      return (
        ok: false,
        message: '尚未连接到信令服务器，请稍候重试',
        deviceName: app.deviceName,
        cancelled: false,
      );
    }

    final targetId = DeviceIdentity.normalizeDeviceId(app.deviceId);
    final peer = findAccountDevice(targetId);
    final displayName = peer?.displayName ?? app.deviceName;
    if (peer == null) {
      return (
        ok: false,
        message: '目标设备不属于当前登录账户，无法操作',
        deviceName: displayName,
        cancelled: false,
      );
    }
    if (!peer.isOnline) {
      return (
        ok: false,
        message: '目标计算机当前离线，无法校验并启动程序',
        deviceName: displayName,
        cancelled: false,
      );
    }
    if (!peer.allowQuickLaunch) {
      return (
        ok: false,
        message: '目标计算机未开启快速启动功能',
        deviceName: displayName,
        cancelled: false,
      );
    }
    if (!peer.allowRemote) {
      return (
        ok: false,
        message: '目标计算机未允许远程控制',
        deviceName: displayName,
        cancelled: false,
      );
    }
    if (peer.isBeingControlled) {
      return (
        ok: false,
        message: '目标计算机当前正在进行其他远程会话，请稍后再试',
        deviceName: displayName,
        cancelled: false,
      );
    }

    onStage('正在校验目标设备在线状态、权限与程序目录…');

    final rnd = Random.secure();
    final reqId =
        'ql_${DateTime.now().millisecondsSinceEpoch}_${rnd.nextInt(1 << 30).toRadixString(16)}';

    final completer = Completer<QuickLaunchStartResponseEvent?>();
    late final StreamSubscription<QuickLaunchStartResponseEvent> sub;
    sub = signaling.quickLaunchStartResponseStream.listen((evt) {
      if (evt.requestId != reqId) return;
      if (evt.status.toLowerCase() == 'launching') {
        onStage('校验通过，正在启动程序并等待窗口抵达前台…');
        return;
      }
      if (!completer.isCompleted) {
        completer.complete(evt);
      }
    });

    void onCancelChanged() {
      if (cancelled.value) {
        signaling.sendQuickLaunchCancel(
          requestId: reqId,
          targetDeviceId: targetId,
        );
        if (!completer.isCompleted) {
          completer.complete(null);
        }
      }
    }

    cancelled.addListener(onCancelChanged);
    final timer = Timer(const Duration(seconds: 42), () {
      if (!completer.isCompleted) {
        signaling.sendQuickLaunchCancel(
          requestId: reqId,
          targetDeviceId: targetId,
        );
        completer.complete(
          const QuickLaunchStartResponseEvent(
            requestId: '',
            hostDeviceId: '',
            status: 'failed',
            ok: false,
            code: 0,
            reason: 'LAUNCH_TIMEOUT',
          ),
        );
      }
    });

    try {
      final sent = signaling.sendQuickLaunchStart(
        requestId: reqId,
        targetDeviceId: targetId,
        appId: app.id,
        exePath: app.exePath,
        appName: app.name,
      );
      if (!sent) {
        return (
          ok: false,
          message: '信令连接不可用，请稍后重试',
          deviceName: displayName,
          cancelled: false,
        );
      }

      final evt = await completer.future;
      if (cancelled.value || evt == null) {
        return (
          ok: false,
          message: '',
          deviceName: displayName,
          cancelled: true,
        );
      }
      if (evt.ok && evt.status.toLowerCase() == 'ready') {
        onStage('程序窗口已抵达前台，正在启动远程控制…');
        return (
          ok: true,
          message: '',
          deviceName: displayName,
          cancelled: false,
        );
      }
      return (
        ok: false,
        message: _formatQuickLaunchError(evt.code, evt.reason),
        deviceName: displayName,
        cancelled: false,
      );
    } finally {
      timer.cancel();
      cancelled.removeListener(onCancelChanged);
      await sub.cancel();
    }
  }

  Future<bool> renameRemoteDevice(String deviceId, String customName) async {
    final res = await accountService.renameDevice(deviceId, customName);
    if (res.ok) {
      if (res.data != null) {
        _accountDevices = res.data!
            .where((d) => d.deviceId != _identity.deviceId)
            .toList();
        notifyListeners();
      } else {
        await refreshAccountDevices();
      }
    }
    return res.ok;
  }

  Future<bool> removeRemoteDevice(String deviceId) async {
    final res = await accountService.removeDevice(deviceId);
    if (res.ok) {
      final normalized = DeviceIdentity.normalizeDeviceId(deviceId);
      if (res.data != null) {
        _accountDevices = res.data!
            .where((d) => d.deviceId != _identity.deviceId)
            .toList();
      } else {
        _accountDevices = _accountDevices
            .where((d) => DeviceIdentity.normalizeDeviceId(d.deviceId) != normalized)
            .toList();
      }
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
    _quickLaunchApps = const [];
    _oauthBindings = null;
    _oauthBindingsLoading = false;
    _oauthBindingsFailed = false;
    notifyListeners();
  }

  Future<void> _adoptAssignedDeviceIdentity(
    String newDeviceId,
    String? newDeviceSecret,
  ) async {
    final normalized = DeviceIdentity.normalizeDeviceId(newDeviceId);
    if (normalized.length < 6 || normalized.length > 12) return;
    final secretChanged =
        newDeviceSecret != null &&
        newDeviceSecret.trim().length >= 16 &&
        newDeviceSecret.trim() != _identity.deviceSecret;
    if (normalized == _identity.deviceId && !secretChanged) return;

    await settingsRepo.saveAssignedDeviceIdentity(
      deviceId: normalized,
      deviceSecret: secretChanged ? newDeviceSecret.trim() : null,
    );
    _identity = _identity.copyWith(
      deviceId: normalized,
      deviceSecret: secretChanged ? newDeviceSecret.trim() : null,
    );
    signaling.updateDeviceIdentity(
      deviceId: _identity.deviceId,
      deviceSecret: _identity.deviceSecret,
    );
    notifyListeners();
  }

  @override
  void dispose() {
    _ltokenRefreshTimer?.cancel();
    _foregroundPresenceTimer?.cancel();
    _sigStateSub?.cancel();
    _sigAccountAuthSub?.cancel();
    _sigDevicesSub?.cancel();
    _sigUnreachableSub?.cancel();
    _sigQuickLaunchSyncSub?.cancel();
    _sigDeviceIdSub?.cancel();
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
