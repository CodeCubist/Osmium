using Osmium.Core.Interop;
using Osmium.Core.Models;
using Osmium.Core.Network;

namespace Osmium.Core.Services;

public sealed class RemotePowerActionOutcome
{
    public bool Ok
    {
        get;
        init;
    }

    public int Code
    {
        get;
        init;
    }

    public string Action
    {
        get;
        init;
    } = string.Empty;

    public bool AlreadyOnline
    {
        get;
        init;
    }

    public int DelaySeconds
    {
        get;
        init;
    }

    public int RelayedCount
    {
        get;
        init;
    }

    public int SameSubnetRelayedCount
    {
        get;
        init;
    }

    public int LocalPacketsSent
    {
        get;
        init;
    }

    public string ErrorMessage
    {
        get;
        init;
    } = string.Empty;
}

public sealed class EngineService : IDisposable
{
    private readonly OsmiumLogDelegate _nativeLogDelegate;
    private bool _initialized;
    private int _disposed;

    public SettingsService SettingsService
    {
        get;
    }

    public LoggingService Logging
    {
        get;
    }

    public SignalingClient Signaling
    {
        get;
    }

    public AccountService AccountService
    {
        get;
    }

    public P2PCoordinator Coordinator
    {
        get;
    }

    public DeviceWallpaperStore DeviceWallpapers
    {
        get;
    }

    public RemotePowerService RemotePower
    {
        get;
    }

    public QuickLaunchHostService QuickLaunch
    {
        get;
    }

    public OsmiumCapabilities Capabilities
    {
        get;
        private set;
    }

    public IReadOnlyList<OsmiumDisplayInfo> Displays
    {
        get;
        private set;
    } = [];

    public EngineService(string? overrideDataDirectory = null, bool isServiceHostMode = false)
    {
        SettingsService = new SettingsService(overrideDataDirectory, isServiceHostMode);
        Logging = new LoggingService(SettingsService.DataDirectory);
        Signaling = new SignalingClient(Logging);
        AccountService = new AccountService(SettingsService, Logging);
        Coordinator = new P2PCoordinator(SettingsService, Signaling, AccountService, Logging);
        DeviceWallpapers = new DeviceWallpaperStore(SettingsService.DataDirectory, Logging);
        RemotePower = new RemotePowerService(SettingsService, AccountService, Coordinator, Logging);
        QuickLaunch = new QuickLaunchHostService(SettingsService, AccountService, Signaling, Coordinator, Logging);

        AccountService.DeviceRemoved += deviceId => DeviceWallpapers.RemoveWallpaper(deviceId);

        Signaling.AccountDevicesSyncReceived += devices =>
            AccountService.UpdateSameAccountDevicesFromPush(devices);

        Signaling.AccountRevokedReceived += _ =>
            AccountService.HandleRemoteRevoked();

        Signaling.DeviceIdReassigned += (newDeviceId, newDeviceSecret) =>
            SettingsService.AdoptAssignedDeviceId(newDeviceId, newDeviceSecret);

        SettingsService.DeviceIdChanged += newDeviceId =>
            Signaling.UpdateDeviceIdentity(newDeviceId, SettingsService.Identity.DeviceSecret);

        Signaling.PowerCommandReceived += RemotePower.HandlePowerCommand;
        Signaling.PowerWolRelayReceived += RemotePower.HandleWolRelay;
        Signaling.NetworkAddressUpdated += () => _ = SyncSignalingStatusAsync();

        AccountService.SessionChanged += () =>
        {
            _ = Signaling.UpdateAuthTokenAsync(AccountService.CurrentLtoken);
            _ = SyncSignalingStatusAsync();
        };

        Signaling.Registered += () =>
        {
            if (AccountService.IsLoggedIn)
            {
                if (!AccountService.HasLtoken)
                {
                    _ = AccountService.InitializeOnStartupAsync();
                }
                else
                {
                    _ = AccountService.RefreshDevicesAsync();
                }
            }
        };

        _nativeLogDelegate = message => Logging.RawNative(message);
    }

