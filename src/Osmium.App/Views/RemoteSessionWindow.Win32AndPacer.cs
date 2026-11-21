using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ShapesPath = Microsoft.UI.Xaml.Shapes.Path;
using Osmium.App.Helpers;
using Osmium.App.Services;
using Osmium.Core.Interop;
using Osmium.Core.Models;
using Osmium.Core.Network;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WinRT;
using WinRT.Interop;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Osmium.App.Views;

public sealed partial class RemoteSessionWindow : Window
{
    private void StartMousePacer()
    {
        if (_mousePacerThread != null) return;
        _mousePacerStop = false;
        _timerPeriodActive = timeBeginPeriod(1) == 0;
        _mousePacerThread = new Thread(MousePacerLoop)
        {
            IsBackground = true,
            Name = "OsmiumMousePacer",
            Priority = ThreadPriority.AboveNormal
        };
        _mousePacerThread.Start();
    }

    private void StopMousePacer()
    {
        _mousePacerStop = true;
        _mousePacerWake.Set();
        var t = Interlocked.Exchange(ref _mousePacerThread, null);
        if (t != null && t.IsAlive)
        {
            t.Join(250);
        }

        if (_timerPeriodActive)
        {
            _timerPeriodActive = false;
            timeEndPeriod(1);
        }
    }

    private void MousePacerLoop()
    {
        while (!_mousePacerStop)
        {
            _mousePacerWake.Wait();
            if (_mousePacerStop) break;

            int sleepMs = 0;
            lock (_mousePacerLock)
            {
                IntPtr handle = _clientHandle;
                if (handle == IntPtr.Zero || _isClosed || _remoteSessionEnded)
                {
                    _pendingRelX = 0.0;
                    _pendingRelY = 0.0;
                    _hasPendingAbsMove = false;
                    _mousePacerWake.Reset();
                    continue;
                }

                bool hasWork = (_isCursorCaptured && (_pendingRelX != 0.0 || _pendingRelY != 0.0)) ||
                               (!_isCursorCaptured && _hasPendingAbsMove);
                if (!hasWork)
                {
                    _mousePacerWake.Reset();
                    continue;
                }

                long minTicks = Math.Max(1L, Stopwatch.Frequency / Math.Max(60, _targetMouseSamplingHz));
                long now = Stopwatch.GetTimestamp();
                long remaining = (_lastMouseSendTicks + minTicks) - now;
                if (remaining <= 0)
                {
                    FlushPendingMouseLocked(handle, now);
                    _mousePacerWake.Reset();
                    continue;
                }

                sleepMs = (int)((remaining * 1000L) / Stopwatch.Frequency);
            }

            if (sleepMs >= 1)
            {
                Thread.Sleep(Math.Min(sleepMs, 6));
            }
            else
            {
                Thread.SpinWait(64);
                Thread.Yield();
            }
        }
    }

    private unsafe void ProcessRawMouseInput(IntPtr hRawInput)
    {
        if (_clientHandle == IntPtr.Zero || _remoteSessionEnded) return;

        uint size = 0;
        uint hdrSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(hRawInput, RidInput, IntPtr.Zero, ref size, hdrSize) != 0 ||
            size < Marshal.SizeOf<RawInputMousePacket>() || size > 512)
        {
            return;
        }

        byte* buf = stackalloc byte[(int)size];
        if (GetRawInputData(hRawInput, RidInput, (IntPtr)buf, ref size, hdrSize) != size) return;

        RawInputMousePacket* pkt = (RawInputMousePacket*)buf;
        if (pkt->Header.Type != RimTypeMouse) return;

        ref readonly RawMouse rm = ref pkt->Mouse;
        bool captured = _isCursorCaptured;
        ushort bf = rm.ButtonFlags;
        long now = Stopwatch.GetTimestamp();
        long minTicks = Math.Max(1L, Stopwatch.Frequency / Math.Max(60, _targetMouseSamplingHz));

