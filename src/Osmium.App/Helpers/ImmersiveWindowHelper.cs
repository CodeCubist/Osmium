using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Osmium.App.Helpers;

public static class ImmersiveWindowHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;

    private const int DwmwcpRound = 2;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);
    private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);

    private const int GwlpWndProc = -4;
    private const uint GwHwndNext = 2;
    private const uint GwChild = 5;
    private const uint WmSize = 0x0005;
    private const uint WmActivate = 0x0006;
    private const uint WmClose = 0x0010;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmShowWindow = 0x0018;
    private const uint WmWindowPosChanging = 0x0046;
    private const uint WmWindowPosChanged = 0x0047;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmNcCalcSize = 0x0083;
    private const uint WmSysCommand = 0x0112;
    private const uint WmParentNotify = 0x0210;
    private const uint ScMinimize = 0xF020;
    private const uint ScClose = 0xF060;
    private const uint MfByCommand = 0x00000000;
    private const uint MfGrayed = 0x00000001;
    private const uint MfDisabled = 0x00000002;
    private const int WmCreate = 0x0001;
    private const int WaInactive = 0;
    private const int WaActive = 1;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static readonly WndProcDelegate SubclassDelegate = SubclassWndProc;
    private static readonly IntPtr SubclassProcPtr = Marshal.GetFunctionPointerForDelegate(SubclassDelegate);
    private static readonly ConcurrentDictionary<IntPtr, IntPtr> PrevWndProcs = new();

    private static readonly WndProcDelegate BridgeSubclassDelegate = BridgeSubclassWndProc;

    private static readonly IntPtr BridgeSubclassProcPtr =
        Marshal.GetFunctionPointerForDelegate(BridgeSubclassDelegate);

    private static readonly ConcurrentDictionary<IntPtr, IntPtr> BridgePrevWndProcs = new();
    private static readonly ConcurrentDictionary<IntPtr, bool> WindowDarkModes = new();
    private static readonly ConcurrentDictionary<IntPtr, bool> CloseMinimizeBlockedWindows = new();

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bRevert);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy,
        uint uFlags);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    public static void ApplyImmersiveFrame(Window window, bool isDarkTheme, bool transparentBorder = true)
    {
        if (window == null) return;

        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;

            WindowDarkModes[hwnd] = isDarkTheme;
            EnsureTopInsetFixAttached(hwnd);
            NativeBridge.InvalidateRect(hwnd, IntPtr.Zero, true);
            SyncContentBridges(hwnd);

            ApplyFrameAttributes(hwnd, isDarkTheme, transparentBorder);

            if (hwnd == GetActiveWindow())
            {
                SendMessage(hwnd, WmActivate, (IntPtr)WaInactive, IntPtr.Zero);
                SendMessage(hwnd, WmActivate, (IntPtr)WaActive, IntPtr.Zero);
            }
            else
            {
                SendMessage(hwnd, WmActivate, (IntPtr)WaActive, IntPtr.Zero);
                SendMessage(hwnd, WmActivate, (IntPtr)WaInactive, IntPtr.Zero);
            }
        }
        catch
        {
        }
    }

    public static void ApplyDialogFrame(Window window, bool isDarkTheme, bool transparentBorder = true)
    {
        if (window == null) return;

        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;

            WindowDarkModes[hwnd] = isDarkTheme;
            ApplyFrameAttributes(hwnd, isDarkTheme, transparentBorder);
        }
        catch
        {
        }
    }

    private static void ApplyFrameAttributes(IntPtr hwnd, bool isDarkTheme, bool transparentBorder)
    {
        int darkMode = isDarkTheme ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

        int cornerPreference = DwmwcpRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));

        int borderColor = transparentBorder ? DwmColorNone : DwmColorDefault;
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(int));

        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        var titleBar = appWindow.TitleBar;
        if (titleBar != null && AppWindowTitleBar.IsCustomizationSupported())
        {
            titleBar.BackgroundColor = Colors.Transparent;
            titleBar.InactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            if (isDarkTheme)
            {
                titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 240, 244, 248);
                titleBar.ButtonHoverForegroundColor = Colors.White;
                titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(24, 255, 255, 255);
                titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(36, 255, 255, 255);
                titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(140, 240, 244, 248);
            }
            else
            {
                titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 32, 36, 44);
                titleBar.ButtonHoverForegroundColor = Colors.Black;
                titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(14, 0, 0, 0);
                titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(24, 0, 0, 0);
                titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(140, 32, 36, 44);
            }
        }
    }

    public static void EnsureTopInsetFixAttached(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero) return;

        EnsureTopInsetFixAttached(hwnd);
        SyncContentBridges(hwnd);
    }

    public static void SetCloseAndMinimizeBlocked(Window window, bool blocked)
    {
        if (window == null) return;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;
            EnsureTopInsetFixAttached(hwnd);
            CloseMinimizeBlockedWindows[hwnd] = blocked;
            if (blocked)
            {
                IntPtr hMenu = GetSystemMenu(hwnd, false);
                if (hMenu != IntPtr.Zero)
                {
                    EnableMenuItem(hMenu, ScClose, MfByCommand | MfGrayed | MfDisabled);
                    EnableMenuItem(hMenu, ScMinimize, MfByCommand | MfGrayed | MfDisabled);
                }
            }
            else
            {
                GetSystemMenu(hwnd, true);
            }
        }
        catch
        {
        }
    }

    public static void SyncContentBridges(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero) return;

        SyncContentBridges(hwnd);
    }

    public static void SyncContentBridges(IntPtr parentHwnd)
    {
        if (parentHwnd == IntPtr.Zero) return;

        bool isZoomed = IsZoomed(parentHwnd);
        NativeBridge.RECT parentRect = default;
        int parentW = 0;
        int parentH = 0;
        if (!isZoomed && NativeBridge.GetClientRect(parentHwnd, out parentRect))
        {
            parentW = parentRect.Right - parentRect.Left;
            parentH = parentRect.Bottom - parentRect.Top;
        }

        for (IntPtr child = NativeBridge.GetWindow(parentHwnd, GwChild);
             child != IntPtr.Zero;
             child = NativeBridge.GetWindow(child, GwHwndNext))
        {
            if (!IsContentBridgeWindow(child)) continue;

            TryAttachBridgeSubclass(child);

            if (isZoomed || parentW <= 0 || parentH <= 1) continue;
            if (!NativeBridge.GetWindowRect(child, out var childRect)) continue;

            var origin = new NativeBridge.POINT { X = childRect.Left, Y = childRect.Top };
            if (!NativeBridge.ScreenToClient(parentHwnd, ref origin)) continue;

            int childW = childRect.Right - childRect.Left;
            int childH = childRect.Bottom - childRect.Top;

            bool shiftedDown = origin.X == 0 && origin.Y >= 1 && origin.Y <= 4;
            bool shortHeight = origin.X == 0 && origin.Y == 0 && childW == parentW && childH < parentH &&
                               (parentH - childH) <= 4;
            if (shiftedDown || shortHeight)
            {
                SetWindowPos(child, IntPtr.Zero, 0, 0, parentW, parentH, SwpNoZOrder | SwpNoActivate);
            }
        }
    }

    private static bool IsContentBridgeWindow(IntPtr hwnd)
    {
        var buffer = new char[128];
        int length = NativeBridge.GetClassName(hwnd, buffer, buffer.Length);
        if (length <= 0) return false;

        string className = new string(buffer, 0, length);
        return className.Contains("DesktopChildSiteBridge", StringComparison.OrdinalIgnoreCase) ||
               className.Contains("ContentBridge", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryAttachBridgeSubclass(IntPtr bridgeHwnd)
    {
        if (bridgeHwnd == IntPtr.Zero || BridgePrevWndProcs.ContainsKey(bridgeHwnd)) return;

        try
        {
            IntPtr current = GetWindowLongPtr(bridgeHwnd, GwlpWndProc);
            if (current == BridgeSubclassProcPtr || current == IntPtr.Zero) return;

            IntPtr prev = SetWindowLongPtr(bridgeHwnd, GwlpWndProc, BridgeSubclassProcPtr);
            if (prev != IntPtr.Zero && prev != BridgeSubclassProcPtr)
            {
                BridgePrevWndProcs[bridgeHwnd] = prev;
            }
        }
        catch
        {
        }
    }

    private static bool TryGetClientOrigin(IntPtr childHwnd, IntPtr parentHwnd, out int clientX, out int clientY)
    {
        clientX = 0;
        clientY = 0;
        if (!NativeBridge.GetWindowRect(childHwnd, out var rect))
        {
            return false;
        }

        var origin = new NativeBridge.POINT { X = rect.Left, Y = rect.Top };
        if (!NativeBridge.ScreenToClient(parentHwnd, ref origin))
        {
            return false;
        }

        clientX = origin.X;
        clientY = origin.Y;
        return true;
    }

    private static IntPtr BridgeSubclassWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!BridgePrevWndProcs.TryGetValue(hwnd, out IntPtr prevProc) || prevProc == IntPtr.Zero)
        {
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        if (msg == WmNcDestroy)
        {
            BridgePrevWndProcs.TryRemove(hwnd, out _);
            SetWindowLongPtr(hwnd, GwlpWndProc, prevProc);
            return CallWindowProc(prevProc, hwnd, msg, wParam, lParam);
        }

        if (msg == WmWindowPosChanging && lParam != IntPtr.Zero)
        {
            IntPtr parentHwnd = NativeBridge.GetParent(hwnd);
            if (parentHwnd != IntPtr.Zero &&
                !IsZoomed(parentHwnd) &&
                NativeBridge.GetClientRect(parentHwnd, out var parentRect))
            {
                var wp = Marshal.PtrToStructure<NativeBridge.WINDOWPOS>(lParam);
                int parentW = parentRect.Right - parentRect.Left;
                int parentH = parentRect.Bottom - parentRect.Top;

                bool moving = (wp.flags & SwpNoMove) == 0;
                bool sizing = (wp.flags & SwpNoSize) == 0;

                if (moving && wp.x == 0 && wp.y >= 1 && wp.y <= 4)
                {
                    int inset = wp.y;
                    wp.y = 0;

                    if (sizing && parentH > inset && wp.cx == parentW && wp.cy == parentH - inset)
                    {
                        wp.cy = parentH;
                    }

                    Marshal.StructureToPtr(wp, lParam, false);
                }
                else if (sizing && parentH > 1 && wp.cx == parentW && wp.cy < parentH && (parentH - wp.cy) <= 4)
                {
                    if (TryGetClientOrigin(hwnd, parentHwnd, out int curX, out int curY) &&
                        curX == 0 && (curY == 0 || (curY >= 1 && curY <= 4)))
                    {
                        wp.cy = parentH;
                        Marshal.StructureToPtr(wp, lParam, false);
                    }
                }
            }
        }

        return CallWindowProc(prevProc, hwnd, msg, wParam, lParam);
    }

    private static class NativeBridge
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        [DllImport("user32.dll")]
        internal static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect,
            [MarshalAs(UnmanagedType.Bool)] bool bErase);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateSolidBrush(uint crColor);

        [DllImport("user32.dll")]
        internal static extern int FillRect(IntPtr hDC, [In] ref RECT lprc, IntPtr hbr);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr hObject);
    }

    public static void EnsureTopInsetFixAttached(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || PrevWndProcs.ContainsKey(hwnd)) return;

        IntPtr current = GetWindowLongPtr(hwnd, GwlpWndProc);
        if (current == SubclassProcPtr || current == IntPtr.Zero) return;

        IntPtr prev = SetWindowLongPtr(hwnd, GwlpWndProc, SubclassProcPtr);
        if (prev != IntPtr.Zero && prev != SubclassProcPtr)
        {
            PrevWndProcs[hwnd] = prev;
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            SyncContentBridges(hwnd);
        }
    }

    private static IntPtr SubclassWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!PrevWndProcs.TryGetValue(hwnd, out IntPtr prevProc) || prevProc == IntPtr.Zero)
        {
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        if (msg == WmNcDestroy)
        {
            PrevWndProcs.TryRemove(hwnd, out _);
            WindowDarkModes.TryRemove(hwnd, out _);
            CloseMinimizeBlockedWindows.TryRemove(hwnd, out _);
            SetWindowLongPtr(hwnd, GwlpWndProc, prevProc);
            return CallWindowProc(prevProc, hwnd, msg, wParam, lParam);
        }

        if (CloseMinimizeBlockedWindows.TryGetValue(hwnd, out bool isBlocked) && isBlocked)
        {
            if (msg == WmClose)
            {
                return IntPtr.Zero;
            }

            if (msg == WmSysCommand)
            {
                uint cmd = unchecked((uint)(wParam.ToInt64() & 0xFFF0L));
                if (cmd == ScClose || cmd == ScMinimize)
                {
                    return IntPtr.Zero;
                }
            }
        }

        if (msg == WmEraseBkgnd && wParam != IntPtr.Zero)
        {
            bool isDark = WindowDarkModes.TryGetValue(hwnd, out bool dark) && dark;
            if (NativeBridge.GetClientRect(hwnd, out var rect))
            {
                uint colorRef = isDark ? 0x00202020u : 0x00F3F3F3u;
                IntPtr brush = NativeBridge.CreateSolidBrush(colorRef);
                if (brush != IntPtr.Zero)
                {
                    NativeBridge.FillRect(wParam, ref rect, brush);
                    NativeBridge.DeleteObject(brush);
                    return (IntPtr)1;
                }
            }
        }

        if (msg == WmParentNotify &&
            (wParam.ToInt64() & 0xFFFFL) == WmCreate &&
            lParam != IntPtr.Zero &&
            IsContentBridgeWindow(lParam))
        {
            TryAttachBridgeSubclass(lParam);
        }

        if (msg == WmSize || msg == WmShowWindow || msg == WmWindowPosChanged)
        {
            IntPtr result = CallWindowProc(prevProc, hwnd, msg, wParam, lParam);
            SyncContentBridges(hwnd);
            return result;
        }

        if (msg == WmNcCalcSize && wParam != IntPtr.Zero && lParam != IntPtr.Zero)
        {
            int origTop = Marshal.ReadInt32(lParam, 4);
            IntPtr result = CallWindowProc(prevProc, hwnd, msg, wParam, lParam);
            int newTop = Marshal.ReadInt32(lParam, 4);

            if (!IsZoomed(hwnd) && newTop > origTop && (newTop - origTop) <= 4)
            {
                Marshal.WriteInt32(lParam, 4, origTop);
            }

            return result;
        }

        return CallWindowProc(prevProc, hwnd, msg, wParam, lParam);
    }
}