using System.Runtime.InteropServices;

namespace Osmium.App.Helpers;

internal sealed record TrayMenuSnapshot(
    string OpenLabel,
    string AutoStartLabel,
    bool AutoStartChecked,
    bool ShowDisconnect,
    string DisconnectLabel,
    string ExitLabel,
    string Tooltip);

internal sealed class TrayIconHelper : IDisposable
{
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const uint WmNull = 0x0000;
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const uint WmContextMenu = 0x007B;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmTrayCallback = 0x802A;

    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;
    private const int IdiApplication = 32512;

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint MfChecked = 0x00000008;
    private const uint MfUnchecked = 0x00000000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmNoNotify = 0x0080;

    private const uint CmdOpen = 1001;
    private const uint CmdAutoStart = 1002;
    private const uint CmdDisconnect = 1003;
    private const uint CmdExit = 1004;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly WndProcDelegate _wndProcDel;
    private readonly IntPtr _hInstance;
    private readonly string _className;
    private readonly uint _taskbarCreatedMsg;
    private readonly IntPtr _hIcon;
    private readonly bool _ownsIcon;

    private readonly Action _onRestore;
    private readonly Action _onToggleAutoStart;
    private readonly Action _onDisconnectHost;
    private readonly Action _onExit;
    private readonly Action _onSystemShutdown;
    private readonly Func<TrayMenuSnapshot> _getSnapshot;

    private IntPtr _hwnd;
    private bool _iconAdded;
    private bool _visible;
    private bool _menuOpen;
    private bool _disposed;

