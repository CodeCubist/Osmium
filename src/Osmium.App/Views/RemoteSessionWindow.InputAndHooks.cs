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
    private (float normX, float normY) ComputeNormalizedVideoCoords(Point localDip)
    {
        double vw = Math.Max(1.0, VideoInputSurface.ActualWidth);
        double vh = Math.Max(1.0, VideoInputSurface.ActualHeight);

        int srcW = _latestStats.VideoWidth > 0 ? _latestStats.VideoWidth : Math.Max(1, _hostStatus.StreamWidth);
        int srcH = _latestStats.VideoHeight > 0 ? _latestStats.VideoHeight : Math.Max(1, _hostStatus.StreamHeight);
        if (srcW <= 0 || srcH <= 0)
        {
            return (Math.Clamp((float)(localDip.X / vw), 0f, 1f), Math.Clamp((float)(localDip.Y / vh), 0f, 1f));
        }

        double srcAspect = (double)srcW / srcH;
        double dstAspect = vw / vh;
        double vpW, vpH, vpX, vpY;
        if (srcAspect > dstAspect)
        {
            vpW = vw;
            vpH = vw / srcAspect;
            vpX = 0.0;
            vpY = (vh - vpH) * 0.5;
        }
        else
        {
            vpH = vh;
            vpW = vh * srcAspect;
            vpY = 0.0;
            vpX = (vw - vpW) * 0.5;
        }

        float nx = Math.Clamp((float)((localDip.X - vpX) / Math.Max(1.0, vpW)), 0f, 1f);
        float ny = Math.Clamp((float)((localDip.Y - vpY) / Math.Max(1.0, vpH)), 0f, 1f);
        return (nx, ny);
    }

    private void VideoInputSurface_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        UpdateCachedVideoSurfaceBounds();
        if (_clientHandle == IntPtr.Zero || _isCursorCaptured || _isClosed || _remoteSessionEnded ||
            _openFlyoutCount > 0) return;
        var pt = e.GetCurrentPoint(VideoInputSurface);
        var (nx, ny) = ComputeNormalizedVideoCoords(pt.Position);
        SendAbsoluteMouseImmediate(nx, ny);
    }

    private void VideoInputSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_hwnd != IntPtr.Zero && GetForegroundWindow() != _hwnd)
        {
            SetForegroundWindow(_hwnd);
        }

        _isWindowFocused = true;
        VideoInputSurface.Focus(FocusState.Programmatic);
        UpdateCachedVideoSurfaceBounds();
        if (_clientHandle == IntPtr.Zero || _remoteSessionEnded) return;

        if (_userPausedVirtualCapture && IsVirtualMouseEngagedNow)
        {
            _userPausedVirtualCapture = false;
            SyncKeyboardHookAndCursorCapture();
            e.Handled = true;
            return;
        }

        SyncKeyboardHookAndCursorCapture();

        if (_isCursorCaptured) return;

        var pt = e.GetCurrentPoint(VideoInputSurface);
        var (nx, ny) = ComputeNormalizedVideoCoords(pt.Position);
        SendAbsoluteMouseImmediate(nx, ny, force: true);

        byte btn = pt.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => 1,
            PointerUpdateKind.MiddleButtonPressed => 2,
            PointerUpdateKind.RightButtonPressed => 3,
            PointerUpdateKind.XButton1Pressed => 4,
            PointerUpdateKind.XButton2Pressed => 5,
            _ => 0
        };
        if (btn != 0)
        {
            NativeMethods.Osmium_ClientSendMouseButton(_clientHandle, btn, 1);
            VideoInputSurface.CapturePointer(e.Pointer);
        }

        e.Handled = true;
    }

    private void VideoInputSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_clientHandle == IntPtr.Zero || _isCursorCaptured || _remoteSessionEnded) return;

        var pt = e.GetCurrentPoint(VideoInputSurface);
        var (nx, ny) = ComputeNormalizedVideoCoords(pt.Position);
        SendAbsoluteMouseImmediate(nx, ny, force: true);

        byte btn = pt.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonReleased => 1,
            PointerUpdateKind.MiddleButtonReleased => 2,
            PointerUpdateKind.RightButtonReleased => 3,
            PointerUpdateKind.XButton1Released => 4,
            PointerUpdateKind.XButton2Released => 5,
            _ => 0
        };
        if (btn != 0)
        {
            NativeMethods.Osmium_ClientSendMouseButton(_clientHandle, btn, 0);
            try
            {
                VideoInputSurface.ReleasePointerCapture(e.Pointer);
            }
            catch
            {
            }
        }

        e.Handled = true;
    }

    private void VideoInputSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_clientHandle == IntPtr.Zero || _isCursorCaptured || _isClosed || _remoteSessionEnded ||
            _openFlyoutCount > 0) return;
        if (!_isWindowFocused)
        {
            if (_hwnd != IntPtr.Zero && GetForegroundWindow() == _hwnd)
                _isWindowFocused = true;
            else if (_mouseMode != 0)
                return;
        }

        var pts = e.GetIntermediatePoints(VideoInputSurface);
        if (pts != null && pts.Count > 1)
        {
            for (int i = pts.Count - 1; i >= 0; i--)
            {
                var (nx, ny) = ComputeNormalizedVideoCoords(pts[i].Position);
                SendAbsoluteMouseImmediate(nx, ny);
            }

            return;
        }

        var pt = e.GetCurrentPoint(VideoInputSurface);
        var (cx, cy) = ComputeNormalizedVideoCoords(pt.Position);
        SendAbsoluteMouseImmediate(cx, cy);
    }

    private void VideoInputSurface_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_clientHandle == IntPtr.Zero || _isCursorCaptured) return;
        var pt = e.GetCurrentPoint(VideoInputSurface);
        short delta = (short)pt.Properties.MouseWheelDelta;
        if (pt.Properties.IsHorizontalMouseWheel)
        {
            SendMouseWheelInput(delta, 0);
        }
        else
        {
            SendMouseWheelInput(0, delta);
        }

        e.Handled = true;
    }

    private void SendMouseWheelInput(short deltaX, short deltaY)
    {
        if (_clientHandle == IntPtr.Zero) return;
        short adjY = App.Engine.SettingsService.Settings.ReverseScrollDirection
            ? (short)Math.Clamp(-(int)deltaY, short.MinValue, short.MaxValue)
            : deltaY;
        if (deltaX != 0 || adjY != 0)
        {
            NativeMethods.Osmium_ClientSendMouseWheel(_clientHandle, deltaX, adjY);
        }
    }

    private void VideoInputSurface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
    }

    private bool IsTextInputFocused()
    {
        if (RootSessionGrid.XamlRoot == null) return false;
        var focused = FocusManager.GetFocusedElement(RootSessionGrid.XamlRoot);
        return focused is TextBox or PasswordBox or RichEditBox;
    }

    private bool AreTripleShortcutModifiersDown(bool llAltFlag = false)
    {
        bool ctrl = _llLCtrlDown || _llRCtrlDown ||
                    (GetAsyncKeyState(0x11) & 0x8000) != 0 ||
                    (GetAsyncKeyState(0xA2) & 0x8000) != 0 ||
                    (GetAsyncKeyState(0xA3) & 0x8000) != 0;
        bool alt = llAltFlag || _llLAltDown || _llRAltDown ||
                   (GetAsyncKeyState(0x12) & 0x8000) != 0 ||
                   (GetAsyncKeyState(0xA4) & 0x8000) != 0 ||
                   (GetAsyncKeyState(0xA5) & 0x8000) != 0;
        bool shift = _llLShiftDown || _llRShiftDown ||
                     (GetAsyncKeyState(0x10) & 0x8000) != 0 ||
                     (GetAsyncKeyState(0xA0) & 0x8000) != 0 ||
                     (GetAsyncKeyState(0xA1) & 0x8000) != 0;
        return ctrl && alt && shift;
    }

    private bool ExecuteSessionShortcut(VirtualKey key)
    {
        if (_isClosed) return false;
        switch (key)
        {
            case VirtualKey.F11:
                SetFullscreenState(!_isFullscreen);
                return true;
            case VirtualKey.M:
                if (_isCursorCaptured)
                    ReleaseCursorCapture(manualPause: true);
                else if (IsVirtualMouseEngagedNow)
                {
                    _userPausedVirtualCapture = false;
                    SyncKeyboardHookAndCursorCapture();
                }

                return true;
            case VirtualKey.Q:
                Close();
                return true;
            case VirtualKey.F:
                SetFullscreenState(!_isFullscreen);
                return true;
            case VirtualKey.O:
                var s = App.Engine.SettingsService.Settings;
                s.ShowPerformanceOverlay = !s.ShowPerformanceOverlay;
                _isUpdatingUi = true;
                OsdToggleMenuItem.IsChecked = s.ShowPerformanceOverlay;
                _isUpdatingUi = false;
                App.Engine.SettingsService.SaveSettings();
                ApplyOsdVisibilityAndDetailLayout();
                return true;
            default:
                return false;
        }
    }

    private bool HandleGlobalSessionShortcut(VirtualKey key, bool down)
    {
        if (!down) return false;

        if (key == VirtualKey.F11 && !IsTextInputFocused())
        {
            return ExecuteSessionShortcut(VirtualKey.F11);
        }

        if (!AreTripleShortcutModifiersDown()) return false;
        if (key is VirtualKey.M or VirtualKey.Q or VirtualKey.F or VirtualKey.O)
        {
            ReleaseAllSwallowedRemoteKeys();
            return ExecuteSessionShortcut(key);
        }

        return false;
    }

    private void RootSessionGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isClosed) return;
        if (HandleGlobalSessionShortcut(e.Key, down: true))
        {
            e.Handled = true;
            return;
        }

        if (IsTextInputFocused() || !_isWindowFocused) return;
        if (_llKeyHook != IntPtr.Zero) return;

        if (_clientHandle != IntPtr.Zero)
        {
            ushort vk = ResolveExactVk((ushort)e.Key, (ushort)e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey);
            NativeMethods.Osmium_ClientSendKey(_clientHandle, vk, (ushort)e.KeyStatus.ScanCode, 1,
                e.KeyStatus.IsExtendedKey ? 1 : 0);
            e.Handled = true;
        }
    }

    private void RootSessionGrid_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_isClosed || IsTextInputFocused() || !_isWindowFocused) return;
        if (_llKeyHook != IntPtr.Zero) return;

        if (_clientHandle != IntPtr.Zero)
        {
            ushort vk = ResolveExactVk((ushort)e.Key, (ushort)e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey);
            NativeMethods.Osmium_ClientSendKey(_clientHandle, vk, (ushort)e.KeyStatus.ScanCode, 0,
                e.KeyStatus.IsExtendedKey ? 1 : 0);
            e.Handled = true;
        }
    }

    private static ushort ResolveExactVk(ushort vk, ushort scanCode, bool isExtended)
    {
        if (vk is 0x10 or 0x11 or 0x12)
        {
            uint mapped = MapVirtualKey(isExtended ? (uint)(0xE000 | scanCode) : scanCode, MapVkVscToVkEx);
            if (mapped != 0) return (ushort)mapped;
        }

        return vk;
    }

    private void RemoteSessionWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        ImmersiveWindowHelper.SyncContentBridges(this);
        _isWindowFocused = args.WindowActivationState != WindowActivationState.Deactivated;
        if (!_isWindowFocused)
        {
            ReleaseCursorCapture(manualPause: false);
            SetLowLevelKeyboardHook(false);
        }
        else
        {
            SyncKeyboardHookAndCursorCapture();
            OnSessionWindowActivatedForClipboard();
        }
    }

    private void VideoSwapChainPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateVideoSwapChainSize();
    }

    private void VideoSwapChainPanel_CompositionScaleChanged(SwapChainPanel sender, object args)
    {
        UpdateVideoSwapChainSize();
    }

    private void UpdateVideoSwapChainSize()
    {
        if (_clientHandle == IntPtr.Zero || _isClosed) return;
        double w = VideoSwapChainPanel.ActualWidth;
        double h = VideoSwapChainPanel.ActualHeight;
        if (w < 16 || h < 16) return;

        float scale = VideoSwapChainPanel.CompositionScaleX > 0.1f
            ? VideoSwapChainPanel.CompositionScaleX
            : (float)(VideoSwapChainPanel.XamlRoot?.RasterizationScale ?? 1.0);
        int wPx = Math.Max(64, (int)Math.Ceiling(w * scale));
        int hPx = Math.Max(64, (int)Math.Ceiling(h * scale));
        float exactScale = (float)(wPx / w);
        if (wPx == _lastSwapChainPxW && hPx == _lastSwapChainPxH && Math.Abs(exactScale - _lastSwapChainScale) < 0.001f)
        {
            return;
        }

        _lastSwapChainPxW = wPx;
        _lastSwapChainPxH = hPx;
        _lastSwapChainScale = exactScale;
        NativeMethods.Osmium_ClientResize(_clientHandle, wPx, hPx, exactScale);
    }

    private void RemoteSessionWindow_SizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        ImmersiveWindowHelper.SyncContentBridges(this);
        UpdateVideoSwapChainSize();
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateVideoSwapChainSize();
            UpdateTitleBarPassthroughRegions();
            UpdateCachedVideoSurfaceBounds();
        });
    }

    private void UpdateTitleBarCaptionMetrics()
    {
        if (_appWindow == null || AppTitleBar.XamlRoot == null) return;
        try
        {
            if (_isFullscreen)
            {
                AppTitleBar.Padding = new Thickness(12, 0, 12, 0);
                TitleBarBottomDivider.Margin = new Thickness(-12, 0, -12, 0);
                RightMenuToolbar.Height = double.NaN;
                RightMenuToolbar.VerticalAlignment = VerticalAlignment.Center;
                SessionMenuButton.VerticalAlignment = VerticalAlignment.Center;
                SessionMenuButton.Height = 28;
                SessionMenuButton.CornerRadius = new CornerRadius(6);
                return;
            }

            double scale = Math.Max(0.5, AppTitleBar.XamlRoot.RasterizationScale);
            double rightInsetDip = _appWindow.TitleBar.RightInset > 0
                ? _appWindow.TitleBar.RightInset / scale
                : 138.0;
            double captionH = _appWindow.TitleBar.Height > 0
                ? _appWindow.TitleBar.Height / scale
                : 32.0;
            captionH = Math.Clamp(captionH, 28.0, AppTitleBar.Height > 0 ? AppTitleBar.Height : 46.0);

            AppTitleBar.Padding = new Thickness(12, 0, rightInsetDip, 0);
            TitleBarBottomDivider.Margin = new Thickness(-12, 0, -(rightInsetDip + 16), 0);
            RightMenuToolbar.Height = captionH;
            RightMenuToolbar.VerticalAlignment = VerticalAlignment.Top;
            SessionMenuButton.VerticalAlignment = VerticalAlignment.Center;
            SessionMenuButton.Height = Math.Clamp(Math.Round(captionH - 4.0), 24.0, 30.0);
            SessionMenuButton.CornerRadius = new CornerRadius(6);
        }
        catch
        {
        }
    }

    private void UpdateTitleBarPassthroughRegions()
    {
        if (_appWindow == null || AppTitleBar.XamlRoot == null) return;
        try
        {
            UpdateTitleBarCaptionMetrics();
            AppTitleBar.UpdateLayout();

            var nonClient = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
            if (_isFullscreen)
            {
                nonClient.SetRegionRects(NonClientRegionKind.Caption, []);
                if (AppTitleBar.Visibility != Visibility.Visible || AppTitleBar.ActualWidth <= 1 ||
                    AppTitleBar.ActualHeight <= 1)
                {
                    nonClient.SetRegionRects(NonClientRegionKind.Passthrough, []);
                    return;
                }

                float fsScale = (float)AppTitleBar.XamlRoot.RasterizationScale;
                var barPt = AppTitleBar.TransformToVisual(null).TransformPoint(new Point(0, 0));
                nonClient.SetRegionRects(NonClientRegionKind.Passthrough, [
                    new RectInt32(
                        (int)Math.Floor(barPt.X * fsScale),
                        (int)Math.Floor(barPt.Y * fsScale),
                        (int)Math.Ceiling(AppTitleBar.ActualWidth * fsScale),
                        (int)Math.Ceiling(AppTitleBar.ActualHeight * fsScale))
                ]);
                return;
            }

            if (AppTitleBar.Visibility != Visibility.Visible)
            {
                nonClient.SetRegionRects(NonClientRegionKind.Passthrough, []);
                return;
            }

            float scale = (float)AppTitleBar.XamlRoot.RasterizationScale;
            var rects = new List<RectInt32>();

            void AddElementRect(FrameworkElement el, double? maxWidth = null)
            {
                if (el.Visibility != Visibility.Visible || el.ActualWidth <= 1 || el.ActualHeight <= 1) return;
                double w = maxWidth.HasValue ? Math.Min(el.ActualWidth, maxWidth.Value) : el.ActualWidth;
                if (w <= 1) return;
                var pt = el.TransformToVisual(null).TransformPoint(new Point(0, 0));
                rects.Add(new RectInt32(
                    (int)Math.Floor(pt.X * scale),
                    (int)Math.Floor(pt.Y * scale),
                    (int)Math.Ceiling(w * scale),
                    (int)Math.Ceiling(el.ActualHeight * scale)));
            }

            AddElementRect(BrowserTabsArea);
            AddElementRect(SessionMenuButton);

            nonClient.SetRegionRects(NonClientRegionKind.Passthrough, rects.ToArray());
        }
        catch
        {
        }
    }

    private void AttachInputWndProc()
    {
        if (_hwnd == IntPtr.Zero) return;

        lock (_subclassLock)
        {
            if (_blankCursor == IntPtr.Zero)
            {
                _blankCursor = CreateBlankCursor();
            }

            if (_wndProcDel == null)
            {
                _wndProcDel = SubclassInputWndProc;
                _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDel);
            }

            _enumChildDel ??= (child, _) =>
            {
                SubclassWindowLocked(child);
                return true;
            };

            SubclassWindowLocked(_hwnd);
            EnumChildWindows(_hwnd, _enumChildDel, IntPtr.Zero);
        }

        if (!_rawInputRegistered)
        {
            var rid = new RawInputDevice
            {
                UsagePage = 0x01,
                Usage = 0x02,
                Flags = RidevInputSink,
                Target = _hwnd
            };
            _rawInputRegistered = RegisterRawInputDevices([rid], 1, (uint)Marshal.SizeOf<RawInputDevice>());
        }

        AttachClipboardListener();
    }

    private void EnsureChildHwndsSubclassed()
    {
        if (_hwnd == IntPtr.Zero || _isClosed || _wndProcPtr == IntPtr.Zero) return;
        lock (_subclassLock)
        {
            _enumChildDel ??= (child, _) =>
            {
                SubclassWindowLocked(child);
                return true;
            };
            SubclassWindowLocked(_hwnd);
            EnumChildWindows(_hwnd, _enumChildDel, IntPtr.Zero);
        }
    }

    private void SubclassWindowLocked(IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero || _wndProcPtr == IntPtr.Zero || SubclassedWindows.ContainsKey(targetHwnd))
            return;

        GetWindowThreadProcessId(targetHwnd, out uint pid);
        if (pid != (uint)Environment.ProcessId) return;

        IntPtr prev = SetWindowLongPtr(targetHwnd, GwlWndProc, _wndProcPtr);
        if (prev != IntPtr.Zero && prev != _wndProcPtr)
        {
            SubclassedWindows[targetHwnd] = new SubclassedWindowEntry
            {
                Window = new WeakReference<RemoteSessionWindow>(this),
                PrevWndProc = prev
            };
            if (!_ownedSubclassedHwnds.Contains(targetHwnd))
            {
                _ownedSubclassedHwnds.Add(targetHwnd);
            }
        }
        else if (prev == _wndProcPtr)
        {
        }
    }

    private void SetBlankClassCursors(bool blank)
    {
        lock (_subclassLock)
        {
            if (blank)
            {
                if (_blankCursor == IntPtr.Zero) return;
                foreach (IntPtr h in _ownedSubclassedHwnds)
                {
                    if (!IsWindow(h)) continue;
                    IntPtr prev = SetClassLongPtr(h, GclpHCursor, _blankCursor);
                    if (prev != _blankCursor && !_savedClassCursors.ContainsKey(h))
                    {
                        _savedClassCursors[h] = prev;
                    }
                }
            }
            else
            {
                foreach (var kv in _savedClassCursors)
                {
                    if (IsWindow(kv.Key))
                    {
                        SetClassLongPtr(kv.Key, GclpHCursor, kv.Value);
                    }
                }

                _savedClassCursors.Clear();
            }
        }
    }

    private static IntPtr CreateBlankCursor()
    {
        byte[] andMask = new byte[128];
        Array.Fill(andMask, (byte)0xFF);
        byte[] xorMask = new byte[128];
        return CreateCursor(GetModuleHandle(null), 0, 0, 32, 32, andMask, xorMask);
    }

    private void DetachInputWndProcAndHooks()
    {
        DetachClipboardListener();
        SetLowLevelKeyboardHook(false);

        if (_rawInputRegistered && _hwnd != IntPtr.Zero)
        {
            var rid = new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = RidevRemove, Target = IntPtr.Zero };
            RegisterRawInputDevices([rid], 1, (uint)Marshal.SizeOf<RawInputDevice>());
            _rawInputRegistered = false;
        }

        SetBlankClassCursors(false);

        lock (_subclassLock)
        {
            for (int i = _ownedSubclassedHwnds.Count - 1; i >= 0; i--)
            {
                IntPtr h = _ownedSubclassedHwnds[i];
                if (SubclassedWindows.TryRemove(h, out var entry) && IsWindow(h))
                {
                    if (GetWindowLongPtr(h, GwlWndProc) == _wndProcPtr)
                    {
                        SetWindowLongPtr(h, GwlWndProc, entry.PrevWndProc);
                    }
                }
            }

            _ownedSubclassedHwnds.Clear();
        }

        IntPtr blank = Interlocked.Exchange(ref _blankCursor, IntPtr.Zero);
        if (blank != IntPtr.Zero)
        {
            DestroyCursor(blank);
        }
    }

    private static IntPtr SubclassInputWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (SubclassedWindows.TryGetValue(hwnd, out var entry))
        {
            entry.Window.TryGetTarget(out var win);

            if (msg == WmNcDestroy)
            {
                SubclassedWindows.TryRemove(hwnd, out _);
                if (win != null)
                {
                    lock (win._subclassLock)
                    {
                        if (win._savedClassCursors.Remove(hwnd, out IntPtr origCursor) && IsWindow(hwnd))
                        {
                            SetClassLongPtr(hwnd, GclpHCursor, origCursor);
                        }

                        win._ownedSubclassedHwnds.Remove(hwnd);
                    }
                }

                if (GetWindowLongPtr(hwnd, GwlWndProc) == (win?._wndProcPtr ?? IntPtr.Zero))
                {
                    SetWindowLongPtr(hwnd, GwlWndProc, entry.PrevWndProc);
                }

                return CallWindowProc(entry.PrevWndProc, hwnd, msg, wParam, lParam);
            }

            if (win != null && !win._isClosed)
            {
                if (win._isFullscreen)
                {
                    if (msg == WmNcLButtonDblClk)
                    {
                        return IntPtr.Zero;
                    }

                    if (msg == WmSysCommand && (((uint)wParam.ToInt64()) & 0xFFF0U) == ScMove)
                    {
                        return IntPtr.Zero;
                    }
                }

                if (msg == WmParentNotify && (((uint)wParam.ToInt64()) & 0xFFFF) == 1)
                {
                    win.EnsureChildHwndsSubclassed();
                }

                if (msg == WmClipboardUpdate && hwnd == win._hwnd && !win._remoteSessionEnded)
                {
                    win.OnLocalClipboardUpdated();
                }

                if (msg == WmInput && hwnd == win._hwnd &&
                    win._isWindowFocused && !win._remoteSessionEnded && win._openFlyoutCount == 0)
                {
                    win.ProcessRawMouseInput(lParam);
                }

                if (win._isCursorCaptured && win._isWindowFocused && !win._remoteSessionEnded)
                {
                    if (msg == WmSetCursor)
                    {
                        IntPtr blank = win._blankCursor;
                        if (blank != IntPtr.Zero) SetCursor(blank);
                        return new IntPtr(1);
                    }

                    if (msg == WmMouseMove || msg == WmPointerUpdate || msg == WmPointerEnter || msg == WmMouseActivate)
                    {
                        if (msg == WmMouseMove && GetCursorPos(out var cur) && GetClipCursor(out var clip))
                        {
                            int cx = clip.Left;
                            int cy = clip.Top;
                            if (cur.X != cx || cur.Y != cy)
                            {
                                SetCursorPos(cx, cy);
                            }
                        }

                        IntPtr res = CallWindowProc(entry.PrevWndProc, hwnd, msg, wParam, lParam);
                        IntPtr blank = win._blankCursor;
                        if (blank != IntPtr.Zero) SetCursor(blank);
                        return res;
                    }
                }
            }

            return CallWindowProc(entry.PrevWndProc, hwnd, msg, wParam, lParam);
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void RefreshTargetMouseSamplingHz()
    {
        double hostFps = _hostStatus.Fps > 0 ? _hostStatus.Fps : _currentFps;
        if (_latestStats.Fps > hostFps) hostFps = _latestStats.Fps;
        hostFps = Math.Clamp(hostFps, 30.0, 240.0);
        _targetMouseSamplingHz = Math.Clamp((int)Math.Ceiling(hostFps * 1.2) + 5, 75, 300);
    }

    private void UpdateCachedVideoSurfaceBounds()
    {
        try
        {
            if (_isClosed || VideoInputSurface.XamlRoot == null) return;
            double vw = VideoInputSurface.ActualWidth;
            double vh = VideoInputSurface.ActualHeight;
            if (vw < 16 || vh < 16) return;

            float scale = (float)VideoInputSurface.XamlRoot.RasterizationScale;
            var ptDip = VideoInputSurface.TransformToVisual(null).TransformPoint(new Point(0, 0));
            _surfLeftPx = (int)Math.Round(ptDip.X * scale);
            _surfTopPx = (int)Math.Round(ptDip.Y * scale);
            _surfWidthPx = Math.Max(1, (int)Math.Round(vw * scale));
            _surfHeightPx = Math.Max(1, (int)Math.Round(vh * scale));

            int sw = _latestStats.VideoWidth > 0 ? _latestStats.VideoWidth : _hostStatus.StreamWidth;
            int sh = _latestStats.VideoHeight > 0 ? _latestStats.VideoHeight : _hostStatus.StreamHeight;
            if (sw > 0 && sh > 0)
            {
                _cachedStreamWidth = sw;
                _cachedStreamHeight = sh;
            }
        }
        catch
        {
        }
    }

    private bool TryComputeNormFromClientPx(int clientX, int clientY, out float nx, out float ny)
    {
        int sl = _surfLeftPx;
        int st = _surfTopPx;
        int sw = _surfWidthPx;
        int sh = _surfHeightPx;
        if (sw <= 1 || sh <= 1 || clientX < sl || clientX >= sl + sw || clientY < st || clientY >= st + sh)
        {
            nx = 0;
            ny = 0;
            return false;
        }

        double localX = clientX - sl;
        double localY = clientY - st;
        int srcW = _cachedStreamWidth;
        int srcH = _cachedStreamHeight;
        if (srcW <= 0 || srcH <= 0)
        {
            nx = Math.Clamp((float)(localX / sw), 0f, 1f);
            ny = Math.Clamp((float)(localY / sh), 0f, 1f);
            return true;
        }

        double srcAspect = (double)srcW / srcH;
        double dstAspect = (double)sw / sh;
        double vpW, vpH, vpX, vpY;
        if (srcAspect > dstAspect)
        {
            vpW = sw;
            vpH = sw / srcAspect;
            vpX = 0.0;
            vpY = (sh - vpH) * 0.5;
        }
        else
        {
            vpH = sh;
            vpW = sh * srcAspect;
            vpY = 0.0;
            vpX = (sw - vpW) * 0.5;
        }

        nx = Math.Clamp((float)((localX - vpX) / Math.Max(1.0, vpW)), 0f, 1f);
        ny = Math.Clamp((float)((localY - vpY) / Math.Max(1.0, vpH)), 0f, 1f);
        return true;
    }

    private bool TrySampleCurrentCursorNorm(out float nx, out float ny)
    {
        nx = 0;
        ny = 0;
        if (_hwnd == IntPtr.Zero || _pointerInTopBar) return false;
        if (!GetCursorPos(out var scr)) return false;

        IntPtr hoveredRoot = GetAncestor(WindowFromPoint(scr), GaRoot);
        if (hoveredRoot != _hwnd) return false;

        if (!ScreenToClient(_hwnd, ref scr)) return false;
        return TryComputeNormFromClientPx(scr.X, scr.Y, out nx, out ny);
    }

    private void SendAbsoluteMouseImmediate(float nx, float ny, bool force = false)
    {
        lock (_mousePacerLock)
        {
            IntPtr handle = _clientHandle;
            if (handle == IntPtr.Zero || _isClosed || _remoteSessionEnded) return;
            if (!force && Math.Abs(nx - _lastSentNormX) < 1e-5f && Math.Abs(ny - _lastSentNormY) < 1e-5f)
                return;

            _lastSentNormX = nx;
            _lastSentNormY = ny;
            _hasPendingAbsMove = false;
            _lastMouseSendTicks = Stopwatch.GetTimestamp();
            NativeMethods.Osmium_ClientSendMouseMove(handle, 1, nx, ny);
        }
    }

    private void FlushPendingMouseLocked(IntPtr handle, long nowTicks)
    {
        if (_isCursorCaptured)
        {
            if (_pendingRelX != 0.0 || _pendingRelY != 0.0)
            {
                float dx = (float)_pendingRelX;
                float dy = (float)_pendingRelY;
                _pendingRelX = 0.0;
                _pendingRelY = 0.0;
                _lastMouseSendTicks = nowTicks;
                NativeMethods.Osmium_ClientSendMouseMove(handle, 0, dx, dy);
            }
        }
        else if (_hasPendingAbsMove)
        {
            _hasPendingAbsMove = false;
            if (TrySampleCurrentCursorNorm(out float nx, out float ny) &&
                (Math.Abs(nx - _lastSentNormX) >= 1e-5f || Math.Abs(ny - _lastSentNormY) >= 1e-5f))
            {
                _lastSentNormX = nx;
                _lastSentNormY = ny;
                _lastMouseSendTicks = nowTicks;
                NativeMethods.Osmium_ClientSendMouseMove(handle, 1, nx, ny);
            }
        }
    }
}