        if (!captured)
        {
            if ((rm.LastX != 0 || rm.LastY != 0) && !_pointerInTopBar)
            {
                lock (_mousePacerLock)
                {
                    IntPtr handle = _clientHandle;
                    if (handle == IntPtr.Zero || _isClosed || _remoteSessionEnded) return;
                    if ((now - _lastMouseSendTicks) >= minTicks)
                    {
                        if (TrySampleCurrentCursorNorm(out float nx, out float ny) &&
                            (Math.Abs(nx - _lastSentNormX) >= 1e-5f || Math.Abs(ny - _lastSentNormY) >= 1e-5f))
                        {
                            _lastSentNormX = nx;
                            _lastSentNormY = ny;
                            _hasPendingAbsMove = false;
                            _lastMouseSendTicks = now;
                            NativeMethods.Osmium_ClientSendMouseMove(handle, 1, nx, ny);
                        }
                    }
                    else
                    {
                        _hasPendingAbsMove = true;
                        _mousePacerWake.Set();
                    }
                }
            }

            return;
        }

        double dx = 0.0;
        double dy = 0.0;
        if ((rm.Flags & MouseMoveAbsolute) == 0)
        {
            _hasPrevRawAbs = false;
            dx = rm.LastX;
            dy = rm.LastY;
        }
        else if (rm.LastX != 0 || rm.LastY != 0)
        {
            if (_hasPrevRawAbs)
            {
                double adx = (rm.LastX - _prevRawAbsX) / 40.0;
                double ady = (rm.LastY - _prevRawAbsY) / 40.0;
                if (Math.Abs(adx) >= 0.5 || Math.Abs(ady) >= 0.5)
                {
                    dx = adx;
                    dy = ady;
                }
            }

            _prevRawAbsX = rm.LastX;
            _prevRawAbsY = rm.LastY;
            _hasPrevRawAbs = true;
        }

        bool hasButtons = (bf & 0x03FF) != 0;
        lock (_mousePacerLock)
        {
            IntPtr handle = _clientHandle;
            if (handle == IntPtr.Zero || _isClosed || _remoteSessionEnded) return;

            if (dx != 0.0 || dy != 0.0)
            {
                _pendingRelX += dx;
                _pendingRelY += dy;
            }

            if (hasButtons || (now - _lastMouseSendTicks) >= minTicks)
            {
                FlushPendingMouseLocked(handle, now);
            }
            else if (_pendingRelX != 0.0 || _pendingRelY != 0.0)
            {
                _mousePacerWake.Set();
            }
        }

