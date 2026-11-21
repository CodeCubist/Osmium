using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Osmium.App.Helpers;
using Osmium.App.Services;
using Osmium.Core.Models;
using Osmium.Core.Network;

namespace Osmium.App.Views;

public sealed partial class SettingsPage : Page
{
    private const int TabCount = 7;
    private const int DevicesTabIndex = 5;

    private bool _loading = true;
    private bool _suppressNavSelection = false;
    private int _currentTabIndex = 0;
    private Storyboard? _tabTransitionStoryboard;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
        Unloaded += SettingsPage_Unloaded;
        LocalizationService.LanguageChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyLocalization();
            LoadValues();
        });
        if (App.MainWindowInstance != null)
        {
            App.MainWindowInstance.AutoStartChangedFromTray += () => DispatcherQueue.TryEnqueue(() =>
            {
                bool prev = _loading;
                _loading = true;
                LaunchOnStartupSwitch.IsOn = StartupHelper.IsAutoStartEnabled();
                _loading = prev;
            });
        }

        App.Engine.Coordinator.HostSessionStarted += _ => DispatcherQueue.TryEnqueue(ApplyHostSecurityLockState);
        App.Engine.Coordinator.HostSessionEnded += _ => DispatcherQueue.TryEnqueue(ApplyHostSecurityLockState);
    }

    private void OnAccountSessionChanged()
        => DispatcherQueue.TryEnqueue(RenderDeviceManagementPanel);

    private void OnSameAccountDevicesChanged(IReadOnlyList<AccountDeviceDto> _)
        => DispatcherQueue.TryEnqueue(RenderDeviceManagementPanel);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string initialTab && !string.IsNullOrEmpty(initialTab))
        {
            int idx = GetTabIndex(initialTab);
            if (idx >= 0)
            {
                if (idx == 1 && App.Engine.Coordinator.IsHostStreaming) idx = 0;
                _currentTabIndex = idx;
            }
        }
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        App.Engine.AccountService.SessionChanged -= OnAccountSessionChanged;
        App.Engine.AccountService.SessionChanged += OnAccountSessionChanged;
        App.Engine.AccountService.SameAccountDevicesChanged -= OnSameAccountDevicesChanged;
        App.Engine.AccountService.SameAccountDevicesChanged += OnSameAccountDevicesChanged;

        _loading = true;
        ApplyLocalization();
        LoadValues();
        if (_currentTabIndex == 1 && App.Engine.Coordinator.IsHostStreaming)
        {
            _currentTabIndex = 0;
        }

        ApplyTabSelectionImmediate(_currentTabIndex);
        ApplyHostSecurityLockState();
        if (App.IsPendingLanguageRestart)
        {
            LangRestartBar.Severity = InfoBarSeverity.Informational;
            LangRestartBar.Message = LocalizationService.GetString("Settings_LangRestartPendingMsg");
            LangRestartBar.IsOpen = true;
        }

        _loading = false;

        if (_currentTabIndex == DevicesTabIndex && App.Engine.AccountService.IsLoggedIn)
        {
            _ = RefreshSettingsDevicesAsync();
        }
    }

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        App.Engine.AccountService.SessionChanged -= OnAccountSessionChanged;
        App.Engine.AccountService.SameAccountDevicesChanged -= OnSameAccountDevicesChanged;
    }

    private void ApplyHostSecurityLockState()
    {
        if (NavTabCaptureSecurity == null) return;
        bool locked = App.Engine.Coordinator.IsHostStreaming;

        NavTabCaptureSecurity.IsEnabled = !locked;
        if (locked && _currentTabIndex == 1)
        {
            ApplyTabSelectionImmediate(0);
        }

        EnableEncryptionSwitch.IsEnabled = !locked;
        DeviceAliasBox.IsEnabled = !locked;
        CodePolicyCombo.IsEnabled = !locked;
        UseFixedCodeSwitch.IsEnabled = !locked;
        FixedCodeBox.IsEnabled = !locked && UseFixedCodeSwitch.IsOn;
        FixedCodeBox.AllowReveal = !locked;
        HostCaptureAudioSwitch.IsEnabled = !locked;
        HostAllowInputSwitch.IsEnabled = !locked;
        HostCaptureCursorSwitch.IsEnabled = !locked;
        HostAllowDriverMgmtSwitch.IsEnabled = !locked;
        HostRequireDriverConfirmSwitch.IsEnabled = !locked && HostAllowDriverMgmtSwitch.IsOn;
        InstallDriversBtn.IsEnabled = !locked;
        if (locked)
        {
            UninstallDriversBtn.IsEnabled = false;
        }
        else
        {
            RefreshHostDriverRowStatus();
        }

        RenderDeviceManagementPanel();
    }

    private void SettingsTabViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            SettingsTabViewport.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            };
        }
    }

    public void SelectTab(string tabKey)
    {
        int targetIndex = GetTabIndex(tabKey);
        if (targetIndex < 0) return;
        if (targetIndex == 1 && App.Engine.Coordinator.IsHostStreaming) return;

        var navItem = GetNavItem(targetIndex);
        if (!Equals(SettingsNavView.SelectedItem, navItem))
        {
            _suppressNavSelection = true;
            SettingsNavView.SelectedItem = navItem;
            _suppressNavSelection = false;
        }

        SwitchSettingsTab(targetIndex);
    }

    private void SettingsNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavSelection || PanelGeneral == null) return;
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            int targetIndex = GetTabIndex(tag);
            if (targetIndex == 1 && App.Engine.Coordinator.IsHostStreaming)
            {
                _suppressNavSelection = true;
                SettingsNavView.SelectedItem = GetNavItem(_currentTabIndex == 1 ? 0 : _currentTabIndex);
                _suppressNavSelection = false;
                return;
            }

            if (targetIndex >= 0)
            {
                SwitchSettingsTab(targetIndex);
            }
        }
    }

    private void ApplyTabSelectionImmediate(int targetIndex)
    {
        _currentTabIndex = targetIndex;
        _suppressNavSelection = true;
        SettingsNavView.SelectedItem = GetNavItem(targetIndex);
        _suppressNavSelection = false;

        for (int i = 0; i < TabCount; i++)
        {
            var (panel, translate) = GetTabParts(i);
            panel.Visibility = i == targetIndex ? Visibility.Visible : Visibility.Collapsed;
            panel.Opacity = 1.0;
            translate.X = 0;
        }
    }

    private void SwitchSettingsTab(int targetIndex)
    {
        if (targetIndex == 1 && App.Engine.Coordinator.IsHostStreaming) return;
        int previousIndex = _currentTabIndex;
        if (targetIndex == previousIndex) return;

        _tabTransitionStoryboard?.Stop();
        _currentTabIndex = targetIndex;

        StackPanel? targetPanel = null;
        TranslateTransform? targetTranslate = null;

        for (int i = 0; i < TabCount; i++)
        {
            var (panel, translate) = GetTabParts(i);
            if (i == targetIndex)
            {
                targetPanel = panel;
                targetTranslate = translate;
            }
            else
            {
                panel.Visibility = Visibility.Collapsed;
                panel.Opacity = 1.0;
                translate.X = 0;
            }
        }

        if (targetPanel == null || targetTranslate == null) return;

        SettingsScrollViewer.ChangeView(null, 0, null, true);

        int direction = targetIndex > previousIndex ? 1 : -1;
        double width = SettingsTabViewport?.ActualWidth ?? 0;
        double slideOffset = Math.Min(100.0, Math.Max(width * 0.22, 64.0)) * direction;

        targetPanel.Visibility = Visibility.Visible;
        targetPanel.Opacity = 0;
        targetTranslate.X = slideOffset;

        var storyboard = new Storyboard();
        var slideDuration = new Duration(TimeSpan.FromMilliseconds(280));
        var fadeDuration = new Duration(TimeSpan.FromMilliseconds(200));
        var slideEasing = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
        var fadeEasing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var slideAnim = new DoubleAnimation
        {
            To = 0,
            Duration = slideDuration,
            EasingFunction = slideEasing,
            EnableDependentAnimation = false
        };
        Storyboard.SetTarget(slideAnim, targetTranslate);
        Storyboard.SetTargetProperty(slideAnim, "X");

        var fadeAnim = new DoubleAnimation
        {
            To = 1.0,
            Duration = fadeDuration,
            EasingFunction = fadeEasing
        };
        Storyboard.SetTarget(fadeAnim, targetPanel);
        Storyboard.SetTargetProperty(fadeAnim, "Opacity");

        storyboard.Children.Add(slideAnim);
        storyboard.Children.Add(fadeAnim);

        _tabTransitionStoryboard = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_tabTransitionStoryboard, storyboard)) return;
            targetTranslate.X = 0;
            targetPanel.Opacity = 1.0;
            _tabTransitionStoryboard = null;
        };
        storyboard.Begin();

        if (targetIndex == DevicesTabIndex)
        {
            RenderDeviceManagementPanel();
            if (App.Engine.AccountService.IsLoggedIn)
            {
                _ = RefreshSettingsDevicesAsync();
            }
        }

        DispatcherQueue.TryEnqueue(() => PopupCornerHelper.AttachToTree(targetPanel));
    }

    private static int GetTabIndex(string tag) => tag switch
    {
        "General" => 0,
        "CaptureSecurity" => 1,
        "Video" => 2,
        "AudioInput" => 3,
        "Network" => 4,
        "Devices" => 5,
        "About" => 6,
        _ => -1
    };

    private NavigationViewItem GetNavItem(int index) => index switch
    {
        0 => NavTabGeneral,
        1 => NavTabCaptureSecurity,
        2 => NavTabVideo,
        3 => NavTabAudioInput,
        4 => NavTabNetwork,
        5 => NavTabDevices,
        _ => NavTabAbout
    };

    private (StackPanel Panel, TranslateTransform Translate) GetTabParts(int index) => index switch
    {
        0 => (PanelGeneral, PanelGeneralTranslate),
        1 => (PanelCaptureSecurity, PanelCaptureSecurityTranslate),
        2 => (PanelVideo, PanelVideoTranslate),
        3 => (PanelAudioInput, PanelAudioInputTranslate),
        4 => (PanelNetwork, PanelNetworkTranslate),
        5 => (PanelDevices, PanelDevicesTranslate),
        _ => (PanelAbout, PanelAboutTranslate)
    };

    private void LoadValues()
    {
        bool prev = _loading;
        _loading = true;

        var s = App.Engine.SettingsService.Settings;
        var id = App.Engine.SettingsService.Identity;

        HostDisplayCombo.Items.Clear();
        var friendlyDisplays = DisplayHelper.GetFriendlyMonitorNamesByGdiDevice();
        foreach (var d in App.Engine.Displays)
        {
            HostDisplayCombo.Items.Add(new ComboBoxItem
            {
                Content = DisplayHelper.FormatDisplayOption(d, friendlyDisplays),
                Tag = d.Index
            });
        }

        if (HostDisplayCombo.Items.Count == 0)
        {
            HostDisplayCombo.Items.Add(new ComboBoxItem
                { Content = LocalizationService.GetString("Host_DefaultDisplay"), Tag = 0 });
        }

        SelectByTag(HostDisplayCombo, s.HostDisplayIndex.ToString());

        SelectByTag(HostMaxResCombo, $"{s.HostMaxWidth}x{s.HostMaxHeight}");
        SelectByTag(HostMaxFpsCombo, s.HostMaxFps.ToString());
        HostBitrateSlider.Value = Math.Clamp(s.HostMaxBitrateKbps, 2000, 500000);
        HostBitrateLabel.Text = $"{HostBitrateSlider.Value / 1000.0:F0} Mbps";

        SelectByTag(HostCodecCombo, ((int)s.HostCodec).ToString());
        SelectByTag(HostEncoderModeCombo, ((int)s.HostEncoderMode).ToString());

        DeviceAliasBox.Text = id.DeviceName;
        SelectByTag(CodePolicyCombo, ((int)id.RefreshPolicy).ToString());
        UseFixedCodeSwitch.IsOn = id.UseFixedAccessCode;
        FixedCodeBox.Password = id.FixedAccessCode;
        bool locked = App.Engine.Coordinator.IsHostStreaming;
        FixedCodeBox.IsEnabled = !locked && id.UseFixedAccessCode;
        FixedCodeBox.AllowReveal = !locked;

        HostCaptureAudioSwitch.IsOn = s.HostCaptureAudio;
        HostAllowInputSwitch.IsOn = s.HostAllowInputControl;
        HostCaptureCursorSwitch.IsOn = s.HostCaptureCursor;
        HostAllowDriverMgmtSwitch.IsOn = s.HostAllowRemoteDriverMgmt;
        HostRequireDriverConfirmSwitch.IsOn = s.HostRequireDriverConfirm;
        HostRequireDriverConfirmSwitch.IsEnabled = !locked && s.HostAllowRemoteDriverMgmt;
        RefreshHostDriverRowStatus();

        StunPortBox.Value = s.StunPortOverride;
        PacketSizeBox.Value = Math.Clamp(s.PacketSize, 512, 1460);
        EnableEncryptionSwitch.IsOn = s.EnableEncryption;

        PopulateResolutionCombo(s.TargetWidth, s.TargetHeight);
        PopulateFpsCombo(s.Fps);

        UnlockBitrateSwitch.IsOn = s.UnlockBitrate;
        AutoAdjustBitrateSwitch.IsOn = s.AutoAdjustBitrate;
        UpdateBitrateSliderBoundsAndValue();

        SelectByTag(WindowModeCombo, ((int)s.WindowMode).ToString());
        SelectByTag(ClientCodecCombo, ((int)s.VideoCodec).ToString());
        SelectByTag(ClientDecoderCombo, ((int)s.VideoDecoderMode).ToString());
        SelectByTag(AudioChannelsCombo, ((int)s.AudioChannels).ToString());
        SelectByTag(CaptureSysKeysCombo, ((int)s.CaptureSysKeys).ToString());
        SelectByTag(LanguageCombo, s.Language);
        SelectByTag(ThemeCombo, ((int)s.UiTheme).ToString());
        SelectByTag(UiDisplayModeCombo, ((int)s.UiDisplayMode).ToString());

        VsyncSwitch.IsOn = s.EnableVsync;
        FramePacingSwitch.IsOn = s.EnableFramePacing;
        FramePacingSwitch.IsEnabled = s.EnableVsync;
        EnableHdrSwitch.IsOn = s.EnableHdr;
        EnableYuv444Switch.IsOn = s.EnableYuv444;
        PerfOverlaySwitch.IsOn = s.ShowPerformanceOverlay;
        ConnWarningsSwitch.IsOn = s.ShowConnectionWarnings;
        ConfWarningsSwitch.IsOn = s.ShowConfigurationWarnings;

        MuteHostSpeakersSwitch.IsOn = s.MuteHostSpeakers;
        MuteOnFocusLossSwitch.IsOn = s.MuteOnFocusLoss;

        AbsMouseSwitch.IsOn = s.AbsoluteMouseMode;
        AbsTouchSwitch.IsOn = s.AbsoluteTouchMode;
        SwapMouseSwitch.IsOn = s.SwapMouseButtons;
        ReverseScrollSwitch.IsOn = s.ReverseScrollDirection;
        MultiControllerSwitch.IsOn = s.MultiController;
        SwapFaceButtonsSwitch.IsOn = s.SwapFaceButtons;
        GamepadMouseSwitch.IsOn = s.GamepadMouse;
        BackgroundGamepadSwitch.IsOn = s.BackgroundGamepad;
        KeepAwakeSwitch.IsOn = s.KeepAwake;
        CloseToTraySwitch.IsOn = s.CloseToTray;
        HideToTrayOnMinimizeSwitch.IsOn = s.HideToTrayOnMinimize;
        ShowSidebarDevicesSwitch.IsOn = s.ShowSameAccountDevicesInSidebar;
        LaunchOnStartupSwitch.IsOn = StartupHelper.IsAutoStartEnabled();
        RenderDeviceManagementPanel();

        _loading = prev;
    }

    private void ApplyLocalization()
    {
        bool prev = _loading;
        _loading = true;

        SettingsTitleText.Text = LocalizationService.GetString("Settings_PageTitle");
        NavTabGeneral.Content = LocalizationService.GetString("Tab_General");
        NavTabCaptureSecurity.Content = LocalizationService.GetString("Tab_CaptureSecurity");
        NavTabVideo.Content = LocalizationService.GetString("Tab_Video");
        NavTabAudioInput.Content = LocalizationService.GetString("Tab_AudioInput");
        NavTabNetwork.Content = LocalizationService.GetString("Tab_Network");
        NavTabDevices.Content = LocalizationService.GetString("Tab_Devices");
        NavTabAbout.Content = LocalizationService.GetString("Tab_About");

        RowLanguage.Title = LocalizationService.GetString("Set_Language");
        RowTheme.Title = LocalizationService.GetString("Set_Theme");
        RowUiDisplayMode.Title = LocalizationService.GetString("Set_UiMode");
        RowKeepAwake.Title = LocalizationService.GetString("Set_KeepAwake");
        RowKeepAwake.Description = LocalizationService.GetString("Set_KeepAwakeDesc");
        RowCloseToTray.Title = LocalizationService.GetString("Set_CloseToTray");
        RowHideToTrayOnMinimize.Title = LocalizationService.GetString("Set_HideToTrayOnMinimize");
        RowLaunchOnStartup.Title = LocalizationService.GetString("Set_AutoStart");
        RowLaunchOnStartup.Description = LocalizationService.GetString("Set_AutoStartDesc");

        LocalizationService.SetComboItems(LanguageCombo,
            ("Set_LangAuto", "auto"), ("Set_LangZh", "zh-CN"), ("Set_LangEn", "en-US"));
        LocalizationService.SetComboItems(ThemeCombo,
            ("Set_ThemeLight", "0"), ("Set_ThemeDark", "1"), ("Set_ThemeSystem", "2"));
        LocalizationService.SetComboItems(UiDisplayModeCombo,
            ("Set_UiWindowed", "0"), ("Set_UiMaximized", "1"), ("Set_UiFullscreen", "2"));

        SecHostVideoTitle.Text = LocalizationService.GetString("Host_SecVideo");
        SecHostSecurityTitle.Text = LocalizationService.GetString("Host_SecSecurity");
        SecHostPermissionsTitle.Text = LocalizationService.GetString("Host_SecPermissions");

        RowHostDisplay.Title = LocalizationService.GetString("Host_RowDisplay");
        RowHostMaxRes.Title = LocalizationService.GetString("Host_RowMaxRes");
        RowHostMaxFps.Title = LocalizationService.GetString("Host_RowMaxFps");
        RowHostMaxBitrate.Title = LocalizationService.GetString("Host_RowMaxBitrate");
        RowHostCodec.Title = LocalizationService.GetString("Host_RowCodec");
        RowHostEncoderMode.Title = LocalizationService.GetString("Host_RowEncoderMode");

        RowDeviceAlias.Title = LocalizationService.GetString("Host_RowAlias");
        RowCodePolicy.Title = LocalizationService.GetString("Host_RowCodePolicy");
        RowFixedSwitch.Title = LocalizationService.GetString("Host_RowFixedSwitch");
        RowFixedBox.Title = LocalizationService.GetString("Host_RowFixedBox");
        FixedCodeBox.PlaceholderText = LocalizationService.GetString("Host_FixedPlaceholder");

        RowHostAudio.Title = LocalizationService.GetString("Host_RowAudio");
        RowHostInput.Title = LocalizationService.GetString("Host_RowInput");
        RowHostCursor.Title = LocalizationService.GetString("Host_RowCursor");
        RowHostDrivers.Title = LocalizationService.GetString("Host_RowDrivers");
        RowHostAllowRemoteDriverMgmt.Title = LocalizationService.GetString("Host_RowAllowDriverMgmt");
        RowHostAllowRemoteDriverMgmt.Description = LocalizationService.GetString("Host_RowAllowDriverMgmtDesc");
        RowHostRequireDriverConfirm.Title = LocalizationService.GetString("Host_RowRequireDriverConfirm");
        RowHostRequireDriverConfirm.Description = LocalizationService.GetString("Host_RowRequireDriverConfirmDesc");
        RefreshHostDriverRowStatus();

        HostMaxResCombo.Items.Clear();
        HostMaxResCombo.Items.Add(new ComboBoxItem
            { Content = LocalizationService.GetString("Host_ResNative"), Tag = "0x0" });
        foreach (string res in new[]
                 {
                     "7680x4320 - 8K", "5120x2880 - 5K", "3840x2160 - 4K",
                     "3440x1440 - UWQHD", "2560x1440 - 2K", "1920x1080 - 1080p", "1280x720 - 720p"
                 })
        {
            HostMaxResCombo.Items.Add(new ComboBoxItem { Content = res, Tag = res.Split(' ')[0] });
        }

        HostCodecCombo.Items.Clear();
        HostCodecCombo.Items.Add(new ComboBoxItem
            { Content = LocalizationService.GetString("Host_CodecAuto"), Tag = "0" });
        HostCodecCombo.Items.Add(new ComboBoxItem { Content = "H.264", Tag = "1" });
        HostCodecCombo.Items.Add(new ComboBoxItem { Content = "HEVC", Tag = "2" });
        HostCodecCombo.Items.Add(new ComboBoxItem { Content = "AV1", Tag = "3" });

        LocalizationService.SetComboItems(
            HostEncoderModeCombo,
            ("Host_EncAuto", "0"),
            ("Host_EncHw", "1"),
            ("Host_EncSw", "2"));

        LocalizationService.SetComboItems(
            CodePolicyCombo,
            ("Host_PolicyManual", "0"),
            ("Host_PolicyAfterSession", "1"),
            ("Host_PolicyOnStartup", "2"));

        RowClientRes.Title = LocalizationService.GetString("Set_ResTitle");
        BtnCustomRes.Content = LocalizationService.GetString("Set_CustomBtn");
        RowClientFps.Title = LocalizationService.GetString("Set_FpsTitle");
        BtnCustomFps.Content = LocalizationService.GetString("Set_CustomBtn");
        RowClientBitrate.Title = LocalizationService.GetString("Set_BitrateTitle");
        RowAutoBitrate.Title = LocalizationService.GetString("Set_AutoBitrate");
        RowUnlockBitrate.Title = LocalizationService.GetString("Set_UnlockBitrate");
        RowUnlockBitrate.Description = LocalizationService.GetString("Set_UnlockBitrateDesc");
        RowWindowMode.Title = LocalizationService.GetString("Set_WindowMode");
        RowVsync.Title = LocalizationService.GetString("Set_Vsync");
        RowFramePacing.Title = LocalizationService.GetString("Set_FramePacing");
        RowFramePacing.Description = LocalizationService.GetString("Set_FramePacingDesc");
        RowClientCodec.Title = LocalizationService.GetString("Set_Codec");
        RowClientDecoder.Title = LocalizationService.GetString("Set_Decoder");
        RowHdr.Title = LocalizationService.GetString("Set_Hdr");
        RowHdr.Description = LocalizationService.GetString("Set_HdrDesc");
        RowYuv444.Title = LocalizationService.GetString("Set_Yuv444");
        RowPerfOverlay.Title = LocalizationService.GetString("Set_PerfOverlay");
        RowConnWarn.Title = LocalizationService.GetString("Set_ConnWarn");
        RowConfWarn.Title = LocalizationService.GetString("Set_ConfWarn");

        LocalizationService.SetComboItems(WindowModeCombo,
            ("Set_WinFullscreen", "0"), ("Set_WinBorderless", "1"), ("Set_WinWindowed", "2"));
        LocalizationService.SetComboItems(ClientDecoderCombo,
            ("Set_DecAuto", "0"), ("Set_DecHw", "1"), ("Set_DecSw", "2"));

        ClientCodecCombo.Items.Clear();
        ClientCodecCombo.Items.Add(new ComboBoxItem
            { Content = LocalizationService.GetString("Host_CodecAuto"), Tag = "0" });
        ClientCodecCombo.Items.Add(new ComboBoxItem { Content = "H.264", Tag = "1" });
        ClientCodecCombo.Items.Add(new ComboBoxItem { Content = "HEVC", Tag = "2" });
        ClientCodecCombo.Items.Add(new ComboBoxItem { Content = "AV1", Tag = "3" });

        RowAudioChannels.Title = LocalizationService.GetString("Set_AudioChannels");
        RowMuteHost.Title = LocalizationService.GetString("Set_MuteHost");
        RowMuteFocus.Title = LocalizationService.GetString("Set_MuteFocus");
        RowSysKeys.Title = LocalizationService.GetString("Set_SysKeys");
        RowAbsMouse.Title = LocalizationService.GetString("Set_AbsMouse");
        RowAbsTouch.Title = LocalizationService.GetString("Set_AbsTouch");
        RowSwapMouse.Title = LocalizationService.GetString("Set_SwapMouse");
        RowReverseScroll.Title = LocalizationService.GetString("Set_ReverseScroll");
        RowMultiGamepad.Title = LocalizationService.GetString("Set_MultiGamepad");
        RowSwapFace.Title = LocalizationService.GetString("Set_SwapFace");
        RowGamepadMouse.Title = LocalizationService.GetString("Set_GamepadMouse");
        RowBgGamepad.Title = LocalizationService.GetString("Set_BgGamepad");

        LocalizationService.SetComboItems(AudioChannelsCombo,
            ("Set_AudioStereo", "0"), ("Set_Audio51", "1"), ("Set_Audio71", "2"));
        LocalizationService.SetComboItems(CaptureSysKeysCombo,
            ("Set_SysKeysOff", "0"), ("Set_SysKeysFull", "1"), ("Set_SysKeysAlways", "2"));

        RowUdpPort.Title = LocalizationService.GetString("Set_UdpPort");
        RowUdpPort.Description = LocalizationService.GetString("Set_UdpPortDesc");
        RowPacketSize.Title = LocalizationService.GetString("Set_Mtu");
        RowEnableEncryption.Title = LocalizationService.GetString("Set_EnableEncryption");
        RowEnableEncryption.Description = LocalizationService.GetString("Set_EnableEncryptionDesc");

        RowShowSidebarDevices.Title = LocalizationService.GetString("Set_ShowSidebarDevices");
        RowShowSidebarDevices.Description = LocalizationService.GetString("Set_ShowSidebarDevicesDesc");
        RowSettingsDevicesHeader.Title = LocalizationService.GetString("Set_DevicesSectionTitle");
        BtnRefreshSettingsDevices.Content = LocalizationService.GetString("Gamepad_RefreshBtn");
        RowDevicesLoginPrompt.Title = LocalizationService.GetString("Set_DevicesLoginPromptTitle");
        RowDevicesLoginPrompt.Description = LocalizationService.GetString("Set_DevicesLoginPromptDesc");
        BtnDevicesGoLogin.Content = LocalizationService.GetString("Assist_GoLoginBtn");
        RenderDeviceManagementPanel();

        _loading = prev;
    }

    private void HostSettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || App.Engine.Coordinator.IsHostStreaming) return;
        var s = App.Engine.SettingsService.Settings;
        var id = App.Engine.SettingsService.Identity;

        if (HostDisplayCombo.SelectedItem is ComboBoxItem dispItem &&
            int.TryParse(dispItem.Tag?.ToString(), out int dIdx))
            s.HostDisplayIndex = dIdx;

        if (HostMaxResCombo.SelectedItem is ComboBoxItem resItem && resItem.Tag?.ToString() is string resStr)
        {
            var parts = resStr.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
            {
                s.HostMaxWidth = w;
                s.HostMaxHeight = h;
            }
        }

        if (HostMaxFpsCombo.SelectedItem is ComboBoxItem fpsItem && int.TryParse(fpsItem.Tag?.ToString(), out int fps))
            s.HostMaxFps = fps;

        if (HostCodecCombo.SelectedItem is ComboBoxItem codecItem &&
            int.TryParse(codecItem.Tag?.ToString(), out int codec))
            s.HostCodec = (VideoCodecPreference)codec;

        if (HostEncoderModeCombo.SelectedItem is ComboBoxItem encItem &&
            int.TryParse(encItem.Tag?.ToString(), out int enc))
            s.HostEncoderMode = (HardwarePreference)enc;

        if (CodePolicyCombo.SelectedItem is ComboBoxItem polItem && int.TryParse(polItem.Tag?.ToString(), out int pol))
            id.RefreshPolicy = (AccessCodeRefreshPolicy)pol;

        App.Engine.SettingsService.SaveSettings();
        App.Engine.SettingsService.SaveIdentity();
    }

    private void HostBitrateSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (HostBitrateLabel == null) return;
        HostBitrateLabel.Text = $"{e.NewValue / 1000.0:F0} Mbps";
        if (_loading) return;
        App.Engine.SettingsService.Settings.HostMaxBitrateKbps = (int)e.NewValue;
        App.Engine.SettingsService.SaveSettings();
    }

    private async void DeviceAliasBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || App.Engine.Coordinator.IsHostStreaming) return;
        string name = DeviceAliasBox.Text.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            App.Engine.SettingsService.Identity.DeviceName = name;
            App.Engine.SettingsService.SaveIdentity();
            App.MainWindowInstance?.ApplyLocalization();
            await App.Engine.SyncSignalingStatusAsync();
        }
    }

    private void FixedCodeSetting_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || App.Engine.Coordinator.IsHostStreaming) return;
        FixedCodeBox.IsEnabled = UseFixedCodeSwitch.IsOn;
        App.Engine.SettingsService.Identity.UseFixedAccessCode = UseFixedCodeSwitch.IsOn;
        App.Engine.SettingsService.SaveIdentity();
    }

    private void FixedCodeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || App.Engine.Coordinator.IsHostStreaming) return;
        App.Engine.SettingsService.Identity.FixedAccessCode = FixedCodeBox.Password.Trim();
        App.Engine.SettingsService.SaveIdentity();
    }

    private void HostPermissionSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || App.Engine.Coordinator.IsHostStreaming) return;
        var s = App.Engine.SettingsService.Settings;
        s.HostCaptureAudio = HostCaptureAudioSwitch.IsOn;
        s.HostAllowInputControl = HostAllowInputSwitch.IsOn;
        s.HostCaptureCursor = HostCaptureCursorSwitch.IsOn;
        s.HostAllowRemoteDriverMgmt = HostAllowDriverMgmtSwitch.IsOn;
        s.HostRequireDriverConfirm = HostRequireDriverConfirmSwitch.IsOn;
        HostRequireDriverConfirmSwitch.IsEnabled = s.HostAllowRemoteDriverMgmt;
        App.Engine.SettingsService.SaveSettings();
    }

    private void PopulateResolutionCombo(int currentW, int currentH)
    {
        ClientResCombo.Items.Clear();
        var entries = new List<(int w, int h, string label)>
        {
            (1280, 720, "720p - 1280×720"),
            (1920, 1080, "1080p - 1920×1080"),
            (2560, 1440, "1440p - 2560×1440"),
            (3440, 1440, "UWQHD - 3440×1440"),
            (3840, 2160, "4K - 3840×2160"),
            (5120, 2880, "5K - 5120×2880"),
            (7680, 4320, "8K - 7680×4320")
        };

        foreach (var d in App.Engine.Displays)
        {
            if (d.Width > 0 && d.Height > 0 && !entries.Any(x => x.w == d.Width && x.h == d.Height))
            {
                entries.Add((d.Width, d.Height,
                    LocalizationService.Format("Set_ResNativeLabel", d.Index + 1, d.Width, d.Height)));
            }
        }

        entries = entries.OrderBy(e => (long)e.w * e.h).ToList();

        int matchedIdx = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            ClientResCombo.Items.Add(new ComboBoxItem { Content = e.label, Tag = $"{e.w}x{e.h}" });
            if (e.w == currentW && e.h == currentH) matchedIdx = i;
        }

        if (matchedIdx < 0)
        {
            ClientResCombo.Items.Add(new ComboBoxItem
            {
                Content = LocalizationService.Format("Set_ResCustomLabel", currentW, currentH),
                Tag = $"{currentW}x{currentH}"
            });
            matchedIdx = ClientResCombo.Items.Count - 1;
        }

        ClientResCombo.SelectedIndex = matchedIdx;
    }

    private void PopulateFpsCombo(int currentFps)
    {
        ClientFpsCombo.Items.Clear();
        var rates = new SortedSet<int> { 30, 60, 90, 120, 144, 165, 240, 360 };
        foreach (var d in App.Engine.Displays)
        {
            if (d.RefreshRate >= 24) rates.Add(d.RefreshRate);
        }

        if (currentFps >= 10) rates.Add(currentFps);

        int matchedIdx = 0;
        int idx = 0;
        foreach (int r in rates)
        {
            bool isNative = App.Engine.Displays.Any(d => d.RefreshRate == r);
            ClientFpsCombo.Items.Add(new ComboBoxItem
            {
                Content = isNative ? LocalizationService.Format("Set_FpsNativeLabel", r) : $"{r} FPS",
                Tag = r.ToString()
            });
            if (r == currentFps) matchedIdx = idx;
            idx++;
        }

        ClientFpsCombo.SelectedIndex = matchedIdx;
    }

    private void UpdateBitrateSliderBoundsAndValue()
    {
        var s = App.Engine.SettingsService.Settings;
        ClientBitrateSlider.Maximum = s.UnlockBitrate ? 500000 : 150000;
        s.BitrateKbps = Math.Clamp(s.BitrateKbps, 500, (int)ClientBitrateSlider.Maximum);
        ClientBitrateSlider.Value = s.BitrateKbps;

        int rec = OsmiumSettings.GetDefaultBitrateKbps(s.TargetWidth, s.TargetHeight, s.Fps, s.EnableYuv444);
        ClientBitrateLabel.Text = $"{s.BitrateKbps / 1000.0:F1} Mbps";
        ResetBitrateButton.Content = LocalizationService.Format("Set_ResetRec", $"{rec / 1000.0:F0}");
        ResetBitrateButton.Visibility = s.BitrateKbps != rec ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MaybeAutoAdjustBitrate()
    {
        var s = App.Engine.SettingsService.Settings;
        if (s.AutoAdjustBitrate)
        {
            s.BitrateKbps = OsmiumSettings.GetDefaultBitrateKbps(s.TargetWidth, s.TargetHeight, s.Fps, s.EnableYuv444);
        }

        bool prev = _loading;
        _loading = true;
        UpdateBitrateSliderBoundsAndValue();
        _loading = prev;
    }

    private static void SelectByTag(ComboBox combo, string tagValue)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == tagValue)
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }
}