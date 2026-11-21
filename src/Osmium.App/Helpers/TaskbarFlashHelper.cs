using System.Runtime.InteropServices;

namespace Osmium.App.Helpers;

internal sealed class TaskbarFlashHelper : IDisposable
{
    private ITaskbarList3? _taskbarList;
    private bool _taskbarListTried;
    private IntPtr _activeHwnd;
    private bool _isRedActive;
    private long _ignoreCodeActivateUntilTickMs;
    private bool _disposed;

    public bool IsRedActive => _isRedActive;

    public void SetSolidRed(IntPtr hwnd)
    {
        if (_disposed || hwnd == IntPtr.Zero) return;

        _activeHwnd = hwnd;
        _isRedActive = true;
        _ignoreCodeActivateUntilTickMs = Environment.TickCount64 + 600;

        ApplyRedTaskbarState(hwnd, redOn: true);
    }

    public void NotifyWindowActivated(IntPtr hwnd, bool isPointerActivated)
    {
        if (_disposed || !_isRedActive) return;
        if (!isPointerActivated && Environment.TickCount64 < _ignoreCodeActivateUntilTickMs)
        {
            return;
        }

        Clear(hwnd);
    }

    public void Clear(IntPtr hwnd)
    {
        IntPtr target = hwnd != IntPtr.Zero ? hwnd : _activeHwnd;
        if (!_isRedActive && target == IntPtr.Zero) return;

        _isRedActive = false;
        _ignoreCodeActivateUntilTickMs = 0;

        if (target != IntPtr.Zero)
        {
            ApplyRedTaskbarState(target, redOn: false);
        }

        _activeHwnd = IntPtr.Zero;
    }

    private void ApplyRedTaskbarState(IntPtr hwnd, bool redOn)
    {
        try
        {
            var tb = EnsureTaskbarList();
            if (tb == null) return;

            if (redOn)
            {
                tb.SetProgressState(hwnd, TbpFlag.Error);
                tb.SetProgressValue(hwnd, 100, 100);
            }
            else
            {
                tb.SetProgressState(hwnd, TbpFlag.NoProgress);
            }
        }
        catch
        {
        }
    }

    private ITaskbarList3? EnsureTaskbarList()
    {
        if (_taskbarList != null) return _taskbarList;
        if (_taskbarListTried) return null;
        _taskbarListTried = true;

        try
        {
            object comObj = new TaskbarListComObject();
            var inst = (ITaskbarList3)comObj;
            inst.HrInit();
            _taskbarList = inst;
        }
        catch
        {
            _taskbarList = null;
        }

        return _taskbarList;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear(_activeHwnd);

        if (_taskbarList != null)
        {
            try
            {
                Marshal.ReleaseComObject(_taskbarList);
            }
            catch
            {
            }

            _taskbarList = null;
        }
    }

    private enum TbpFlag
    {
        NoProgress = 0x00000000,
        Indeterminate = 0x00000001,
        Normal = 0x00000002,
        Error = 0x00000004,
        Paused = 0x00000008
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A976")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListComObject
    {
    }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig]
        int HrInit();

        [PreserveSig]
        int AddTab(IntPtr hwnd);

        [PreserveSig]
        int DeleteTab(IntPtr hwnd);

        [PreserveSig]
        int ActivateTab(IntPtr hwnd);

        [PreserveSig]
        int SetActiveAlt(IntPtr hwnd);

        [PreserveSig]
        int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        [PreserveSig]
        int SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);

        [PreserveSig]
        int SetProgressState(IntPtr hwnd, TbpFlag tbpFlags);
    }
}