    public bool Initialize()
    {
        if (_initialized) return true;

        try
        {
            string dbPath = Path.Combine(AppContext.BaseDirectory, "gamecontrollerdb.txt");
            if (!File.Exists(dbPath)) dbPath = string.Empty;

            if (NativeMethods.Osmium_Init(dbPath, _nativeLogDelegate) == 0)
            {
                Logging.Error("Failed to initialize OsmiumEngine.dll");
                return false;
            }

            _initialized = true;
            RefreshDisplays();
            _ = Task.Run(ProbeCapabilitiesSafe);

            ConnectSignaling();

            return true;
        }
        catch (Exception ex)
        {
            Logging.Error($"Native engine initialization failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void RefreshDisplays()
    {
        if (!_initialized) return;

        try
        {
            var displays = new OsmiumDisplayInfo[16];
            int count = NativeMethods.Osmium_EnumDisplays(displays, displays.Length);
            Displays = displays.Take(count).ToArray();
        }
        catch (Exception ex)
        {
            Logging.Warn($"Failed to enumerate displays: {ex.Message}");
        }
    }

    private void ProbeCapabilitiesSafe()
    {
        if (!_initialized) return;

        try
        {
            if (NativeMethods.Osmium_ProbeCapabilities(out var caps) != 0)
            {
                Capabilities = caps;
                Logging.Info(
                    $"Capabilities: HWDecode={caps.HasHardwareDecoder != 0} ({caps.PreferredDecoderName}), HWEncode={caps.HasHardwareEncoder != 0} ({caps.PreferredEncoderName})");
            }
        }
        catch (Exception ex)
        {
            Logging.Warn($"Hardware capability probe failed: {ex.Message}");
        }
    }

    public void RefreshHardwareInfo()
    {
        if (!_initialized) return;

        RefreshDisplays();
        ProbeCapabilitiesSafe();
    }

    public LocalPowerProfile GetLocalPowerProfile()
    {
        bool allowRemotePower = SettingsService.Settings.HostAllowRemotePower &&
                                (!AccountService.IsLoggedIn || AccountService.IsProfileComplete);
        return WakeOnLanService.CollectLocalPowerProfile(allowRemotePower);
    }

    public void ConnectSignaling()
    {
        var id = SettingsService.Identity;
        var powerProfile = GetLocalPowerProfile();
        Signaling.Start(OsmiumSettings.FixedSignalingServerUrl, id.DeviceId, id.DeviceName,
            Coordinator.EffectiveAllowRemoteControl,
            AccountService.CurrentLtoken,
            powerProfile,
            SettingsService.Settings.HostAllowQuickLaunch,
            id.DeviceSecret);
    }

    public Task SyncSignalingStatusAsync()
    {
        var powerProfile = GetLocalPowerProfile();
        return Signaling.UpdateStatusAsync(
            Coordinator.EffectiveAllowRemoteControl,
            SettingsService.Identity.DeviceName,
            powerProfile,
            SettingsService.Settings.HostAllowQuickLaunch);
    }

    public async Task<RemotePowerActionOutcome> RequestRemotePowerAsync(
        AccountDeviceDto target,
        string action,
        CancellationToken ct = default)
    {
        string normAction = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (!AccountService.IsLoggedIn)
        {
            return new RemotePowerActionOutcome
            {
                Ok = false,
                Code = OsmiumErrorCode.AccountAuthRequired,
                Action = normAction,
                ErrorMessage = AccountService.FormatFriendlyError(OsmiumErrorCode.AccountAuthRequired, 401)
            };
        }

        if (!AccountService.IsProfileComplete)
        {
            return new RemotePowerActionOutcome
            {
                Ok = false,
                Code = OsmiumErrorCode.AccountProfileIncomplete,
                Action = normAction,
                ErrorMessage = AccountService.FormatFriendlyError(OsmiumErrorCode.AccountProfileIncomplete, 403)
            };
        }

        var apiRes = await AccountService.SendDevicePowerCommandAsync(target.DeviceId, normAction);
        if (!apiRes.Ok || apiRes.Data == null)
        {
            return new RemotePowerActionOutcome
            {
                Ok = false,
                Code = apiRes.Code,
                Action = normAction,
                ErrorMessage = AccountService.FormatFriendlyError(apiRes.Code, apiRes.HttpStatusCode)
            };
        }

        int localPacketsSent = 0;
        if (normAction == "wake" && !apiRes.Data.AlreadyOnline)
        {
            var macs = apiRes.Data.WolMacs.Count > 0 ? apiRes.Data.WolMacs : target.WolMacs;
            var broadcasts = apiRes.Data.WolBroadcasts.Count > 0 ? apiRes.Data.WolBroadcasts : target.WolBroadcasts;
            if (macs.Count > 0)
            {
                try
                {
                    var report = await WakeOnLanService.SendMagicPacketsAsync(
                        macs,
                        broadcasts,
                        [9, 7],
                        repeatCount: 3,
                        ct: ct);
                    localPacketsSent = report.PacketsSent;
                }
                catch (Exception ex)
                {
                    Logging.Warn($"Local Wake-on-LAN broadcast failed for {target.DeviceId}: {ex.Message}");
                }
            }
        }

        return new RemotePowerActionOutcome
        {
            Ok = true,
            Code = OsmiumErrorCode.Ok,
            Action = normAction,
            AlreadyOnline = apiRes.Data.AlreadyOnline,
            DelaySeconds = apiRes.Data.DelaySeconds,
            RelayedCount = apiRes.Data.RelayedCount,
            SameSubnetRelayedCount = apiRes.Data.SameSubnetRelayedCount,
            LocalPacketsSent = localPacketsSent
        };
    }

    public IReadOnlyList<OsmiumGamepadInfo> GetConnectedGamepads()
    {
        if (!_initialized) return [];
        var list = new OsmiumGamepadInfo[16];
        int count = NativeMethods.Osmium_GetConnectedGamepads(list, list.Length);
        return list.Take(count).ToArray();
    }

    public bool PollGamepadState(int joystickIndex, out OsmiumGamepadLiveState state)
    {
        if (!_initialized)
        {
            state = default;
            return false;
        }

        return NativeMethods.Osmium_PollGamepadState(joystickIndex, out state) != 0;
    }

    public bool AddGamepadMapping(string mapping)
    {
        if (!_initialized || string.IsNullOrWhiteSpace(mapping)) return false;
        return NativeMethods.Osmium_AddGamepadMapping(mapping) != 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        QuickLaunch.Dispose();
        Coordinator.Dispose();
        Signaling.Dispose();
        AccountService.Dispose();
        if (_initialized)
        {
            NativeMethods.Osmium_Shutdown();
            _initialized = false;
        }

        Logging.Dispose();
    }
}