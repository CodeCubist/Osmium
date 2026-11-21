using Osmium.Core.Interop;
using Osmium.Core.Models;
using Osmium.Core.Network;

namespace Osmium.Core.Services;

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

    public EngineService()
    {
        SettingsService = new SettingsService();
        Logging = new LoggingService(SettingsService.DataDirectory);
        Signaling = new SignalingClient(Logging);
        AccountService = new AccountService(SettingsService, Logging);
        Coordinator = new P2PCoordinator(SettingsService, Signaling, AccountService, Logging);

        Signaling.AccountDevicesSyncReceived += devices =>
            AccountService.UpdateSameAccountDevicesFromPush(devices);

        Signaling.AccountRevokedReceived += _ =>
            AccountService.HandleRemoteRevoked();

        AccountService.SessionChanged += () =>
            _ = Signaling.UpdateAuthTokenAsync(AccountService.CurrentLtoken);

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

    public void ConnectSignaling()
    {
        var id = SettingsService.Identity;
        var s = SettingsService.Settings;
        Signaling.Start(OsmiumSettings.FixedSignalingServerUrl, id.DeviceId, id.DeviceName, s.AllowRemoteControl,
            AccountService.CurrentLtoken);
    }

    public Task SyncSignalingStatusAsync()
    {
        return Signaling.UpdateStatusAsync(
            SettingsService.Settings.AllowRemoteControl,
            SettingsService.Identity.DeviceName);
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