        IntPtr btnHandle = Volatile.Read(ref _clientHandle);
        if (btnHandle == IntPtr.Zero) return;
        if ((bf & 0x0001) != 0) NativeMethods.Osmium_ClientSendMouseButton(btnHandle, 1, 1);
        if ((bf & 0x0002) != 0) NativeMethods.Osmium_ClientSendMouseButton(btnHandle, 1, 0);
        if ((bf & 0x0004) != 0) NativeMethods.Osmium_ClientSendMouseButton(btnHandle, 3, 1);
        if ((bf & 0x0008) != 0) NativeMethods.Osmium_ClientSendMouseButton(btnHandle, 3, 0);
        if ((bf & 0x0010) != 0) NativeMethods.Osmium_ClientSendMouseButton(btnHandle, 2, 1);
        if ((bf & 0x0020) != 0) NativeMethods.Osmium_ClientSendMouseButton(btnHandle, 2, 0);
        if ((bf & 0x0040) != 0) NativeMethods.Osmium_ClientSendMouseButton(btnHandle, 4, 1);
        if ((bf & 0x0080) != 0) NativeMethods.Osmium_ClientSendMouseButton(btnHandle, 4, 0);
        if ((bf & 0x0100) != 0) NativeMethods.Osmium_ClientSendMouseButton(btnHandle, 5, 1);
        if ((bf & 0x0200) != 0) NativeMethods.Osmium_ClientSendMouseButton(btnHandle, 5, 0);
        if ((bf & 0x0400) != 0) SendMouseWheelInput(0, (short)rm.ButtonData);
        if ((bf & 0x0800) != 0) SendMouseWheelInput((short)rm.ButtonData, 0);
    }

    private void ReleaseAllSwallowedRemoteKeys(bool keepSwallowUp = false)
    {
        if (_hookSwallowedKeys.Count == 0) return;
        IntPtr handle = _clientHandle;
        var keys = _hookSwallowedKeys.Keys.ToArray();
        if (handle != IntPtr.Zero && !_isClosed && !_remoteSessionEnded)
        {
            foreach (var key in keys)
            {
                if (_hookSwallowedKeys[key])
                {
                    NativeMethods.Osmium_ClientSendKey(handle, key.Vk, key.Scan, 0, key.Ext ? 1 : 0);
                }
            }
        }

        if (keepSwallowUp)
        {
            foreach (var key in keys)
            {
                _hookSwallowedKeys[key] = false;
            }
        }
        else
        {
            _hookSwallowedKeys.Clear();
        }
    }

    private bool IsSessionWindowForeground(out IntPtr fg)
    {
        fg = GetForegroundWindow();
        if (_hwnd == IntPtr.Zero || fg == IntPtr.Zero) return false;
        return fg == _hwnd ||
               GetAncestor(fg, GaRootOwner) == _hwnd ||
               GetAncestor(fg, GaRoot) == _hwnd;
    }

    private void ResetLowLevelModifierState()
    {
        _llLCtrlDown = false;
        _llRCtrlDown = false;
        _llLAltDown = false;
        _llRAltDown = false;
        _llLShiftDown = false;
        _llRShiftDown = false;
        _shortcutConsumedKeys.Clear();
    }

    private void TrackLowLevelModifier(ushort vk, bool down)
    {
        switch (vk)
        {
            case 0xA2: _llLCtrlDown = down; break;
            case 0xA3: _llRCtrlDown = down; break;
            case 0x11:
                _llLCtrlDown = down;
                if (!down) _llRCtrlDown = false;
                break;
            case 0xA4: _llLAltDown = down; break;
            case 0xA5: _llRAltDown = down; break;
            case 0x12:
                _llLAltDown = down;
                if (!down) _llRAltDown = false;
                break;
            case 0xA0: _llLShiftDown = down; break;
            case 0xA1: _llRShiftDown = down; break;
            case 0x10:
                _llLShiftDown = down;
                if (!down) _llRShiftDown = false;
                break;
        }
    }

    private void SetLowLevelKeyboardHook(bool enable)
    {
        if (enable && _llKeyHook == IntPtr.Zero)
        {
            ResetLowLevelModifierState();
            _llKeyProcDel = LowLevelKeyboardHandler;
            IntPtr hMod = GetModuleHandle(null);
            _llKeyHook = SetWindowsHookEx(WhKeyboardLl, _llKeyProcDel, hMod, 0);
        }
        else if (!enable && _llKeyHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_llKeyHook);
            _llKeyHook = IntPtr.Zero;
            ResetLowLevelModifierState();
            ReleaseAllSwallowedRemoteKeys();
        }
    }

    private IntPtr LowLevelKeyboardHandler(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_isClosed)
        {
            uint msg = (uint)wParam.ToInt64();
            bool down = msg == WmKeyDown || msg == WmSysKeyDown;
            bool up = msg == WmKeyUp || msg == WmSysKeyUp;
            if (down || up)
            {
                var kbd = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                ushort vk = (ushort)kbd.VkCode;
                bool isExt = (kbd.Flags & 0x01) != 0;
                var keyId = (vk, (ushort)kbd.ScanCode, isExt);

                TrackLowLevelModifier(vk, down);

                if (up && _shortcutConsumedKeys.Remove(keyId))
                {
                    return new IntPtr(1);
                }

                bool isOurFg = IsSessionWindowForeground(out IntPtr fg);

                if (down && (isOurFg || _isCursorCaptured))
                {
                    bool tripleMod = AreTripleShortcutModifiersDown((kbd.Flags & 0x20) != 0);
                    VirtualKey vkey = (VirtualKey)vk;
                    bool matchedMouseLockShortcut = tripleMod && vkey == VirtualKey.M;
                    bool matchedOtherTripleShortcut =
                        isOurFg && tripleMod && (vkey is VirtualKey.Q or VirtualKey.F or VirtualKey.O);
                    bool matchedF11 = isOurFg && vkey == VirtualKey.F11 && !IsTextInputFocused();

                    if (matchedMouseLockShortcut || matchedOtherTripleShortcut || matchedF11)
                    {
                        if (_shortcutConsumedKeys.Add(keyId))
                        {
                            if (matchedMouseLockShortcut && _isCursorCaptured)
                            {
                                ClipCursor(IntPtr.Zero);
                            }

                            DispatcherQueue.TryEnqueue(() =>
                            {
                                ReleaseAllSwallowedRemoteKeys(keepSwallowUp: true);
                                ExecuteSessionShortcut(vkey);
                            });
                        }

                        return new IntPtr(1);
                    }
                }

                bool forwardToHost = _isWindowFocused &&
                                     isOurFg &&
                                     fg == _hwnd &&
                                     !_remoteSessionEnded &&
                                     _clientHandle != IntPtr.Zero &&
                                     _openFlyoutCount == 0 &&
                                     !IsTextInputFocused();

                if (down && forwardToHost)
                {
                    _hookSwallowedKeys[keyId] = true;
                    NativeMethods.Osmium_ClientSendKey(
                        _clientHandle,
                        keyId.Item1,
                        keyId.Item2,
                        1,
                        isExt ? 1 : 0);
                    return new IntPtr(1);
                }

                if (up && _hookSwallowedKeys.Remove(keyId, out bool sentToHost))
                {
                    if (sentToHost && _clientHandle != IntPtr.Zero && !_remoteSessionEnded)
                    {
                        NativeMethods.Osmium_ClientSendKey(
                            _clientHandle,
                            keyId.Item1,
                            keyId.Item2,
                            0,
                            isExt ? 1 : 0);
                    }

                    return new IntPtr(1);
                }
            }
        }

        return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
    }

    private void RemoteSessionWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_isClosed) return;
        _isClosed = true;
        _remoteSessionEnded = true;

        App.Engine.Coordinator.ClientSessionEnded -= OnCoordinatorClientSessionEnded;
        LocalizationService.LanguageChanged -= OnLanguageChanged;

        _uiTimer.Stop();
        _gamepadTimer.Stop();
        _toastHideTimer.Stop();
        _fullscreenBarHideTimer.Stop();

        StopMousePacer();
        ReleaseCursorCapture(manualPause: false);
        DetachInputWndProcAndHooks();
        DisposeAuxTransferChannel();
        _winUiBlankCursor?.Dispose();
        _winUiBlankCursor = null;
        _mousePacerWake.Dispose();

        IntPtr handle = Interlocked.Exchange(ref _clientHandle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            NativeMethods.Osmium_StopClientEmbedded(handle);
        }

        _startedDel = null;
        _statsDel = null;
        _endedDel = null;
        _statusJsonDel = null;
        _auxDataDel = null;
        _logDel = null;
        _wndProcDel = null;
        _enumChildDel = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public UIntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouse
    {
        public ushort Flags;
        public ushort Padding;
        public ushort ButtonFlags;
        public ushort ButtonData;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputMousePacket
    {
        public RawInputHeader Header;
        public RawMouse Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint pt);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref NativePoint pt);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint pt);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint pt);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(ref NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(IntPtr rectNull);

    [DllImport("user32.dll")]
    private static extern bool GetClipCursor(out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool show);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight,
        byte[] pvANDPlane, byte[] pvXORPlane);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyCursor(IntPtr hCursor);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint numDevices, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size,
        uint sizeHeader);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetClassLongW")]
    private static extern int SetClassLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    private static IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8
            ? SetClassLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetClassLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy,
        uint uFlags);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}