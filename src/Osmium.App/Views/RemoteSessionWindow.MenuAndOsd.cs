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
    private InputCursor? EnsureWinUiBlankCursor()
    {
        if (_winUiBlankCursor != null) return _winUiBlankCursor;
        try
        {
            _winUiBlankCursor = InputDesktopResourceCursor.CreateFromModule("OsmiumEngine.dll", 101);
        }
        catch
        {
            try
            {
                string dllPath = Path.Combine(AppContext.BaseDirectory, "OsmiumEngine.dll");
                _winUiBlankCursor = InputDesktopResourceCursor.CreateFromModule(dllPath, 101);
            }
            catch
            {
            }
        }

        return _winUiBlankCursor;
    }

    private void EngageCursorCapture()
    {
        if (_isCursorCaptured || _hwnd == IntPtr.Zero || _remoteSessionEnded ||
            VideoInputSurface.XamlRoot == null) return;

        try
        {
            EnsureChildHwndsSubclassed();
            UpdateCachedVideoSurfaceBounds();

            float scale = (float)VideoInputSurface.XamlRoot.RasterizationScale;
            var ptDip = VideoInputSurface.TransformToVisual(null).TransformPoint(new Point(0, 0));
            var topLeft = new NativePoint
            {
                X = (int)Math.Round(ptDip.X * scale),
                Y = (int)Math.Round(ptDip.Y * scale)
            };
            ClientToScreen(_hwnd, ref topLeft);

            int widthPx = Math.Max(16, (int)Math.Round(VideoInputSurface.ActualWidth * scale));
            int heightPx = Math.Max(16, (int)Math.Round(VideoInputSurface.ActualHeight * scale));
            int cx = topLeft.X + widthPx / 2;
            int cy = topLeft.Y + heightPx / 2;

            InputCursor? blankUi = EnsureWinUiBlankCursor();
            if (blankUi != null)
            {
                RootSessionGrid.CustomCursor = blankUi;
                VideoInputSurface.CustomCursor = blankUi;
            }

            if (_blankCursor == IntPtr.Zero)
            {
                _blankCursor = CreateBlankCursor();
            }

            _hasPrevRawAbs = false;
            _isCursorCaptured = true;

            SetBlankClassCursors(true);
            if (_blankCursor != IntPtr.Zero)
            {
                SetCursor(_blankCursor);
            }

            if (!_cursorHidden)
            {
                int guard = 0;
                while (ShowCursor(false) >= 0 && guard++ < 16)
                {
                }

                _cursorHidden = true;
            }

            var clip = new NativeRect { Left = cx, Top = cy, Right = cx + 1, Bottom = cy + 1 };
            SetCursorPos(cx + 1, cy);
            SetCursorPos(cx, cy);
            ClipCursor(ref clip);

            if (_blankCursor != IntPtr.Zero)
            {
                SetCursor(_blankCursor);
            }

            UpdateMouseModeStatusLabel();
            ShowTopToast(LocalizationService.GetString("Session_ToastCursorCaptured"));
        }
        catch
        {
        }
    }

    private void ReleaseCursorCapture(bool manualPause)
    {
        if (manualPause) _userPausedVirtualCapture = true;

        _hasPrevRawAbs = false;
        if (_isCursorCaptured)
        {
            ClipCursor(IntPtr.Zero);
            _isCursorCaptured = false;
        }

        VideoInputSurface.CustomCursor = null;
        RootSessionGrid.CustomCursor = null;
        SetBlankClassCursors(false);

        if (_cursorHidden)
        {
            int guard = 0;
            while (ShowCursor(true) < 0 && guard++ < 16)
            {
            }

            _cursorHidden = false;
        }

        IntPtr arrow = LoadCursor(IntPtr.Zero, (IntPtr)IdcArrow);
        if (arrow != IntPtr.Zero)
        {
            SetCursor(arrow);
        }

        UpdateMouseModeStatusLabel();
    }

    private void ShowTopToast(string text)
    {
        _toastHideTimer.Stop();
        _toastStoryboard?.Stop();
        _toastStoryboard = null;

        TopToastText.Text = text;
        if (TopToastBanner.Visibility != Visibility.Visible)
        {
            TopToastTranslate.Y = -72;
            TopToastBanner.Opacity = 0;
            TopToastBanner.Visibility = Visibility.Visible;
        }

        var sb = new Storyboard();
        var slideIn = new DoubleAnimation
        {
            From = TopToastTranslate.Y,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slideIn, TopToastTranslate);
        Storyboard.SetTargetProperty(slideIn, "Y");

        var fadeIn = new DoubleAnimation
        {
            From = TopToastBanner.Opacity,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fadeIn, TopToastBanner);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");

        sb.Children.Add(slideIn);
        sb.Children.Add(fadeIn);
        _toastStoryboard = sb;
        sb.Completed += (_, _) =>
        {
            if (ReferenceEquals(_toastStoryboard, sb))
            {
                TopToastTranslate.Y = 0;
                TopToastBanner.Opacity = 1.0;
                _toastStoryboard = null;
            }
        };
        sb.Begin();

        _toastHideTimer.Start();
    }

    private void HideTopToastAnimated()
    {
        if (_isClosed || TopToastBanner.Visibility != Visibility.Visible) return;
        _toastStoryboard?.Stop();

        var sb = new Storyboard();
        var slideOut = new DoubleAnimation
        {
            From = TopToastTranslate.Y,
            To = -72,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(slideOut, TopToastTranslate);
        Storyboard.SetTargetProperty(slideOut, "Y");

        var fadeOut = new DoubleAnimation
        {
            From = TopToastBanner.Opacity,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(fadeOut, TopToastBanner);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");

        sb.Children.Add(slideOut);
        sb.Children.Add(fadeOut);
        _toastStoryboard = sb;
        sb.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_toastStoryboard, sb)) return;
            _toastStoryboard = null;
            TopToastTranslate.Y = -72;
            TopToastBanner.Opacity = 0;
            TopToastBanner.Visibility = Visibility.Collapsed;
        };
        sb.Begin();
    }

    private void UiTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isClosed) return;
        bool isOurFgNow = _hwnd != IntPtr.Zero && IsSessionWindowForeground(out _);
        if (_isWindowFocused && _hwnd != IntPtr.Zero && !isOurFgNow)
        {
            _isWindowFocused = false;
            SyncKeyboardHookAndCursorCapture();
        }
        else if (!_isWindowFocused && isOurFgNow)
        {
            _isWindowFocused = true;
            SyncKeyboardHookAndCursorCapture();
        }

        UpdateOsdDisplay();
        if (SessionMenuFlyout.IsOpen && DebugInfoExpander.IsExpanded)
        {
            DebugInfoBodyText.Text = BuildDebugSnapshotText();
        }
    }

    private void UpdateOsdDisplay()
    {
        if (OsdOverlayBorder.Visibility != Visibility.Visible) return;

        var s = _latestStats;
        var elapsed = _sessionClock.Elapsed;
        string elapsedStr = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        int route = s.TransportRoute > 0 ? s.TransportRoute : _launchArgs.TransportRoute;
        string routeLabel = FormatTransportRouteLabel(route);
        int natW = s.NativeWidth > 0 ? s.NativeWidth : _hostStatus.NativeWidth;
        int natH = s.NativeHeight > 0 ? s.NativeHeight : _hostStatus.NativeHeight;
        int vidW = s.VideoWidth > 0 ? s.VideoWidth : natW;
        int vidH = s.VideoHeight > 0 ? s.VideoHeight : natH;

        OsdConnTypeText.Text = LocalizationService.Format("Session_OsdConnTypeFmt", routeLabel);
        OsdLatencyText.Text = LocalizationService.Format("Session_OsdLatencyFmt", s.RttMs);
        OsdNativeResText.Text = LocalizationService.Format("Session_OsdNativeResFmt", natW, natH);
        OsdStreamResText.Text = LocalizationService.Format("Session_OsdStreamResFmt", vidW, vidH);
        OsdFpsText.Text = LocalizationService.Format("Session_OsdFpsFmt", $"{s.Fps:F1}");
        OsdBitrateText.Text = LocalizationService.Format("Session_OsdBitrateFmt", $"{s.BitrateMbps:F2}");
        OsdDurationText.Text = LocalizationService.Format("Session_OsdDurationFmt", elapsedStr);

        var detail = App.Engine.SettingsService.Settings.OsdDetailLevel;
        if (detail == OsdDetailLevel.Compact)
        {
            OsdStreamResText.Visibility = Visibility.Collapsed;
            OsdPacketLossText.Visibility = Visibility.Collapsed;
            OsdDecodeText.Visibility = Visibility.Collapsed;
            OsdCodecText.Visibility = Visibility.Collapsed;
            OsdMouseText.Visibility = Visibility.Collapsed;
            OsdPeerText.Visibility = Visibility.Collapsed;
        }
        else if (detail == OsdDetailLevel.Standard)
        {
            OsdStreamResText.Visibility = Visibility.Visible;
            OsdPacketLossText.Visibility = Visibility.Visible;
            OsdPacketLossText.Text = LocalizationService.Format("Session_OsdPacketLossSimpleFmt",
                $"{s.PacketLossPercent:F2}", s.DroppedFrames);
            OsdDecodeText.Visibility = Visibility.Collapsed;
            OsdCodecText.Visibility = Visibility.Collapsed;
            OsdMouseText.Visibility = Visibility.Collapsed;
            OsdPeerText.Visibility = Visibility.Collapsed;
        }
        else
        {
            OsdStreamResText.Visibility = Visibility.Visible;
            OsdPacketLossText.Visibility = Visibility.Visible;
            OsdPacketLossText.Text = LocalizationService.Format("Session_OsdPacketLossDetailedFmt",
                $"{s.PacketLossPercent:F2}", s.LostPackets, s.ReceivedPackets + s.LostPackets, s.DroppedFrames);
            OsdDecodeText.Visibility = Visibility.Visible;
            OsdDecodeText.Text = LocalizationService.Format("Session_OsdDecodeFmt", $"{s.DecodeLatencyMs:F1}");
            OsdCodecText.Visibility = Visibility.Visible;
            string hdrTag = s.EnableHdr != 0 ? "HDR10" : "SDR";
            string cFmt = s.EnableYuv444 != 0 ? "YUV444" : "YUV420";
            OsdCodecText.Text = LocalizationService.Format("Session_OsdCodecFmt",
                string.IsNullOrEmpty(s.CodecName) ? "--" : s.CodecName,
                string.IsNullOrEmpty(s.DecoderName) ? "--" : s.DecoderName, hdrTag, cFmt);
            OsdMouseText.Visibility = Visibility.Visible;
            string mText = _mouseMode switch
            {
                1 => LocalizationService.GetString("Session_OsdMouseVirtual"),
                2 => LocalizationService.GetString(_hostStatus.SmartFullscreenEngaged
                    ? "Session_OsdMouseSmartVirtual"
                    : "Session_OsdMouseSmartController"),
                _ => LocalizationService.GetString("Session_OsdMouseController")
            };
            OsdMouseText.Text = LocalizationService.Format("Session_OsdMouseFmt", mText);
            OsdPeerText.Visibility = Visibility.Visible;
            OsdPeerText.Text = LocalizationService.Format("Session_OsdPeerFmt", s.PeerEndpoint);
        }

        SyncOsdShadow();
    }

    private static string FormatTransportRouteLabel(int route) => route switch
    {
        1 => LocalizationService.GetString("Session_RouteLan"),
        2 => LocalizationService.GetString("Session_RouteIpv6"),
        3 => LocalizationService.GetString("Session_RouteDirect"),
        _ => LocalizationService.GetString("Session_RouteDirect")
    };

    private string BuildDebugSnapshotText()
    {
        var s = _latestStats;
        var elapsed = _sessionClock.Elapsed;
        string elapsedStr = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        int route = s.TransportRoute > 0 ? s.TransportRoute : _launchArgs.TransportRoute;
        string routeLabel = FormatTransportRouteLabel(route);

        int natW = s.NativeWidth > 0 ? s.NativeWidth : _hostStatus.NativeWidth;
        int natH = s.NativeHeight > 0 ? s.NativeHeight : _hostStatus.NativeHeight;
        string localEp = !string.IsNullOrWhiteSpace(s.LocalEndpoint)
            ? s.LocalEndpoint
            : (s.LocalPort > 0 ? $"0.0.0.0:{s.LocalPort}" : LocalizationService.GetString("Session_LocalAutoPort"));
        string peerEp = !string.IsNullOrWhiteSpace(s.PeerEndpoint) ? s.PeerEndpoint : _launchArgs.ActiveEndpoint;

        var sb = new StringBuilder();
        sb.AppendLine(LocalizationService.Format("Session_DbgLineRouteFmt", routeLabel));
        sb.AppendLine(LocalizationService.Format("Session_DbgLineLocalFmt", localEp));
        sb.AppendLine(LocalizationService.Format("Session_DbgLinePeerFmt", peerEp));
        sb.AppendLine(LocalizationService.Format("Session_DbgLineSigHostFmt", _launchArgs.SignalingServerHost,
            _launchArgs.SignalingServerIp));
        sb.AppendLine(LocalizationService.Format("Session_DbgLineSigUriFmt", _launchArgs.SignalingServerUrl));
        sb.AppendLine(LocalizationService.Format("Session_DbgLineNativeFmt", natW, natH));
        sb.AppendLine(LocalizationService.Format("Session_DbgLineStreamFmt", s.VideoWidth, s.VideoHeight));
        sb.AppendLine(LocalizationService.Format("Session_DbgLinePerfFmt", $"{s.Fps:F1}", s.RttMs,
            $"{s.DecodeLatencyMs:F2}"));
        sb.AppendLine(LocalizationService.Format("Session_DbgLineBitrateFmt", $"{s.BitrateMbps:F2}"));
        sb.AppendLine(LocalizationService.Format("Session_DbgLineDurationFmt", elapsedStr));
        sb.AppendLine(LocalizationService.Format("Session_DbgLineLossFmt", $"{s.PacketLossPercent:F2}", s.LostPackets,
            s.ReceivedPackets, s.DroppedFrames));
        sb.AppendLine(LocalizationService.Format("Session_DbgLineCodecFmt", s.CodecName, s.DecoderName,
            s.EnableHdr != 0 ? "HDR10" : "SDR", s.EnableYuv444 != 0 ? "4:4:4" : "4:2:0"));
        sb.AppendLine(LocalizationService.Format("Session_DbgLinePeripheralFmt", _mouseMode, _isCursorCaptured,
            _hostStatus.VirtualSpeakerActive, _hostStatus.VirtualMicActive));
        return sb.ToString().TrimEnd();
    }

    private void ApplyOsdVisibilityAndDetailLayout()
    {
        OsdOverlayBorder.Visibility = App.Engine.SettingsService.Settings.ShowPerformanceOverlay
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateOsdDisplay();
    }

    private void DisplayTabSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_clientHandle != IntPtr.Zero && sender is Button { Tag: int idx })
        {
            NativeMethods.Osmium_ClientSwitchDisplay(_clientHandle, idx);
            _hostStatus.ActiveDisplayIndex = idx;
            UpdateTabSelectionStyles();
        }
    }

    private void ViewScaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingUi) return;
        if (ViewScaleCombo.SelectedItem is ComboBoxItem { Tag: ScaleOption opt })
        {
            if (opt.Percent == 100)
            {
                _targetScalePercent = 100;
                _targetCustomWidth = 0;
                _targetCustomHeight = 0;
            }
            else
            {
                _targetCustomWidth = opt.TargetW;
                _targetCustomHeight = opt.TargetH;
                _targetScalePercent = opt.Percent > 0 ? opt.Percent : 100;
            }

            App.Engine.SettingsService.Settings.ClientScalePercent = _targetScalePercent;
            App.Engine.SettingsService.SaveSettings();
            SendUpdatedVideoParams();
        }
    }

    private void FpsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingUi) return;
        if (FpsCombo.SelectedItem is ComboBoxItem { Tag: int fps })
        {
            _currentFps = fps;
            RefreshTargetMouseSamplingHz();
            SendUpdatedVideoParams();
        }
    }

    private void VideoFlagToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi) return;
        if (ReferenceEquals(sender, HdrToggle) && !_hostStatus.HostHdrEnabled)
        {
            _enableHdr = false;
            if (HdrToggle.IsOn)
            {
                _isUpdatingUi = true;
                try
                {
                    HdrToggle.IsOn = false;
                }
                finally
                {
                    _isUpdatingUi = false;
                }
            }

            ShowTopToast(LocalizationService.GetString("Session_HdrRequiresHostHint"));
            return;
        }

        _enableHdr = _hostStatus.HostHdrEnabled && HdrToggle.IsOn;
        _enableYuv444 = Yuv444Toggle.IsOn;
        SendUpdatedVideoParams();
    }

    private void VsyncToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi || _clientHandle == IntPtr.Zero) return;
        _enableVsync = VsyncToggle.IsOn;
        NativeMethods.Osmium_ClientSetVsync(_clientHandle, _enableVsync ? 1 : 0);
    }

    private void MouseModeRadio_Click(object sender, RoutedEventArgs e)
    {
        if (_clientHandle == IntPtr.Zero || sender is not RadioButton { Tag: string tagStr } ||
            !int.TryParse(tagStr, out int mode))
        {
            return;
        }

        _mouseMode = Math.Clamp(mode, 0, 2);
        _userPausedVirtualCapture = false;
        MouseModeControllerRadio.IsChecked = _mouseMode == 0;
        MouseModeVirtualRadio.IsChecked = _mouseMode == 1;
        MouseModeSmartRadio.IsChecked = _mouseMode == 2;
        UpdateMouseModeStatusLabel();

        App.Engine.SettingsService.Settings.PreferredMouseMode = (RemoteMouseMode)_mouseMode;
        App.Engine.SettingsService.SaveSettings();

        NativeMethods.Osmium_ClientSetMouseMode(_clientHandle, _mouseMode, _mouseMode == 0 ? 1 : 0);
        SessionMenuFlyout.Hide();
        DispatcherQueue.TryEnqueue(() =>
        {
            VideoInputSurface.Focus(FocusState.Programmatic);
            SyncKeyboardHookAndCursorCapture();
        });
    }

    private void ReleaseCursorLockItem_Click(object sender, RoutedEventArgs e)
    {
        SessionMenuFlyout.Hide();
        ReleaseCursorCapture(manualPause: true);
    }

    private void VirtualAudioToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi || _clientHandle == IntPtr.Zero) return;
        _virtualSpeakerEnabled = VirtualSpeakerToggle.IsOn;
        _virtualMicEnabled = VirtualMicToggle.IsOn;
        NativeMethods.Osmium_ClientSetVirtualAudio(
            _clientHandle,
            _virtualSpeakerEnabled ? 1 : 0,
            _virtualMicEnabled ? 1 : 0,
            string.IsNullOrWhiteSpace(_selectedLocalMicId) ? null : _selectedLocalMicId);
    }

    private void LocalMicCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingUi) return;
        if (LocalMicCombo.SelectedItem is ComboBoxItem { Tag: string id })
        {
            _selectedLocalMicId = id;
            App.Engine.SettingsService.Settings.PreferredLocalMicDeviceId = id;
            App.Engine.SettingsService.SaveSettings();

            if (_virtualMicEnabled && _clientHandle != IntPtr.Zero)
            {
                NativeMethods.Osmium_ClientSetVirtualAudio(
                    _clientHandle,
                    _virtualSpeakerEnabled ? 1 : 0,
                    1,
                    id);
            }
        }
    }

    private void SendCtrlAltDel_Click(object sender, RoutedEventArgs e)
    {
        if (_clientHandle != IntPtr.Zero)
        {
            NativeMethods.Osmium_ClientSendSystemAction(_clientHandle, 1);
            SessionMenuFlyout.Hide();
        }
    }

    private void OpenTaskManager_Click(object sender, RoutedEventArgs e)
    {
        if (_clientHandle != IntPtr.Zero)
        {
            NativeMethods.Osmium_ClientSendSystemAction(_clientHandle, 2);
            SessionMenuFlyout.Hide();
        }
    }

    private void RemoteInstallDrivers_Click(object sender, RoutedEventArgs e)
    {
        if (_clientHandle == IntPtr.Zero) return;
        NativeMethods.Osmium_ClientSendSystemAction(_clientHandle, 4);
        SessionMenuFlyout.Hide();
        ShowTopToast(LocalizationService.GetString("Session_ToastDriverInstallSent"));
    }

    private void RemoteUninstallDrivers_Click(object sender, RoutedEventArgs e)
    {
        if (_clientHandle == IntPtr.Zero) return;
        NativeMethods.Osmium_ClientSendSystemAction(_clientHandle, 5);
        SessionMenuFlyout.Hide();
        ShowTopToast(LocalizationService.GetString("Session_ToastDriverUninstallSent"));
    }

    private void OsdToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi) return;
        var s = App.Engine.SettingsService.Settings;
        s.ShowPerformanceOverlay = OsdToggleSwitch.IsOn;
        App.Engine.SettingsService.SaveSettings();
        ApplyOsdVisibilityAndDetailLayout();
    }

    private void OsdDetailCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingUi) return;
        if (OsdDetailCombo.SelectedItem is ComboBoxItem { Tag: string tagStr } &&
            int.TryParse(tagStr, out int level))
        {
            var detail = (OsdDetailLevel)Math.Clamp(level, 0, 2);
            App.Engine.SettingsService.Settings.OsdDetailLevel = detail;
            App.Engine.SettingsService.SaveSettings();
            ApplyOsdVisibilityAndDetailLayout();
        }
    }

    private void DebugInfoExpander_Expanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        DebugInfoBodyText.Text = BuildDebugSnapshotText();
    }

    private void CopyDebugInfo_Click(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(BuildDebugSnapshotText());
        Clipboard.SetContent(dp);
        ShowTopToast(LocalizationService.GetString("Session_ToastDebugCopied"));
    }

    private void FullscreenBtn_Click(object sender, RoutedEventArgs e)
    {
        SessionMenuFlyout.Hide();
        SetFullscreenState(!_isFullscreen);
    }

    private void SetFullscreenState(bool fullscreen)
    {
        if (_appWindow == null || _isFullscreen == fullscreen) return;
        _isFullscreen = fullscreen;
        FullscreenBtnText.Text =
            LocalizationService.GetString(fullscreen ? "Session_FullscreenExit" : "Session_FullscreenEnter");

        if (fullscreen)
        {
            SetTitleBar(null);
            _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            TopBarRow.Height = new GridLength(0);
            Grid.SetRow(AppTitleBar, 1);
            AppTitleBar.VerticalAlignment = VerticalAlignment.Top;
            AppTitleBar.Padding = new Thickness(12, 0, 12, 0);
            AppTitleBar.Visibility = Visibility.Collapsed;
            FullscreenTopHoverTrigger.Visibility = Visibility.Visible;
        }
        else
        {
            FullscreenTopHoverTrigger.Visibility = Visibility.Collapsed;
            _fullscreenBarHideTimer.Stop();
            _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            TopBarRow.Height = new GridLength(46);
            Grid.SetRow(AppTitleBar, 0);
            AppTitleBar.VerticalAlignment = VerticalAlignment.Stretch;
            AppTitleBar.Visibility = Visibility.Visible;
            SetTitleBar(AppTitleBar);
        }

        ApplyTitleBarChromeTheme();
        UpdateTabSelectionStyles();
        UpdateTitleBarCaptionMetrics();
        SyncKeyboardHookAndCursorCapture();
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateTitleBarCaptionMetrics();
            UpdateTitleBarPassthroughRegions();
            UpdateCachedVideoSurfaceBounds();
        });
    }

    private void FullscreenTopHoverTrigger_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_isFullscreen) return;
        _pointerInTopBar = true;
        _fullscreenBarHideTimer.Stop();
        AppTitleBar.Visibility = Visibility.Visible;
        UpdateTitleBarPassthroughRegions();
    }

    private void AppTitleBar_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerInTopBar = true;
        _fullscreenBarHideTimer.Stop();
    }

    private void AppTitleBar_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInTopBar = false;
        if (_isFullscreen && _openFlyoutCount == 0)
        {
            _fullscreenBarHideTimer.Stop();
            _fullscreenBarHideTimer.Start();
        }
    }

    private void SettingsFlyout_Opened(object? sender, object e)
    {
        _openFlyoutCount++;
        ReleaseCursorCapture(manualPause: false);
        SyncKeyboardHookAndCursorCapture();
    }

    private void SessionMenuFlyout_Opened(object? sender, object e)
    {
        SettingsFlyout_Opened(sender, e);
        RefreshLocalMicList();
        if (DebugInfoExpander.IsExpanded)
        {
            DebugInfoBodyText.Text = BuildDebugSnapshotText();
        }
    }

    private void SettingsFlyout_Closed(object? sender, object e)
    {
        if (_openFlyoutCount > 0) _openFlyoutCount--;
        if (_openFlyoutCount == 0)
        {
            if (_hwnd != IntPtr.Zero && GetForegroundWindow() == _hwnd)
            {
                _isWindowFocused = true;
            }

            if (_isFullscreen && !_pointerInTopBar)
            {
                _fullscreenBarHideTimer.Start();
            }

            VideoInputSurface.Focus(FocusState.Programmatic);
            SyncKeyboardHookAndCursorCapture();
        }
    }

    private void DisconnectBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}