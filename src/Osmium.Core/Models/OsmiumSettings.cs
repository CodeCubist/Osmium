using System.Text.Json.Serialization;

namespace Osmium.Core.Models;

public enum VideoCodecPreference
{
    Auto = 0,
    H264 = 1,
    Hevc = 2,
    Av1 = 3
}

public enum HardwarePreference
{
    Auto = 0,
    ForceHardware = 1,
    ForceSoftware = 2
}

public enum StreamWindowMode
{
    Fullscreen = 0,
    BorderlessWindowed = 1,
    Windowed = 2
}

public enum CaptureSysKeysMode
{
    Off = 0,
    FullscreenOnly = 1,
    Always = 2
}

public enum AudioChannelConfig
{
    Stereo = 0,
    Surround51 = 1,
    Surround71 = 2
}

public enum UiWindowDisplayMode
{
    Windowed = 0,
    Maximized = 1,
    Fullscreen = 2
}

public enum AppThemePreference
{
    Light = 0,
    Dark = 1,
    System = 2
}

public enum OsdDetailLevel
{
    Compact = 0,
    Standard = 1,
    Detailed = 2
}

public enum RemoteMouseMode
{
    ControllerMouse = 0,
    VirtualMouse = 1,
    SmartMode = 2
}

public sealed class RecentSessionRecord
{
    public string DeviceId
    {
        get;
        set;
    } = string.Empty;

    public string Alias
    {
        get;
        set;
    } = string.Empty;

    public DateTimeOffset LastConnectedAt
    {
        get;
        set;
    } = DateTimeOffset.UtcNow;

    public string FormattedId => DeviceIdentity.FormatDeviceId(DeviceId);
}

public sealed class OsmiumSettings
{
    public const string FixedSignalingServerUrl = "wss://osmium.fufu.skin";

    [JsonIgnore] public string SignalingServerUrl => FixedSignalingServerUrl;

    public int StunPortOverride
    {
        get;
        set;
    } = 0;

    [JsonIgnore] public bool AutoConnectSignaling => true;

    public bool EnableEncryption
    {
        get;
        set;
    } = true;

    public bool AllowRemoteControl
    {
        get;
        set;
    } = true;

    public bool RequireApprovalDialog
    {
        get;
        set;
    } = false;

    public int HostDisplayIndex
    {
        get;
        set;
    } = 0;

    public int HostMaxWidth
    {
        get;
        set;
    } = 0;

    public int HostMaxHeight
    {
        get;
        set;
    } = 0;

    public int HostMaxFps
    {
        get;
        set;
    } = 120;

    public int HostMaxBitrateKbps
    {
        get;
        set;
    } = 60000;

    public VideoCodecPreference HostCodec
    {
        get;
        set;
    } = VideoCodecPreference.Auto;

    public HardwarePreference HostEncoderMode
    {
        get;
        set;
    } = HardwarePreference.Auto;

    public bool HostCaptureAudio
    {
        get;
        set;
    } = true;

    public bool HostAllowInputControl
    {
        get;
        set;
    } = true;

    public bool HostCaptureCursor
    {
        get;
        set;
    } = true;

    public bool HostAllowRemoteDriverMgmt
    {
        get;
        set;
    } = true;

    public bool HostRequireDriverConfirm
    {
        get;
        set;
    } = false;

    public int SettingsSchemaVersion
    {
        get;
        set;
    } = 0;

    public int TargetWidth
    {
        get;
        set;
    } = 0;

    public int TargetHeight
    {
        get;
        set;
    } = 0;

    public int Fps
    {
        get;
        set;
    } = 60;

    public int BitrateKbps
    {
        get;
        set;
    } = 20000;

    public bool UnlockBitrate
    {
        get;
        set;
    } = false;

    public bool AutoAdjustBitrate
    {
        get;
        set;
    } = true;

    public int PacketSize
    {
        get;
        set;
    } = 1240;

    public StreamWindowMode WindowMode
    {
        get;
        set;
    } = StreamWindowMode.Windowed;

    public VideoCodecPreference VideoCodec
    {
        get;
        set;
    } = VideoCodecPreference.Auto;

    public HardwarePreference VideoDecoderMode
    {
        get;
        set;
    } = HardwarePreference.Auto;

    public bool EnableVsync
    {
        get;
        set;
    } = true;

