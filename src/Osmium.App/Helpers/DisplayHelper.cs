using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Osmium.App.Services;
using Osmium.Core.Interop;

namespace Osmium.App.Helpers;

public static partial class DisplayHelper
{
    private const uint QdcOnlyActivePaths = 2;
    private const int DisplayConfigDeviceInfoGetSourceName = 1;
    private const int DisplayConfigDeviceInfoGetTargetName = 2;

    [GeneratedRegex(@"(\\\\\.\\DISPLAY\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex GdiDeviceRegex();

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public ulong RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DisplayConfigModeInfoRaw
    {
        private long _alignment;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfoRaw[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);

    public static IReadOnlyDictionary<string, string> GetFriendlyMonitorNamesByGdiDevice()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != 0 ||
                pathCount == 0 || modeCount == 0)
            {
                return map;
            }

            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfoRaw[modeCount];
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            {
                return map;
            }

            for (int i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                var srcReq = new DisplayConfigSourceDeviceName
                {
                    Header = new DisplayConfigDeviceInfoHeader
                    {
                        Type = DisplayConfigDeviceInfoGetSourceName,
                        Size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                        AdapterId = path.SourceInfo.AdapterId,
                        Id = path.SourceInfo.Id
                    }
                };
                if (DisplayConfigGetDeviceInfo(ref srcReq) != 0 || string.IsNullOrWhiteSpace(srcReq.ViewGdiDeviceName))
                {
                    continue;
                }

                var tgtReq = new DisplayConfigTargetDeviceName
                {
                    Header = new DisplayConfigDeviceInfoHeader
                    {
                        Type = DisplayConfigDeviceInfoGetTargetName,
                        Size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                        AdapterId = path.TargetInfo.AdapterId,
                        Id = path.TargetInfo.Id
                    }
                };
                if (DisplayConfigGetDeviceInfo(ref tgtReq) != 0 ||
                    string.IsNullOrWhiteSpace(tgtReq.MonitorFriendlyDeviceName))
                {
                    continue;
                }

                string friendly = tgtReq.MonitorFriendlyDeviceName.Trim();
                if (friendly.Contains("Generic", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                map[srcReq.ViewGdiDeviceName.Trim()] = friendly;
            }
        }
        catch
        {
        }

        return map;
    }

    public static string FormatDisplayOption(
        OsmiumDisplayInfo display,
        IReadOnlyDictionary<string, string> friendlyMap)
    {
        string? friendlyName = null;
        if (!string.IsNullOrWhiteSpace(display.Name))
        {
            var m = GdiDeviceRegex().Match(display.Name);
            if (m.Success)
            {
                friendlyMap.TryGetValue(m.Groups[1].Value, out friendlyName);
            }
            else if (friendlyMap.TryGetValue(display.Name.Trim(), out var direct))
            {
                friendlyName = direct;
            }
        }

        string primarySuffix = display.IsPrimary != 0
            ? LocalizationService.GetString("Host_PrimaryTag")
            : string.Empty;

        int number = display.Index + 1;
        if (!string.IsNullOrWhiteSpace(friendlyName))
        {
            return LocalizationService.Format(
                "Host_DisplayItemNamedFmt",
                number,
                friendlyName,
                display.Width,
                display.Height,
                display.RefreshRate,
                primarySuffix);
        }

        return LocalizationService.Format(
            "Host_DisplayItemSimpleFmt",
            number,
            display.Width,
            display.Height,
            display.RefreshRate,
            primarySuffix);
    }
}