using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Osmium.App.Helpers;
using Osmium.App.Services;
using Osmium.App.Views;
using Osmium.Core.Models;
using Osmium.Core.Network;

namespace Osmium.App;

public sealed partial class MainWindow : Window
{
    private string? _pendingSettingsTab;
    private string? _pendingAccountTab;
    private int _currentNavIndex = -1;
    private Storyboard? _navTransitionStoryboard;
    private IntPtr _hwnd;
    private AppWindow? _appWindow;
    private TrayIconHelper? _trayIcon;
    private TaskbarFlashHelper? _taskbarFlasher;
    private bool _forceExit;
    private bool _isHiddenInTray;
    private readonly Dictionary<string, NavigationViewItem> _peerDeviceNavItems = new(StringComparer.Ordinal);

    public event Action? AutoStartChangedFromTray;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            RootShellGrid.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ImmersiveWindowHelper.EnsureTopInsetFixAttached(this);

        Activated += (_, args) =>
        {
            ImmersiveWindowHelper.SyncContentBridges(this);
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                _taskbarFlasher?.NotifyWindowActivated(
                    _hwnd,
                    isPointerActivated: args.WindowActivationState == WindowActivationState.PointerActivated);
            }
        };
        SizeChanged += (_, _) => ImmersiveWindowHelper.SyncContentBridges(this);
        RootShellGrid.ActualThemeChanged += (_, _) =>
            ImmersiveWindowHelper.ApplyImmersiveFrame(this, RootShellGrid.ActualTheme == ElementTheme.Dark);
        RootShellGrid.AddHandler(
            UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _taskbarFlasher?.Clear(_hwnd)),
            handledEventsToo: true);

        _taskbarFlasher = new TaskbarFlashHelper();
        ConfigureWindowIconAndSize();
        ApplyTheme(App.Engine.SettingsService.Settings.UiTheme);
        ApplyLocalization();
        PopupCornerHelper.Install(this, RootShellGrid);
        ContentFrame.Navigated += (_, _) =>
            DispatcherQueue.TryEnqueue(() => PopupCornerHelper.AttachToTree(ContentFrame));

        App.Engine.Coordinator.ConfirmIncomingConnectionAsync = ConfirmIncomingConnectionOnUiAsync;
        App.Engine.Coordinator.ConfirmDriverActionAsync = ConfirmDriverActionOnUiAsync;
        App.Engine.Coordinator.HostSessionStarted += info => DispatcherQueue.TryEnqueue(() => OnHostSessionStarted(info));
        App.Engine.Coordinator.HostSessionEnded += _ => DispatcherQueue.TryEnqueue(OnHostSessionEnded);
        App.Engine.QuickLaunch.AppLaunchedOnHost += args => DispatcherQueue.TryEnqueue(() => OnQuickLaunchAppStartedOnHost(args));
        App.Engine.AccountService.SessionChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyLocalization();
            SyncSameAccountPeerNavItems();
        });
        App.Engine.AccountService.SameAccountDevicesChanged += _ =>
            DispatcherQueue.TryEnqueue(SyncSameAccountPeerNavItems);
        LocalizationService.LanguageChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyLocalization();
            UpdateTrayTooltip();
        });
        Closed += (_, _) =>
        {
            _taskbarFlasher?.Dispose();
            _taskbarFlasher = null;
            _trayIcon?.Dispose();
            _trayIcon = null;
        };

        NavView.SelectedItem = NavOverviewItem;
    }

    public bool IsDarkThemeActive => RootShellGrid.ActualTheme == ElementTheme.Dark;

    public void ApplyTheme(AppThemePreference preference)
    {
        RootShellGrid.RequestedTheme = preference switch
        {
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        bool isDark = preference == AppThemePreference.Dark ||
                      (preference == AppThemePreference.System &&
                       Application.Current.RequestedTheme == ApplicationTheme.Dark);

        ImmersiveWindowHelper.ApplyImmersiveFrame(this, isDark);
    }

    public void ApplyLocalization()
    {
        NavHeaderThisDevice.Content = LocalizationService.GetString("Nav_SectionThisDevice");
        NavHeaderRemote.Content = LocalizationService.GetString("Nav_SectionRemoteAssist");

        string machine = App.Engine.AccountService.GetEffectiveLocalDeviceName();
        NavOverviewText.Text = machine;
        ToolTipService.SetToolTip(NavOverviewItem, machine);

        NavRemoteConnectText.Text = LocalizationService.GetString("Nav_ConnectWorkspace");
        NavQuickLaunchText.Text = LocalizationService.GetString("Nav_QuickLaunch");
        NavGamepadText.Text = LocalizationService.GetString("Nav_GamepadMapper");

        string accountLabel = App.Engine.AccountService.IsLoggedIn &&
                              !string.IsNullOrWhiteSpace(App.Engine.AccountService.CurrentUsername)
            ? App.Engine.AccountService.CurrentUsername
            : LocalizationService.GetString("Nav_Account");
        NavAccountText.Text = accountLabel;
        ToolTipService.SetToolTip(NavAccountItem, accountLabel);

        NavSettingsText.Text = LocalizationService.GetString("Nav_Settings");
        SyncSameAccountPeerNavItems();
    }

    public void SyncSameAccountPeerNavItems()
    {
        if (NavView?.MenuItems == null) return;

        string localDisplay = App.Engine.AccountService.GetEffectiveLocalDeviceName();
        NavOverviewText.Text = localDisplay;
        ToolTipService.SetToolTip(NavOverviewItem, localDisplay);

        var settings = App.Engine.SettingsService.Settings;
        var hiddenIds = settings.HiddenSidebarDeviceIds;
        string localId = DeviceIdentity.NormalizeDeviceId(App.Engine.SettingsService.Identity.DeviceId);
        var peers = App.Engine.AccountService.IsLoggedIn && settings.ShowSameAccountDevicesInSidebar
            ? App.Engine.AccountService.SameAccountDevices
                .Where(d =>
                {
                    if (string.IsNullOrWhiteSpace(d.DeviceId)) return false;
                    string norm = DeviceIdentity.NormalizeDeviceId(d.DeviceId);
                    if (string.Equals(norm, localId, StringComparison.Ordinal)) return false;
                    return hiddenIds == null || !hiddenIds.Contains(norm);
                })
                .ToList()
            : [];

        var activeIds = new HashSet<string>(
            peers.Select(p => DeviceIdentity.NormalizeDeviceId(p.DeviceId)),
            StringComparer.Ordinal);

        foreach (string existingId in _peerDeviceNavItems.Keys.ToList())
        {
            if (!activeIds.Contains(existingId))
            {
                var itemToRemove = _peerDeviceNavItems[existingId];
                bool wasSelected = ReferenceEquals(NavView.SelectedItem, itemToRemove);
                NavView.MenuItems.Remove(itemToRemove);
                _peerDeviceNavItems.Remove(existingId);
                if (wasSelected)
                {
                    NavView.SelectedItem = NavOverviewItem;
                }
            }
        }

        string currentUserId = App.Engine.AccountService.CurrentUserId;
        foreach (var peer in peers)
        {
            string devId = DeviceIdentity.NormalizeDeviceId(peer.DeviceId);
            string baseName = string.IsNullOrWhiteSpace(peer.DeviceName)
                ? DeviceIdentity.FormatDeviceId(devId)
                : peer.DeviceName;
            string displayText = peer.IsBeingControlled ? $"● {baseName}" : baseName;

            string tooltipText =
                $"{baseName} - {LocalizationService.GetString(peer.IsOnline ? "Status_Online" : "Status_Offline")}";
            if (peer.IsBeingControlled)
            {
                bool isSameAccountCtrl = !string.IsNullOrWhiteSpace(peer.ControllerUserId) &&
                                         string.Equals(peer.ControllerUserId, currentUserId,
                                             StringComparison.OrdinalIgnoreCase);
                string who = isSameAccountCtrl
                    ? App.Engine.AccountService.ResolveAccountDeviceName(peer.ControllerDeviceId,
                        peer.ControllerDeviceName)
                    : (!string.IsNullOrWhiteSpace(peer.ControllerUserName)
                        ? peer.ControllerUserName
                        : (peer.ControllerDeviceName ?? string.Empty));
                if (!string.IsNullOrWhiteSpace(who))
                {
                    tooltipText =
                        $"{baseName} - {LocalizationService.Format("Session_WhoControllingWhomFmt", who, baseName)}";
                }
            }

            double itemOpacity = peer.IsOnline ? 1.0 : 0.55;
            if (_peerDeviceNavItems.TryGetValue(devId, out var existingItem))
            {
                if (existingItem.Content is TextBlock tb)
                {
                    tb.Text = displayText;
                    tb.Opacity = itemOpacity;
                }

                if (existingItem.Icon is UIElement existingIcon)
                {
                    existingIcon.Opacity = itemOpacity;
                }

                ToolTipService.SetToolTip(existingItem, tooltipText);
            }
            else
            {
                var icon = new FontIcon
                {
                    Glyph = "\uF35A",
                    Opacity = itemOpacity,
                    RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform { ScaleX = 1.28, ScaleY = 1.28 }
                };
                if (Application.Current.Resources.TryGetValue("FluentIconsFont", out object? fontObj) &&
                    fontObj is FontFamily ff)
                {
                    icon.FontFamily = ff;
                }

                var labelBlock = new TextBlock
                {
                    Text = displayText,
                    Opacity = itemOpacity,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap
                };
                if (Application.Current.Resources.TryGetValue("AppGlobalFont", out object? appFontObj) &&
                    appFontObj is FontFamily appFf)
                {
                    labelBlock.FontFamily = appFf;
                }

                var navItem = new NavigationViewItem
                {
                    Tag = "PeerDevice:" + devId,
                    Icon = icon,
                    Content = labelBlock
                };
                ToolTipService.SetToolTip(navItem, tooltipText);

                int insertIdx = NavView.MenuItems.IndexOf(NavHeaderRemote);
                if (insertIdx >= 0)
                {
                    NavView.MenuItems.Insert(insertIdx, navItem);
                }
                else
                {
                    NavView.MenuItems.Add(navItem);
                }

                _peerDeviceNavItems[devId] = navItem;
            }
        }
    }

    private void ConfigureWindowIconAndSize()
    {
        try
        {
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow = appWindow;

            WindowManagerHelper.ResizeWithDpi(appWindow, this, 1024, 726);
            WindowManagerHelper.CenterWindowOnScreen(appWindow, 1024, 726);

            if (appWindow.Presenter is OverlappedPresenter op)
            {
                op.PreferredMinimumWidth = 860;
                op.PreferredMinimumHeight = 600;
            }

            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "osmium.ico");
            if (File.Exists(iconPath))
            {
                appWindow.SetIcon(iconPath);
            }

            switch (App.Engine.SettingsService.Settings.UiDisplayMode)
            {
                case UiWindowDisplayMode.Maximized:
                    if (appWindow.Presenter is OverlappedPresenter opMax) opMax.Maximize();
                    break;
                case UiWindowDisplayMode.Fullscreen:
                    appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                    break;
            }

            appWindow.Closing += MainWindow_AppWindowClosing;
            appWindow.Changed += MainWindow_AppWindowChanged;
            EnsureTrayIcon();
            SyncTrayIconVisibility();
        }
        catch
        {
        }
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon != null) return;
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "osmium.ico");
        _trayIcon = new TrayIconHelper(
            iconPath,
            onRestore: () => DispatcherQueue.TryEnqueue(RestoreFromTray),
            onToggleAutoStart: () => DispatcherQueue.TryEnqueue(async () =>
            {
                bool next = !StartupHelper.IsAutoStartEnabled();
                StartupHelper.SetAutoStartEnabled(next);
                if (!next && StartupHelper.IsServiceAutoStartEnabled())
                {
                    await StartupHelper.DisableServiceAutoStartAsync();
                }

                AutoStartChangedFromTray?.Invoke();
            }),
            onDisconnectHost: () => DispatcherQueue.TryEnqueue(() => App.Engine.Coordinator.StopHostSession()),
            onExit: () => DispatcherQueue.TryEnqueue(ExitFromTray),
            onSystemShutdown: ExitFromTray,
            getSnapshot: BuildTrayMenuSnapshot);
    }

    private TrayMenuSnapshot BuildTrayMenuSnapshot()
    {
        bool streaming = App.Engine.Coordinator.IsHostStreaming;
        return new TrayMenuSnapshot(
            OpenLabel: LocalizationService.GetString("Tray_OpenMain"),
            AutoStartLabel: LocalizationService.GetString("Tray_AutoStart"),
            AutoStartChecked: StartupHelper.IsAutoStartEnabled(),
            ShowDisconnect: streaming,
            DisconnectLabel: LocalizationService.GetString("Tray_DisconnectHost"),
            ExitLabel: LocalizationService.GetString("Tray_Exit"),
            Tooltip: BuildTrayTooltip());
    }

    private static string BuildTrayTooltip()
    {
        if (!App.Engine.Coordinator.IsHostStreaming)
        {
            return "Osmium";
        }

        var info = App.Engine.Coordinator.ActiveHostSessionInfo;
        if (info != null && (!string.IsNullOrWhiteSpace(info.ControllerDisplayName) ||
                             !string.IsNullOrWhiteSpace(info.HostDisplayName)))
        {
            bool isSameAccount = info.IsSameAccount ||
                                 (App.Engine.AccountService.IsLoggedIn &&
                                  !string.IsNullOrWhiteSpace(info.ControllerUserId) &&
                                  string.Equals(info.ControllerUserId, App.Engine.AccountService.CurrentUserId,
                                      StringComparison.OrdinalIgnoreCase));
            string who = isSameAccount
                ? App.Engine.AccountService.ResolveAccountDeviceName(info.PeerDeviceId, info.ControllerDeviceName)
                : (string.IsNullOrWhiteSpace(info.ControllerDisplayName) ? info.PeerName : info.ControllerDisplayName);
            string whom = isSameAccount
                ? App.Engine.AccountService.GetEffectiveLocalDeviceName()
                : (string.IsNullOrWhiteSpace(info.HostDisplayName)
                    ? App.Engine.AccountService.GetEffectiveLocalDeviceName()
                    : info.HostDisplayName);
            return $"Osmium - {LocalizationService.Format("Session_WhoControllingWhomFmt", who, whom)}";
        }

        return LocalizationService.GetString("Tray_TooltipControlling");
    }

    private static string ResolveControllerDisplayName(HostSessionActiveInfo info)
    {
        bool isSameAccount = info.IsSameAccount ||
                             (App.Engine.AccountService.IsLoggedIn &&
                              !string.IsNullOrWhiteSpace(info.ControllerUserId) &&
                              string.Equals(info.ControllerUserId, App.Engine.AccountService.CurrentUserId,
                                  StringComparison.OrdinalIgnoreCase));
        string who = isSameAccount
            ? App.Engine.AccountService.ResolveAccountDeviceName(info.PeerDeviceId, info.ControllerDeviceName)
            : (string.IsNullOrWhiteSpace(info.ControllerDisplayName) ? info.PeerName : info.ControllerDisplayName);
        return string.IsNullOrWhiteSpace(who)
            ? DeviceIdentity.FormatDeviceId(info.PeerDeviceId)
            : who;
    }

    private void OnHostSessionStarted(HostSessionActiveInfo info)
    {
        SyncTrayIconVisibility();
        UpdateTrayTooltip();
        if (!App.Engine.QuickLaunch.HasActiveSessionGuard)
        {
            NavigateToLocalHome();
            RestoreFromTray();
        }

        var settings = App.Engine.SettingsService.Settings;
        if (settings.HostNotifyOnConnect)
        {
            EnsureTrayIcon();
            string who = ResolveControllerDisplayName(info);
            string title = LocalizationService.GetString("Host_AlertConnectedTitle");
            string body = LocalizationService.Format("Host_AlertConnectedBodyFmt", who);
            _trayIcon?.ShowNotification(title, body);
        }

        if (settings.HostFlashTaskbarOnConnect)
        {
            _taskbarFlasher?.SetSolidRed(_hwnd);
        }
    }

    private void OnQuickLaunchAppStartedOnHost(Osmium.Core.Services.QuickLaunchHostStartedEventArgs args)
    {
        EnsureTrayIcon();
        string title = LocalizationService.GetString("Host_AlertQuickLaunchTitle");
        string body;
        if (args.IsLocalLaunch)
        {
            body = LocalizationService.Format("Host_AlertQuickLaunchLocalBodyFmt", args.AppName);
        }
        else
        {
            string who = App.Engine.AccountService.ResolveAccountDeviceName(
                args.RequesterDeviceId,
                DeviceIdentity.FormatDeviceId(args.RequesterDeviceId));
            body = LocalizationService.Format("Host_AlertQuickLaunchRemoteBodyFmt", who, args.AppName);
        }

        _trayIcon?.ShowNotification(title, body);
    }

    private void OnHostSessionEnded()
    {
        _taskbarFlasher?.Clear(_hwnd);
        SyncTrayIconVisibility();
        UpdateTrayTooltip();
    }

    private void UpdateTrayTooltip()
    {
        _trayIcon?.UpdateTooltip(BuildTrayTooltip());
    }

    public void SyncTrayIconVisibility()
    {
        EnsureTrayIcon();
        var s = App.Engine.SettingsService.Settings;
        bool show = s.CloseToTray || s.HideToTrayOnMinimize || _isHiddenInTray || App.Engine.Coordinator.IsHostStreaming;
        _trayIcon?.SetVisible(show, BuildTrayTooltip());
    }

    public void HideToTray()
    {
        if (_forceExit || _appWindow == null) return;
        _isHiddenInTray = true;
        EnsureTrayIcon();
        _trayIcon?.SetVisible(true, BuildTrayTooltip());
        _appWindow.Hide();
    }

    public void RestoreFromTray()
    {
        if (_appWindow == null) return;
        if (_appWindow.Presenter is OverlappedPresenter op && op.State == OverlappedPresenterState.Minimized)
        {
            op.Restore();
        }

        _appWindow.Show();
        _isHiddenInTray = false;
        BringWindowToForeground();
        SyncTrayIconVisibility();
        DispatcherQueue.TryEnqueue(() => PopupCornerHelper.AttachToTree(ContentFrame));
    }

    private void BringWindowToForeground()
    {
        if (_hwnd == IntPtr.Zero)
        {
            Activate();
            return;
        }

        ShowWindow(_hwnd, IsIconic(_hwnd) ? SwRestore : SwShow);

        IntPtr fgHwnd = GetForegroundWindow();
        uint fgThread = fgHwnd != IntPtr.Zero ? GetWindowThreadProcessId(fgHwnd, out _) : 0;
        uint curThread = GetCurrentThreadId();
        bool attached = false;

        try
        {
            if (fgThread != 0 && fgThread != curThread)
            {
                attached = AttachThreadInput(curThread, fgThread, true);
            }

            SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            SetWindowPos(_hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            BringWindowToTop(_hwnd);
            SetForegroundWindow(_hwnd);
            Activate();
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(curThread, fgThread, false);
            }
        }
    }

    public void ForceExit()
    {
        _forceExit = true;
        _taskbarFlasher?.Dispose();
        _taskbarFlasher = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Close();
    }

    private void ExitFromTray() => ForceExit();
}