    public bool EnableFramePacing
    {
        get;
        set;
    } = false;

    public bool EnableHdr
    {
        get;
        set;
    } = false;

    public bool EnableYuv444
    {
        get;
        set;
    } = false;

    public bool ShowPerformanceOverlay
    {
        get;
        set;
    } = false;

    public OsdDetailLevel OsdDetailLevel
    {
        get;
        set;
    } = OsdDetailLevel.Standard;

    public int ClientScalePercent
    {
        get;
        set;
    } = 100;

    public bool ShowConnectionWarnings
    {
        get;
        set;
    } = true;

    public bool ShowConfigurationWarnings
    {
        get;
        set;
    } = true;

    public AudioChannelConfig AudioChannels
    {
        get;
        set;
    } = AudioChannelConfig.Stereo;

    public bool MuteHostSpeakers
    {
        get;
        set;
    } = false;

    public bool MuteOnFocusLoss
    {
        get;
        set;
    } = false;

    public string PreferredLocalMicDeviceId
    {
        get;
        set;
    } = string.Empty;

    public bool AbsoluteMouseMode
    {
        get;
        set;
    } = true;

    public RemoteMouseMode PreferredMouseMode
    {
        get;
        set;
    } = RemoteMouseMode.ControllerMouse;

    public bool AbsoluteTouchMode
    {
        get;
        set;
    } = true;

    public bool SwapMouseButtons
    {
        get;
        set;
    } = false;

    public bool ReverseScrollDirection
    {
        get;
        set;
    } = false;

    public CaptureSysKeysMode CaptureSysKeys
    {
        get;
        set;
    } = CaptureSysKeysMode.FullscreenOnly;

    public bool MultiController
    {
        get;
        set;
    } = true;

    public bool SwapFaceButtons
    {
        get;
        set;
    } = false;

    public bool GamepadMouse
    {
        get;
        set;
    } = true;

    public bool BackgroundGamepad
    {
        get;
        set;
    } = false;

    public UiWindowDisplayMode UiDisplayMode
    {
        get;
        set;
    } = UiWindowDisplayMode.Windowed;

    public AppThemePreference UiTheme
    {
        get;
        set;
    } = AppThemePreference.Light;

    public bool KeepAwake
    {
        get;
        set;
    } = true;

    public bool CloseToTray
    {
        get;
        set;
    } = false;

    public bool HideToTrayOnMinimize
    {
        get;
        set;
    } = false;

    public bool ShowSameAccountDevicesInSidebar
    {
        get;
        set;
    } = true;

    public HashSet<string> HiddenSidebarDeviceIds
    {
        get;
        set;
    } = new(StringComparer.Ordinal);

    public string Language
    {
        get;
        set;
    } = "auto";

    public List<RecentSessionRecord> RecentSessions
    {
        get;
        set;
    } = [];

    public static int GetDefaultBitrateKbps(int width, int height, int fps, bool yuv444 = false)
    {
        int effW = width > 0 ? width : 1920;
        int effH = height > 0 ? height : 1080;
        double fpsFactor = (fps <= 60 ? fps : Math.Sqrt(fps / 60.0) * 60.0) / 30.0;
        long pixels = (long)Math.Max(1, effW) * Math.Max(1, effH);
        (long px, double factor)[] table =
        [
            (640L * 360L, 1.0),
            (854L * 480L, 2.0),
            (1280L * 720L, 5.0),
            (1920L * 1080L, 10.0),
            (2560L * 1440L, 20.0),
            (3840L * 2160L, 40.0),
            (5120L * 2880L, 65.0),
            (7680L * 4320L, 120.0)
        ];

        double resFactor = table[^1].factor;
        for (int i = 0; i < table.Length; i++)
        {
            if (pixels == table[i].px)
            {
                resFactor = table[i].factor;
                break;
            }

            if (pixels < table[i].px)
            {
                if (i == 0)
                {
                    resFactor = table[0].factor;
                }
                else
                {
                    double t = (double)(pixels - table[i - 1].px) / (table[i].px - table[i - 1].px);
                    resFactor = table[i - 1].factor + t * (table[i].factor - table[i - 1].factor);
                }

                break;
            }
        }

        if (yuv444) resFactor *= 2.0;

        return Math.Clamp((int)Math.Round(resFactor * fpsFactor) * 1000, 1000, 500000);
    }
}