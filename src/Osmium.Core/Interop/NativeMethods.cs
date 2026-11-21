using System.Runtime.InteropServices;

namespace Osmium.Core.Interop;

[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
public struct OsmiumCapabilities
{
    public int HasHardwareDecoder;
    public int HasHardwareEncoder;
    public int DecoderSupportsH264;
    public int DecoderSupportsHevc;
    public int DecoderSupportsAv1;
    public int EncoderSupportsH264;
    public int EncoderSupportsHevc;
    public int EncoderSupportsAv1;
    public int SupportsHdr;
    public int MaxWidth;
    public int MaxHeight;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string PreferredDecoderName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string PreferredEncoderName;
}

internal static unsafe class Utf8FixedHelper
{
    public static string Decode(byte* ptr, int maxBytes)
    {
        if (ptr == null || maxBytes <= 0) return string.Empty;
        int len = 0;
        while (len < maxBytes && ptr[len] != 0) len++;
        return len == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(ptr, len);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct OsmiumDisplayInfo
{
    public int Index;
    public int Width;
    public int Height;
    public int RefreshRate;
    public int Left;
    public int Top;
    public int IsPrimary;
    private fixed byte _name[128];
    private fixed byte _deviceName[128];

    public string Name
    {
        get
        {
            fixed (byte* p = _name)
                return Utf8FixedHelper.Decode(p, 128);
        }
    }

    public string DeviceName
    {
        get
        {
            fixed (byte* p = _deviceName)
                return Utf8FixedHelper.Decode(p, 128);
        }
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct OsmiumAudioDeviceInfo
{
    public int Index;
    public int IsDefault;
    private fixed byte _id[256];
    private fixed byte _name[256];

    public string Id
    {
        get
        {
            fixed (byte* p = _id)
                return Utf8FixedHelper.Decode(p, 256);
        }
    }

    public string Name
    {
        get
        {
            fixed (byte* p = _name)
                return Utf8FixedHelper.Decode(p, 256);
        }
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
public struct OsmiumGamepadInfo
{
    public int JoystickIndex;
    public int InstanceId;
    public int IsGameController;
    public int ButtonCount;
    public int AxisCount;
    public int HatCount;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string Name;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string Guid;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct OsmiumGamepadLiveState
{
    public int Connected;
    public int ButtonsMask;
    public fixed short Axes[8];
    public fixed byte Hats[4];
    public int LastPressedButton;
    public int LastMovedAxis;
    public int LastMovedAxisDirection;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct OsmiumClientStats
{
    public uint ReceivedFrames;
    public uint DecodedFrames;
    public uint RenderedFrames;
    public uint DroppedFrames;
    public double Fps;
    public double BitrateMbps;
    public double DecodeLatencyMs;
    public uint RttMs;
    public int VideoWidth;
    public int VideoHeight;
    public int NativeWidth;
    public int NativeHeight;
    public int TransportRoute;
    public int LocalPort;
    public double PacketLossPercent;
    public ulong ReceivedPackets;
    public ulong LostPackets;
    public int ActiveDisplayIndex;
    public int MouseMode;
    public int VirtualMouseEngaged;
    public int EnableHdr;
    public int EnableYuv444;
    private fixed byte _codecName[32];
    private fixed byte _decoderName[64];
    private fixed byte _peerEndpoint[64];
    private fixed byte _localEndpoint[64];

    public string CodecName
    {
        get
        {
            fixed (byte* p = _codecName)
                return Utf8FixedHelper.Decode(p, 32);
        }
    }

    public string DecoderName
    {
        get
        {
            fixed (byte* p = _decoderName)
                return Utf8FixedHelper.Decode(p, 64);
        }
    }

    public string PeerEndpoint
    {
        get
        {
            fixed (byte* p = _peerEndpoint)
                return Utf8FixedHelper.Decode(p, 64);
        }
    }

    public string LocalEndpoint
    {
        get
        {
            fixed (byte* p = _localEndpoint)
                return Utf8FixedHelper.Decode(p, 64);
        }
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
public struct OsmiumHostStats
{
    public uint EncodedFrames;
    public uint DroppedFrames;
    public double Fps;
    public double BitrateMbps;
    public double EncodeLatencyMs;
    public uint RttMs;
    public int CaptureWidth;
    public int CaptureHeight;
    public int TransportRoute;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string CodecName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string EncoderName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string PeerEndpoint;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct OsmiumHostConfig
{
    public int DisplayIndex;
    public int MaxWidth;
    public int MaxHeight;
    public int MaxFps;
    public int MaxBitrateKbps;
    public int CodecPreference;
    public int EncoderPreference;
    public int CaptureAudio;
    public int AllowInputControl;
    public int CaptureCursor;
    public int AllowRemoteDriverMgmt;
    public int RequireLocalDriverConfirm;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct OsmiumClientConfig
{
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string? WindowTitle;
    public int TargetWidth;
    public int TargetHeight;
    public int Fps;
    public int BitrateKbps;
    public int WindowMode;
    public int CodecPreference;
    public int DecoderPreference;
    public int CaptureSysKeysMode;
    public int EnableVsync;
    public int EnableHdr;
    public int EnableYuv444;
    public int ShowPerformanceOverlay;
    public int AbsoluteMouseMode;
    public int MouseMode;
    public int AbsoluteTouchMode;
    public int SwapMouseButtons;
    public int ReverseScrollDirection;
    public int MultiController;
    public int SwapFaceButtons;
    public int GamepadMouse;
    public int BackgroundGamepad;
    public int MuteOnFocusLoss;
    public int KeepAwake;
    public int EnableVirtualSpeaker;
    public int EnableVirtualMic;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string? LocalMicDeviceId;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void OsmiumLogDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string message);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void OsmiumClientStartedDelegate(
    int width,
    int height,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string codecName,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string decoderName);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void OsmiumClientStatsDelegate(in OsmiumClientStats stats);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void OsmiumClientEndedDelegate(int reasonCode,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string reasonMessage);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void OsmiumClientStatusJsonDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string statusJson);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void OsmiumHostStatsDelegate(in OsmiumHostStats stats);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void OsmiumHostEndedDelegate(int reasonCode, [MarshalAs(UnmanagedType.LPUTF8Str)] string reasonMessage);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int OsmiumHostDriverConfirmDelegate(int actionType);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void OsmiumAuxDataDelegate(IntPtr data, int length);

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct OsmiumClientCallbacks
{
    public IntPtr OnStarted;
    public IntPtr OnStatsUpdated;
    public IntPtr OnEnded;
    public IntPtr OnLog;
    public IntPtr OnStatusJson;
    public IntPtr OnAuxData;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct OsmiumHostCallbacks
{
    public IntPtr OnStatsUpdated;
    public IntPtr OnEnded;
    public IntPtr OnLog;
    public IntPtr OnConfirmDriverAction;
    public IntPtr OnAuxData;
}

public static class NativeMethods
{
    private const string DllName = "OsmiumEngine.dll";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_Init(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? gameControllerDbPath,
        OsmiumLogDelegate? logCallback);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_Shutdown();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_ProbeCapabilities(out OsmiumCapabilities outCaps);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_EnumDisplays([Out] OsmiumDisplayInfo[] outDisplays, int maxCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_EnumMicDevices([Out] OsmiumAudioDeviceInfo[] outDevices, int maxCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_GetDriverStatus(
        out int outHasUsbIp,
        out int outIsElevated,
        out int outIsSystem);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_InstallVirtualDrivers();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_UninstallVirtualDrivers();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_RunSystemService();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_RunOneShotSasService();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_GetConnectedGamepads([Out] OsmiumGamepadInfo[] outGamepads, int maxCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_PollGamepadState(int joystickIndex, out OsmiumGamepadLiveState outState);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_AddGamepadMapping(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string mappingString);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int Osmium_CreateTransport(
        string? stunHost,
        ushort stunPort,
        out IntPtr outTransport,
        System.Text.StringBuilder outLocalCandidatesJson,
        int jsonBufLen);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_DestroyTransport(IntPtr transport);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int Osmium_ConnectPeerP2P(
        IntPtr transport,
        byte[] sessionToken16,
        int localRole,
        string peerCandidatesJson,
        int punchTimeoutMs,
        out int outTransportRoute,
        System.Text.StringBuilder outActiveEndpoint,
        int endpointBufLen);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_SetTransportKey(IntPtr transport, byte[] aesKey32);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_SetTransportEncryption(IntPtr transport, int enableEncryption);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_AuthenticateClient(
        IntPtr transport,
        byte[] authTag32,
        in OsmiumClientConfig requestedProfile,
        int timeoutMs);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_WaitClientAuthHost(
        IntPtr transport,
        byte[] expectedTempTag32,
        byte[] expectedFixedTag32,
        int hasFixedCode,
        int timeoutMs,
        out OsmiumClientConfig outNegotiatedProfile);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_StartHostSession(
        IntPtr transport,
        in OsmiumHostConfig config,
        in OsmiumClientConfig negotiatedClientProfile,
        in OsmiumHostCallbacks callbacks,
        out IntPtr outHostHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_StopHostSession(IntPtr hostHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_HostSendAuxData(IntPtr hostHandle, byte[] data, int length);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_StartClientEmbedded(
        IntPtr transport,
        IntPtr swapChainPanelIUnknown,
        int initialWidth,
        int initialHeight,
        float dpiScale,
        in OsmiumClientConfig config,
        in OsmiumClientCallbacks callbacks,
        out IntPtr outClientHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_StopClientEmbedded(IntPtr clientHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_ClientResize(IntPtr clientHandle, int width, int height, float dpiScale);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_ClientSwitchDisplay(IntPtr clientHandle, int displayIndex);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_ClientUpdateVideoParams(
        IntPtr clientHandle,
        int targetWidth,
        int targetHeight,
        int fps,
        int bitrateKbps,
        int enableHdr,
        int enableYuv444);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_ClientSetMouseMode(IntPtr clientHandle, int mouseMode, int hideHostCursor);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_ClientSetVirtualAudio(
        IntPtr clientHandle,
        int virtualSpeaker,
        int virtualMic,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? localMicDeviceId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_ClientSendSystemAction(IntPtr clientHandle, int action);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_ClientSendKey(IntPtr clientHandle, ushort vkCode, ushort scanCode, int isDown,
        int isExtended);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_ClientSendMouseMove(IntPtr clientHandle, int isAbsolute, float xOrDeltaX,
        float yOrDeltaY);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_ClientSendMouseButton(IntPtr clientHandle, byte button, int isDown);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_ClientSendMouseWheel(IntPtr clientHandle, short deltaX, short deltaY);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_ClientPollInput(IntPtr clientHandle, int windowFocused);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_ClientSetOverlayVisible(IntPtr clientHandle, int visible);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_ClientSetVsync(IntPtr clientHandle, int enableVsync);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_ClientSetMute(IntPtr clientHandle, int muted);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_ClientSendAuxData(IntPtr clientHandle, byte[] data, int length);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Osmium_RunClientSession(
        IntPtr transport,
        in OsmiumClientConfig config,
        in OsmiumClientCallbacks callbacks);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_StopClientSession();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Osmium_ToggleClientOverlay();
}