    public TrayIconHelper(
        string? iconFilePath,
        Action onRestore,
        Action onToggleAutoStart,
        Action onDisconnectHost,
        Action onExit,
        Action onSystemShutdown,
        Func<TrayMenuSnapshot> getSnapshot)
    {
        _onRestore = onRestore;
        _onToggleAutoStart = onToggleAutoStart;
        _onDisconnectHost = onDisconnectHost;
        _onExit = onExit;
        _onSystemShutdown = onSystemShutdown;
        _getSnapshot = getSnapshot;

        _hInstance = GetModuleHandle(null);
        _className = $"OsmiumTrayHost_{Environment.ProcessId}_{Guid.NewGuid():N}";
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
        _wndProcDel = TrayWndProc;

        int cx = Math.Max(16, GetSystemMetrics(SmCxSmIcon));
        int cy = Math.Max(16, GetSystemMetrics(SmCySmIcon));
        if (!string.IsNullOrEmpty(iconFilePath) && File.Exists(iconFilePath))
        {
            _hIcon = LoadImage(IntPtr.Zero, iconFilePath, ImageIcon, cx, cy, LrLoadFromFile);
            _ownsIcon = _hIcon != IntPtr.Zero;
        }

        if (_hIcon == IntPtr.Zero)
        {
            _hIcon = LoadIcon(IntPtr.Zero, (IntPtr)IdiApplication);
            _ownsIcon = false;
        }

        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDel),
            hInstance = _hInstance,
            lpszClassName = _className
        };
        RegisterClassEx(ref wc);

        _hwnd = CreateWindowEx(
            WsExToolWindow,
            _className,
            "OsmiumTrayHost",
            WsPopup,
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            _hInstance,
            IntPtr.Zero);
    }

    public void SetVisible(bool visible, string? tooltip = null)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        _visible = visible;

        if (visible)
        {
            string tip = string.IsNullOrWhiteSpace(tooltip) ? _getSnapshot().Tooltip : tooltip;
            AddOrModifyIcon(tip);
        }
        else if (_iconAdded)
        {
            var nid = CreateBaseNotifyIconData();
            ShellNotifyIcon(NimDelete, ref nid);
            _iconAdded = false;
        }
    }

    public void UpdateTooltip(string tooltip)
    {
        if (_disposed || !_visible || !_iconAdded || _hwnd == IntPtr.Zero) return;
        var nid = CreateBaseNotifyIconData();
        nid.uFlags = NifTip;
        nid.szTip = TruncateTip(tooltip);
        ShellNotifyIcon(NimModify, ref nid);
    }

    private void AddOrModifyIcon(string tooltip)
    {
        var nid = CreateBaseNotifyIconData();
        nid.uFlags = NifMessage | NifIcon | NifTip;
        nid.uCallbackMessage = WmTrayCallback;
        nid.hIcon = _hIcon;
        nid.szTip = TruncateTip(tooltip);

        if (!_iconAdded)
        {
            _iconAdded = ShellNotifyIcon(NimAdd, ref nid);
        }
        else
        {
            ShellNotifyIcon(NimModify, ref nid);
        }
    }

    private NotifyIconData CreateBaseNotifyIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private static string TruncateTip(string tip)
    {
        if (string.IsNullOrWhiteSpace(tip)) return "Osmium";
        return tip.Length <= 127 ? tip : tip[..127];
    }

    private IntPtr TrayWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!_disposed)
        {
            if (msg == WmTrayCallback)
            {
                uint trayEvent = unchecked((uint)(lParam.ToInt64() & 0xFFFF));
                if (trayEvent == WmLButtonUp || trayEvent == WmLButtonDblClk)
                {
                    _onRestore();
                }
                else if (trayEvent == WmRButtonUp || trayEvent == WmContextMenu)
                {
                    ShowContextMenu();
                }

                return IntPtr.Zero;
            }

            if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
            {
                _iconAdded = false;
                if (_visible)
                {
                    AddOrModifyIcon(_getSnapshot().Tooltip);
                }

                return IntPtr.Zero;
            }

            if (msg == WmQueryEndSession)
            {
                return new IntPtr(1);
            }

            if (msg == WmEndSession && wParam != IntPtr.Zero)
            {
                _onSystemShutdown();
                return IntPtr.Zero;
            }
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (_menuOpen || _hwnd == IntPtr.Zero || _disposed) return;
        _menuOpen = true;
        IntPtr hMenu = IntPtr.Zero;
        try
        {
            TrayMenuSnapshot snap = _getSnapshot();
            hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return;

            AppendMenu(hMenu, MfString, (UIntPtr)CmdOpen, snap.OpenLabel);
            SetMenuDefaultItem(hMenu, CmdOpen, false);
            AppendMenu(hMenu, MfSeparator, UIntPtr.Zero, null);
            AppendMenu(hMenu, MfString | (snap.AutoStartChecked ? MfChecked : MfUnchecked),
                (UIntPtr)CmdAutoStart, snap.AutoStartLabel);

            if (snap.ShowDisconnect)
            {
                AppendMenu(hMenu, MfString, (UIntPtr)CmdDisconnect, snap.DisconnectLabel);
            }

            AppendMenu(hMenu, MfSeparator, UIntPtr.Zero, null);
            AppendMenu(hMenu, MfString, (UIntPtr)CmdExit, snap.ExitLabel);

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);
            uint cmd = TrackPopupMenuEx(hMenu, TpmRightButton | TpmReturnCmd | TpmNoNotify, pt.X, pt.Y, _hwnd,
                IntPtr.Zero);
            PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case CmdOpen:
                    _onRestore();
                    break;
                case CmdAutoStart:
                    _onToggleAutoStart();
                    break;
                case CmdDisconnect:
                    _onDisconnectHost();
                    break;
                case CmdExit:
                    _onExit();
                    break;
            }
        }
        finally
        {
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            _menuOpen = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_iconAdded && _hwnd != IntPtr.Zero)
        {
            var nid = CreateBaseNotifyIconData();
            ShellNotifyIcon(NimDelete, ref nid);
            _iconAdded = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        UnregisterClass(_className, _hInstance);

        if (_ownsIcon && _hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool ShellNotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired,
        uint fuLoad);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIdNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, bool fByPos);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}