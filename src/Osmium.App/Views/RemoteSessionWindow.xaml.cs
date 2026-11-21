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
    private const int GwlWndProc = -4;
    private const int GclpHCursor = -12;
    private const int IdcArrow = 32512;
    private const uint GaRoot = 2;
    private const uint GaRootOwner = 3;
    private const uint WmInput = 0x00FF;
    private const uint WmSetCursor = 0x0020;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmNcLButtonDblClk = 0x00A3;
    private const uint WmSysCommand = 0x0112;
    private const uint ScMove = 0xF010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint WmMouseMove = 0x0200;
    private const uint WmParentNotify = 0x0210;
    private const uint WmPointerUpdate = 0x0245;
    private const uint WmPointerEnter = 0x0249;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;
    private const ushort MouseMoveAbsolute = 0x0001;
    private const int WhKeyboardLl = 13;
    private const uint MapVkVscToVkEx = 3;

    private sealed class SubclassedWindowEntry
    {
        public required WeakReference<RemoteSessionWindow> Window
        {
            get;
            init;
        }

        public required IntPtr PrevWndProc
        {
            get;
            init;
        }
    }

    private static readonly ConcurrentDictionary<IntPtr, SubclassedWindowEntry> SubclassedWindows = new();

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private readonly RemoteSessionLaunchArgs _launchArgs;
    private readonly Stopwatch _sessionClock = Stopwatch.StartNew();
    private readonly DispatcherQueueTimer _uiTimer;
    private readonly DispatcherQueueTimer _gamepadTimer;
    private readonly DispatcherQueueTimer _toastHideTimer;
    private readonly DispatcherQueueTimer _fullscreenBarHideTimer;

    private IntPtr _hwnd;
    private AppWindow? _appWindow;
    private IntPtr _clientHandle = IntPtr.Zero;
    private volatile bool _isClosed;
    private volatile bool _remoteSessionEnded;
    private volatile bool _isWindowFocused = true;
    private bool _isFullscreen;
    private bool _isUpdatingUi;
    private volatile int _openFlyoutCount;
    private volatile bool _pointerInTopBar;

    private OsmiumClientStartedDelegate? _startedDel;
    private OsmiumClientStatsDelegate? _statsDel;
    private OsmiumClientEndedDelegate? _endedDel;
    private OsmiumClientStatusJsonDelegate? _statusJsonDel;
    private OsmiumLogDelegate? _logDel;

    private readonly object _subclassLock = new();
    private readonly List<IntPtr> _ownedSubclassedHwnds = [];
    private readonly Dictionary<IntPtr, IntPtr> _savedClassCursors = new();
    private WndProcDelegate? _wndProcDel;
    private EnumWindowsProc? _enumChildDel;
    private IntPtr _wndProcPtr = IntPtr.Zero;
    private bool _rawInputRegistered;
    private IntPtr _blankCursor = IntPtr.Zero;
    private InputCursor? _winUiBlankCursor;

    private LowLevelKeyboardProc? _llKeyProcDel;
    private IntPtr _llKeyHook = IntPtr.Zero;
    private readonly Dictionary<(ushort Vk, ushort Scan, bool Ext), bool> _hookSwallowedKeys = new();
    private readonly HashSet<(ushort Vk, ushort Scan, bool Ext)> _shortcutConsumedKeys = new();
    private volatile bool _llLCtrlDown;
    private volatile bool _llRCtrlDown;
    private volatile bool _llLAltDown;
    private volatile bool _llRAltDown;
    private volatile bool _llLShiftDown;
    private volatile bool _llRShiftDown;

    private volatile bool _isCursorCaptured;
    private bool _cursorHidden;
    private bool _userPausedVirtualCapture;
    private bool _hasPrevRawAbs;
    private int _prevRawAbsX;
    private int _prevRawAbsY;

    private readonly object _mousePacerLock = new();
    private readonly ManualResetEventSlim _mousePacerWake = new(false);
    private volatile bool _mousePacerStop;
    private Thread? _mousePacerThread;
    private bool _timerPeriodActive;
    private double _pendingRelX;
    private double _pendingRelY;
    private bool _hasPendingAbsMove;
    private long _lastMouseSendTicks;
    private float _lastSentNormX = -1f;
    private float _lastSentNormY = -1f;
    private volatile int _surfLeftPx;
    private volatile int _surfTopPx;
    private volatile int _surfWidthPx;
    private volatile int _surfHeightPx;
    private volatile int _cachedStreamWidth;
    private volatile int _cachedStreamHeight;

    private volatile int _targetMouseSamplingHz = 80;

    private sealed class DisplayTabVisual
    {
        public int DisplayIndex
        {
            get;
            init;
        }

        public Grid RootGrid
        {
            get;
            init;
        } = null!;

        public ShapesPath FillPath
        {
            get;
            init;
        } = null!;

        public ShapesPath StrokePath
        {
            get;
            init;
        } = null!;

        public Panel ContentPanel
        {
            get;
            init;
        } = null!;

        public FontIcon Icon
        {
            get;
            init;
        } = null!;

        public TextBlock TitleBlock
        {
            get;
            init;
        } = null!;

        public bool IsHovered
        {
            get;
            set;
        }
    }

    private OsmiumClientStats _latestStats;
    private HostSessionStatusDto _hostStatus = new();
    private List<HostStatusDisplayDto> _renderedTabs = [];
    private readonly List<DisplayTabVisual> _tabVisuals = [];
    private List<OsmiumAudioDeviceInfo> _localMicDevices = [];
    private bool _firstVideoFrameSeen;
    private bool _pipelineInitFailed;
    private string? _endedReasonMsg;
    private int _lastScaleNativeW = 1920;
    private int _lastScaleNativeH = 1080;
    private bool _hasAdaptedToStreamAspect;
    private int _lastAutoSizedHostW;
    private int _lastAutoSizedHostH;
    private int _lastSwapChainPxW;
    private int _lastSwapChainPxH;
    private float _lastSwapChainScale;
    private Storyboard? _toastStoryboard;

    private int _targetScalePercent;
    private int _targetCustomWidth;
    private int _targetCustomHeight;
    private int _currentFps;
    private int _baseBitrateKbps;
    private bool _enableHdr;
    private bool _hasReceivedHostHdrStatus;
    private bool _hostHdrAvailable;
    private bool _enableYuv444;
    private bool _enableVsync;
    private int _mouseMode;
    private bool _virtualSpeakerEnabled;
    private bool _virtualMicEnabled;
    private string _selectedLocalMicId = string.Empty;

    public RemoteSessionWindow(RemoteSessionLaunchArgs launchArgs)
    {
        _launchArgs = launchArgs;
        InitializeComponent();

        var s = App.Engine.SettingsService.Settings;
        _currentFps = Math.Clamp(launchArgs.InitialConfig.Fps > 0 ? launchArgs.InitialConfig.Fps : 60, 15, 240);
        RefreshTargetMouseSamplingHz();
        _baseBitrateKbps =
            Math.Clamp(launchArgs.InitialConfig.BitrateKbps > 0 ? launchArgs.InitialConfig.BitrateKbps : 20000, 1500,
                500000);
        _enableHdr = launchArgs.InitialConfig.EnableHdr != 0;
        _enableYuv444 = launchArgs.InitialConfig.EnableYuv444 != 0;
        _enableVsync = launchArgs.InitialConfig.EnableVsync != 0;
        _mouseMode = launchArgs.InitialConfig.MouseMode;
        _virtualSpeakerEnabled = launchArgs.InitialConfig.EnableVirtualSpeaker != 0;
        _virtualMicEnabled = launchArgs.InitialConfig.EnableVirtualMic != 0;
        _selectedLocalMicId = launchArgs.InitialConfig.LocalMicDeviceId ?? string.Empty;
        _targetScalePercent = Math.Clamp(s.ClientScalePercent, 25, 100);
        _targetCustomWidth = launchArgs.InitialConfig.TargetWidth;
        _targetCustomHeight = launchArgs.InitialConfig.TargetHeight;

        _uiTimer = DispatcherQueue.CreateTimer();
        _uiTimer.Interval = TimeSpan.FromMilliseconds(250);
        _uiTimer.Tick += UiTimer_Tick;

        _gamepadTimer = DispatcherQueue.CreateTimer();
        _gamepadTimer.Interval = TimeSpan.FromMilliseconds(16);
        _gamepadTimer.Tick += (_, _) =>
        {
            if (!_isClosed && !_remoteSessionEnded && _clientHandle != IntPtr.Zero)
            {
                NativeMethods.Osmium_ClientPollInput(_clientHandle,
                    (_isWindowFocused && !IsTextInputFocused()) ? 1 : 0);
            }
        };

        _toastHideTimer = DispatcherQueue.CreateTimer();
        _toastHideTimer.Interval = TimeSpan.FromSeconds(3.2);
        _toastHideTimer.Tick += (_, _) =>
        {
            _toastHideTimer.Stop();
            HideTopToastAnimated();
        };

        _fullscreenBarHideTimer = DispatcherQueue.CreateTimer();
        _fullscreenBarHideTimer.Interval = TimeSpan.FromMilliseconds(650);
        _fullscreenBarHideTimer.Tick += (_, _) =>
        {
            _fullscreenBarHideTimer.Stop();
            if (_isFullscreen && !_pointerInTopBar && _openFlyoutCount == 0)
            {
                AppTitleBar.Visibility = Visibility.Collapsed;
                UpdateTitleBarPassthroughRegions();
            }
        };

        InitializeOsdShadowLayer();
        InitializeChromeAndTheme();
        InitializeUiDefaults();

        Activated += RemoteSessionWindow_Activated;
        SizeChanged += RemoteSessionWindow_SizeChanged;
        Closed += RemoteSessionWindow_Closed;
        VideoStageGrid.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                VideoStageGrid.Clip = new RectangleGeometry
                {
                    Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
                };
            }
        };
        VideoSwapChainPanel.SizeChanged += VideoSwapChainPanel_SizeChanged;
        VideoSwapChainPanel.CompositionScaleChanged += VideoSwapChainPanel_CompositionScaleChanged;
        VideoInputSurface.SizeChanged += (_, _) => UpdateCachedVideoSurfaceBounds();
        BrandChipHost.SizeChanged += (_, _) => UpdateBrandChipGeometry();
        RootSessionGrid.Loaded += RootSessionGrid_Loaded;
        RootSessionGrid.PreviewKeyDown += RootSessionGrid_PreviewKeyDown;
        RootSessionGrid.PreviewKeyUp += RootSessionGrid_PreviewKeyUp;
        RootSessionGrid.ActualThemeChanged += (_, _) =>
        {
            ImmersiveWindowHelper.ApplyImmersiveFrame(this, RootSessionGrid.ActualTheme == ElementTheme.Dark);
            ApplyTitleBarChromeTheme();
            UpdateTabSelectionStyles();
        };
        App.Engine.Coordinator.ClientSessionEnded += OnCoordinatorClientSessionEnded;
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        if (_isClosed) return;
        DispatcherQueue.TryEnqueue(ApplyLocalization);
    }

    private void InitializeChromeAndTheme()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            RootSessionGrid.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ImmersiveWindowHelper.EnsureTopInsetFixAttached(this);

        _hwnd = WindowNative.GetWindowHandle(this);
        var winId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(winId);
        _appWindow.Changed += (_, a) =>
        {
            if (!_isClosed && (a.DidSizeChange || a.DidPresenterChange))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateTitleBarCaptionMetrics();
                    UpdateTitleBarPassthroughRegions();
                });
            }
        };

        string hostTitle = string.IsNullOrWhiteSpace(_launchArgs.HostName)
            ? DeviceIdentity.FormatDeviceId(_launchArgs.TargetDeviceId)
            : _launchArgs.HostName;
        Title = $"Osmium - {hostTitle}";
        string chipTitle = !string.IsNullOrWhiteSpace(_launchArgs.TargetDeviceName)
            ? _launchArgs.TargetDeviceName
            : hostTitle;
        RemoteHostTitleText.Text = string.IsNullOrWhiteSpace(chipTitle)
            ? LocalizationService.GetString("Session_DefaultHostTitle")
            : chipTitle;

        if (_appWindow.Presenter is OverlappedPresenter op)
        {
            op.PreferredMinimumWidth = 360;
            op.PreferredMinimumHeight = 320;
        }

        int initAspectW = _launchArgs.InitialConfig.TargetWidth > 0 ? _launchArgs.InitialConfig.TargetWidth : 1280;
        int initAspectH = _launchArgs.InitialConfig.TargetHeight > 0 ? _launchArgs.InitialConfig.TargetHeight : 720;
        TryAdaptWindowSizeToHostAspect(initAspectW, initAspectH, recenter: true, markAdapted: false);

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "osmium.ico");
        if (File.Exists(iconPath))
        {
            _appWindow.SetIcon(iconPath);
        }

        var pref = App.Engine.SettingsService.Settings.UiTheme;
        RootSessionGrid.RequestedTheme = pref switch
        {
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        bool isDark = pref == AppThemePreference.Dark ||
                      (pref == AppThemePreference.System &&
                       Application.Current.RequestedTheme == ApplicationTheme.Dark);
        ImmersiveWindowHelper.ApplyImmersiveFrame(this, isDark);
        ApplyTitleBarChromeTheme();

        PopupCornerHelper.Install(this, RootSessionGrid);

        var winMode = App.Engine.SettingsService.Settings.WindowMode;
        if (winMode == StreamWindowMode.Fullscreen)
        {
            SetFullscreenState(true);
        }
        else if (winMode == StreamWindowMode.BorderlessWindowed && _appWindow.Presenter is OverlappedPresenter maxOp)
        {
            maxOp.Maximize();
        }
    }

    private void InitializeUiDefaults()
    {
        _isUpdatingUi = true;
        try
        {
            HdrToggle.IsEnabled = false;
            HdrToggle.IsOn = false;
            HdrHostHintText.Visibility = Visibility.Visible;
            Yuv444Toggle.IsOn = _enableYuv444;
            VsyncToggle.IsOn = _enableVsync;

            VirtualSpeakerToggle.IsOn = _virtualSpeakerEnabled;
            VirtualMicToggle.IsOn = _virtualMicEnabled;

            MouseModeControllerRadio.IsChecked = _mouseMode == 0;
            MouseModeVirtualRadio.IsChecked = _mouseMode == 1;
            MouseModeSmartRadio.IsChecked = _mouseMode == 2;

            PopulateFpsCombo();
            RebuildScaleOptions(1920, 1080);

            var s = App.Engine.SettingsService.Settings;
            OsdToggleSwitch.IsOn = s.ShowPerformanceOverlay;
            OsdDetailCombo.SelectedIndex = Math.Clamp((int)s.OsdDetailLevel, 0, 2);
            ApplyOsdVisibilityAndDetailLayout();

            ApplyLocalization();
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void ApplyLocalization()
    {
        if (_isClosed) return;

        bool prev = _isUpdatingUi;
        _isUpdatingUi = true;
        try
        {
            if (string.IsNullOrWhiteSpace(_launchArgs.HostName) &&
                string.IsNullOrWhiteSpace(_launchArgs.TargetDeviceId))
            {
                RemoteHostTitleText.Text = LocalizationService.GetString("Session_DefaultHostTitle");
            }

            if (_remoteSessionEnded)
            {
                WaitingVideoText.Text = BuildSessionEndedText(_endedReasonMsg);
            }
            else if (_pipelineInitFailed)
            {
                WaitingVideoText.Text = LocalizationService.GetString("Session_PipelineInitFailed");
            }
            else if (!_firstVideoFrameSeen)
            {
                WaitingVideoText.Text = LocalizationService.GetString("Session_WaitingVideo");
            }

            ToolTipService.SetToolTip(SessionMenuButton, LocalizationService.GetString("Session_MenuTooltip"));
            SessionMenuTitleText.Text = LocalizationService.GetString("Session_MenuTitle");
            SessionViewResTitleText.Text = LocalizationService.GetString("Session_ViewResTitle");
            HostNativeResInfoText.Text = _hostStatus.NativeWidth > 0 && _hostStatus.NativeHeight > 0
                ? LocalizationService.Format("Session_NativeResFmt", _hostStatus.NativeWidth, _hostStatus.NativeHeight)
                : LocalizationService.GetString("Session_NativeResUnknown");

            SessionFpsColorTitleText.Text = LocalizationService.GetString("Session_FpsColorTitle");
            string onLabel = LocalizationService.GetString("Common_On");
            string offLabel = LocalizationService.GetString("Common_Off");

            foreach (var toggle in new[]
                     {
                         HdrToggle, Yuv444Toggle, VsyncToggle, VirtualSpeakerToggle, VirtualMicToggle, OsdToggleSwitch
                     })
            {
                toggle.OnContent = onLabel;
                toggle.OffContent = offLabel;
            }

            HdrToggle.Header = LocalizationService.GetString("Session_HdrHeader");
            HdrHostHintText.Text = LocalizationService.GetString("Session_HdrRequiresHostHint");
            Yuv444Toggle.Header = LocalizationService.GetString("Session_Yuv444Header");
            VsyncToggle.Header = LocalizationService.GetString("Session_VsyncHeader");

            SessionMouseModeTitleText.Text = LocalizationService.GetString("Session_MouseModeTitle");
            MouseModeControllerRadio.Content = LocalizationService.GetString("Session_UseControllerMouse");
            MouseModeVirtualRadio.Content = LocalizationService.GetString("Session_UseVirtualMouse");
            MouseModeSmartRadio.Content = LocalizationService.GetString("Session_SmartMode");
            ReleaseCursorLockBtn.Content = LocalizationService.GetString("Session_ReleaseCursorLock");
            UpdateMouseModeStatusLabel();

            SessionAudioTitleText.Text = LocalizationService.GetString("Session_AudioTitle");
            VirtualSpeakerToggle.Header = LocalizationService.GetString("Session_VirtualSpeaker");
            VirtualMicToggle.Header = LocalizationService.GetString("Session_VirtualMic");
            LocalMicCombo.Header = LocalizationService.GetString("Session_LocalMicHeader");

            SessionDriversTitleText.Text = LocalizationService.GetString("Session_DriversTitle");
            UpdateHostDriverStatusUi();

            SessionOsdTitleText.Text = LocalizationService.GetString("Session_OsdTitle");
            OsdToggleSwitch.Header = LocalizationService.GetString("Session_OsdToggleHeader");
            OsdDetailCombo.Header = LocalizationService.GetString("Session_OsdDetailHeader");
            OsdDetailCompactItem.Content = LocalizationService.GetString("Session_OsdCompact");
            OsdDetailStandardItem.Content = LocalizationService.GetString("Session_OsdStandard");
            OsdDetailDetailedItem.Content = LocalizationService.GetString("Session_OsdDetailed");

            SessionSysControlTitleText.Text = LocalizationService.GetString("Session_SysControlTitle");
            HostLockedPill.Text = LocalizationService.GetString("Session_ScreenLocked");
            TaskManagerBtn.Content = LocalizationService.GetString("Session_TaskManagerBtn");

            SessionWindowControlTitleText.Text = LocalizationService.GetString("Session_WindowControlTitle");
            ToolTipService.SetToolTip(FullscreenBtn, LocalizationService.GetString("Session_ToggleFullscreenTooltip"));
            FullscreenBtnText.Text =
                LocalizationService.GetString(_isFullscreen ? "Session_FullscreenExit" : "Session_FullscreenEnter");
            DisconnectBtn.Content = LocalizationService.GetString("Session_DisconnectBtn");
            ToolTipService.SetToolTip(DisconnectBtn, LocalizationService.GetString("Session_DisconnectTooltip"));

            SessionDebugInfoTitleText.Text = LocalizationService.GetString("Session_DebugInfoTitle");
            CopyDebugInfoBtn.Content = LocalizationService.GetString("Session_CopyBtn");

            RebuildScaleOptions(_lastScaleNativeW, _lastScaleNativeH);
            RefreshLocalMicList();
            _renderedTabs.Clear();
            RebuildDisplayTabsIfChanged();
            UpdateOsdDisplay();
            if (SessionMenuFlyout.IsOpen && DebugInfoExpander.IsExpanded)
            {
                DebugInfoBodyText.Text = BuildDebugSnapshotText();
            }
        }
        finally
        {
            _isUpdatingUi = prev;
        }
    }

    private void UpdateMouseModeStatusLabel()
    {
        string key = _mouseMode switch
        {
            1 => _isCursorCaptured
                ? "Session_MouseVirtual"
                : (_userPausedVirtualCapture ? "Session_MouseVirtualPaused" : "Session_MouseVirtual"),
            2 => _isCursorCaptured ? "Session_MouseSmartLocked" : "Session_MouseSmart",
            _ => "Session_MouseController"
        };
        string newText = LocalizationService.GetString(key);
        if (MouseModeStatusText.Text != newText)
        {
            MouseModeStatusText.Text = newText;
        }
    }

    private void RootSessionGrid_Loaded(object sender, RoutedEventArgs e)
    {
        AttachInputWndProc();
        UpdateCachedVideoSurfaceBounds();
        StartEmbeddedNativeSession();
        StartMousePacer();
        ApplyTitleBarChromeTheme();
        UpdateBrandChipGeometry();
        UpdateTitleBarPassthroughRegions();
        if (_lastAutoSizedHostW > 0 && _lastAutoSizedHostH > 0)
        {
            DispatcherQueue.TryEnqueue(() =>
                VerifyAndTrimVideoViewportAspect(_lastAutoSizedHostW, _lastAutoSizedHostH,
                    recenter: !_hasAdaptedToStreamAspect));
        }

        _uiTimer.Start();
        _gamepadTimer.Start();
        SyncKeyboardHookAndCursorCapture();
    }

    private void StartEmbeddedNativeSession()
    {
        if (_clientHandle != IntPtr.Zero || _isClosed) return;

        float scaleX = VideoSwapChainPanel.CompositionScaleX > 0.1f
            ? VideoSwapChainPanel.CompositionScaleX
            : (float)(RootSessionGrid.XamlRoot?.RasterizationScale ?? 1.0);
        double dipW = Math.Max(320.0, VideoSwapChainPanel.ActualWidth);
        double dipH = Math.Max(200.0, VideoSwapChainPanel.ActualHeight);
        int initW = Math.Max(64, (int)Math.Ceiling(dipW * scaleX));
        int initH = Math.Max(64, (int)Math.Ceiling(dipH * scaleX));
        float exactScale = (float)(initW / dipW);

        IntPtr panelUnk = ((IWinRTObject)VideoSwapChainPanel).NativeObject.ThisPtr;

        _startedDel = (w, h, codec, dec) =>
        {
            if (_isClosed || _remoteSessionEnded) return;
            App.Engine.Logging.Info($"Embedded stream active: {w}x{h} {codec} via {dec}");
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosed || _remoteSessionEnded) return;
                _firstVideoFrameSeen = true;
                if (w > 0 && h > 0)
                {
                    _cachedStreamWidth = w;
                    _cachedStreamHeight = h;
                    TryAdaptWindowSizeToHostAspect(w, h, recenter: !_hasAdaptedToStreamAspect, markAdapted: true);
                }

                WaitingProgressRing.IsActive = false;
                WaitingProgressRing.Visibility = Visibility.Collapsed;
                WaitingVideoOverlay.Visibility = Visibility.Collapsed;
            });
        };

        _statsDel = (in OsmiumClientStats stats) =>
        {
            if (_isClosed || _remoteSessionEnded) return;
            OsmiumClientStats copy = stats;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosed || _remoteSessionEnded) return;
                _latestStats = copy;
                if (copy.VideoWidth > 0 && copy.VideoHeight > 0)
                {
                    _cachedStreamWidth = copy.VideoWidth;
                    _cachedStreamHeight = copy.VideoHeight;
                    if (!_hasAdaptedToStreamAspect || copy.VideoWidth != _lastAutoSizedHostW ||
                        copy.VideoHeight != _lastAutoSizedHostH)
                    {
                        TryAdaptWindowSizeToHostAspect(copy.VideoWidth, copy.VideoHeight,
                            recenter: !_hasAdaptedToStreamAspect, markAdapted: true);
                    }
                }

                RefreshTargetMouseSamplingHz();
                if (!_firstVideoFrameSeen && copy.RenderedFrames > 0)
                {
                    _firstVideoFrameSeen = true;
                    WaitingProgressRing.IsActive = false;
                    WaitingProgressRing.Visibility = Visibility.Collapsed;
                    WaitingVideoOverlay.Visibility = Visibility.Collapsed;
                }

                UpdateOsdDisplay();
            });
        };

        _endedDel = (code, msg) =>
        {
            if (_isClosed) return;
            App.Engine.Logging.Info($"Embedded stream ended ({code}): {msg}");
            DispatcherQueue.TryEnqueue(() => HandleRemoteSessionEnded(msg));
        };

        _statusJsonDel = json =>
        {
            if (_isClosed || _remoteSessionEnded || string.IsNullOrWhiteSpace(json)) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosed || _remoteSessionEnded) return;
                ApplyHostStatusJson(json);
            });
        };

        _logDel = msg => App.Engine.Logging.RawNative(msg);

        var cfg = _launchArgs.InitialConfig;
        cfg.ShowPerformanceOverlay = 0;

        var callbacks = new OsmiumClientCallbacks
        {
            OnStarted = Marshal.GetFunctionPointerForDelegate(_startedDel),
            OnStatsUpdated = Marshal.GetFunctionPointerForDelegate(_statsDel),
            OnEnded = Marshal.GetFunctionPointerForDelegate(_endedDel),
            OnLog = Marshal.GetFunctionPointerForDelegate(_logDel),
            OnStatusJson = Marshal.GetFunctionPointerForDelegate(_statusJsonDel)
        };

        if (NativeMethods.Osmium_StartClientEmbedded(
                _launchArgs.Transport,
                panelUnk,
                initW,
                initH,
                exactScale,
                in cfg,
                in callbacks,
                out IntPtr handle) == 0 || handle == IntPtr.Zero)
        {
            _pipelineInitFailed = true;
            WaitingProgressRing.IsActive = false;
            WaitingProgressRing.Visibility = Visibility.Collapsed;
            WaitingVideoText.Text = LocalizationService.GetString("Session_PipelineInitFailed");
            return;
        }

        _clientHandle = handle;
        DispatcherQueue.TryEnqueue(UpdateVideoSwapChainSize);
        SyncKeyboardHookAndCursorCapture();

        if (_virtualSpeakerEnabled || _virtualMicEnabled)
        {
            NativeMethods.Osmium_ClientSetVirtualAudio(
                _clientHandle,
                _virtualSpeakerEnabled ? 1 : 0,
                _virtualMicEnabled ? 1 : 0,
                string.IsNullOrWhiteSpace(_selectedLocalMicId) ? null : _selectedLocalMicId);
        }
    }

    private void ApplyHostStatusJson(string json)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<HostSessionStatusDto>(json);
            if (dto == null) return;

            bool nativeSizeChanged = dto.NativeWidth != _hostStatus.NativeWidth ||
                                     dto.NativeHeight != _hostStatus.NativeHeight;
            bool lastActionChanged = !string.IsNullOrEmpty(dto.LastAction) &&
                                     dto.LastAction != _hostStatus.LastAction;

            _hostStatus = dto;
            if (dto.StreamWidth > 0 && dto.StreamHeight > 0 && _cachedStreamWidth <= 0)
            {
                _cachedStreamWidth = dto.StreamWidth;
                _cachedStreamHeight = dto.StreamHeight;
            }

            RefreshTargetMouseSamplingHz();
            RebuildDisplayTabsIfChanged();

            if (nativeSizeChanged && dto.NativeWidth > 0 && dto.NativeHeight > 0)
            {
                if (_targetCustomWidth <= 0 && _targetCustomHeight <= 0 &&
                    _targetScalePercent > 0 && _targetScalePercent < 100)
                {
                    _targetCustomWidth = Math.Max(320, ((int)Math.Round(dto.NativeWidth * _targetScalePercent / 100.0)) & ~1);
                    _targetCustomHeight = Math.Max(180, ((int)Math.Round(dto.NativeHeight * _targetScalePercent / 100.0)) & ~1);
                    SendUpdatedVideoParams();
                }

                RebuildScaleOptions(dto.NativeWidth, dto.NativeHeight);
                if (dto.StreamWidth > 0 && dto.StreamHeight > 0)
                {
                    TryAdaptWindowSizeToHostAspect(dto.StreamWidth, dto.StreamHeight,
                        recenter: !_hasAdaptedToStreamAspect, markAdapted: true);
                }
            }

            HostLockedPill.Visibility = dto.ScreenLocked ? Visibility.Visible : Visibility.Collapsed;
            SyncHdrAvailabilityFromHostStatus(dto.HostHdrEnabled);
            UpdateHostDriverStatusUi();

            SyncKeyboardHookAndCursorCapture();

            if (lastActionChanged)
            {
                string? noticeKey = dto.LastAction switch
                {
                    "SAS_SENT" => "Session_NoticeSasSent",
                    "SAS_NEEDS_ADMIN" => "Session_NoticeSasNeedsAdmin",
                    "TASKMGR_OPENED" => "Session_NoticeTaskMgrOpened",
                    "DRIVER_INSTALLED" => "Session_NoticeDriverInstalled",
                    "DRIVER_INSTALL_FAILED" => "Session_NoticeDriverInstallFailed",
                    "DRIVER_UNINSTALLED" => "Session_NoticeDriverUninstalled",
                    "DRIVER_UNINSTALL_FAILED" => "Session_NoticeDriverUninstallFailed",
                    "DRIVER_MGMT_DISABLED" => "Session_NoticeDriverMgmtDisabled",
                    "DRIVER_MGMT_DENIED" => "Session_NoticeDriverMgmtDenied",
                    _ => null
                };
                if (!string.IsNullOrEmpty(noticeKey)) ShowTopToast(LocalizationService.GetString(noticeKey));
            }
        }
        catch (Exception ex)
        {
            App.Engine.Logging.Warn($"Parse HostStatusJson failed: {ex.Message}");
        }
    }

    private void SyncHdrAvailabilityFromHostStatus(bool hostHdrEnabled)
    {
        bool firstSync = !_hasReceivedHostHdrStatus;
        bool wasAvailable = _hostHdrAvailable;
        _hasReceivedHostHdrStatus = true;
        _hostHdrAvailable = hostHdrEnabled;

        if (!hostHdrEnabled)
        {
            HdrToggle.IsEnabled = false;
            HdrHostHintText.Visibility = Visibility.Visible;

            bool needNotifyReset = _enableHdr || HdrToggle.IsOn;
            _enableHdr = false;
            _isUpdatingUi = true;
            try
            {
                HdrToggle.IsOn = false;
            }
            finally
            {
                _isUpdatingUi = false;
            }

            if (needNotifyReset)
            {
                SendUpdatedVideoParams();
                if (!firstSync && wasAvailable)
                {
                    ShowTopToast(LocalizationService.GetString("Session_HdrRequiresHostHint"));
                }
            }

            return;
        }

        HdrToggle.IsEnabled = true;
        HdrHostHintText.Visibility = Visibility.Collapsed;

        if (firstSync)
        {
            _isUpdatingUi = true;
            try
            {
                HdrToggle.IsOn = _enableHdr;
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }
    }

    private void UpdateHostDriverStatusUi()
    {
        var dto = _hostStatus;
        if (dto.Displays.Count == 0 && !dto.HasUsbIpDriver && string.IsNullOrEmpty(dto.LastAction))
        {
            HostDriverStatusText.Text = LocalizationService.GetString("Session_DriverChecking");
        }
        else
        {
            string usbText =
                LocalizationService.GetString(
                    dto.HasUsbIpDriver ? "Session_DriverUsbReady" : "Session_DriverUsbMissing");
            string policyNote = LocalizationService.GetString(dto.AllowRemoteDriverMgmt
                ? "Session_DriverPolicyEnabled"
                : "Session_DriverPolicyDisabled");
            HostDriverStatusText.Text = $"• {usbText}\n• {policyNote}";
        }

        RemoteInstallDriversBtn.IsEnabled = dto.AllowRemoteDriverMgmt;
        RemoteInstallDriversBtn.Content = LocalizationService.GetString(dto.HasUsbIpDriver
            ? "Session_RemoteReinstallDriver"
            : "Session_RemoteInstallDriver");
        RemoteUninstallDriversBtn.IsEnabled = dto.AllowRemoteDriverMgmt && dto.HasUsbIpDriver;
        RemoteUninstallDriversBtn.Content = LocalizationService.GetString("Session_RemoteUninstallDriver");
    }
}