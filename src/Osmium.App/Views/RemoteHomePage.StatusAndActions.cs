using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Osmium.App.Helpers;
using Osmium.App.Services;
using Osmium.Core.Models;
using Osmium.Core.Network;

namespace Osmium.App.Views;

public sealed partial class RemoteHomePage : Page
{
    private void ApplyLocalization()
    {
        DisconnectHostBtn.Content = LocalizationService.GetString("Home_DisconnectBtn");
        BlacklistHostControllerBtn.Content = LocalizationService.GetString("Peer_BlacklistControllerBtn");
        UpdateHostControllingHeadline();

        DockHostPolicyLabel.Text = LocalizationService.GetString("Dock_CapturePolicy");
        DockRemoteAssistLabel.Text = LocalizationService.GetString("Dock_RemoteAssist");
        QuickLaunchTitleText.Text = LocalizationService.GetString("Home_QuickLaunch");
        QuickLaunchHintText.Text = LocalizationService.GetString("Home_AddAssistShortcut");

        MenuGoAssistItem.Text = LocalizationService.GetString("Home_MenuGoAssist");
        MenuOpenHostItem.Text = LocalizationService.GetString("Nav_HostControl");
        MenuRenameLocalDeviceItem.Text = LocalizationService.GetString("Set_DeviceRenameTitle");
        MenuReconnectItem.Text = LocalizationService.GetString("Home_MenuReconnect");

        FlyoutToggleApproval.Text = LocalizationService.GetString("Flyout_ToggleApproval");
        FlyoutOpenGamepad.Text = LocalizationService.GetString("Nav_GamepadMapper");
        FlyoutOpenSettings.Text = LocalizationService.GetString("Nav_Settings");

        UpdateDeviceHeader();
        UpdateHostStatusPill();
        UpdateDockActionLabels();
    }

    private void UpdateHostStatusPill()
    {
        bool streaming = App.Engine.Coordinator.IsHostStreaming;
        bool isOnline = App.Engine.Signaling.State == SignalingConnectionState.Connected;

        string bgKey, fgKey, textKey;
        if (streaming)
        {
            bgKey = "OsmiumBadgeDangerBg";
            fgKey = "OsmiumBadgeDangerFg";
            textKey = "Status_Streaming";
        }
        else if (isOnline)
        {
            bgKey = "OsmiumBadgeSuccessBg";
            fgKey = "OsmiumBadgeSuccessFg";
            textKey = "Status_Online";
        }
        else
        {
            bgKey = "OsmiumBadgeNeutralBg";
            fgKey = "OsmiumBadgeNeutralFg";
            textKey = "Status_Offline";
        }

        if (Application.Current.Resources[bgKey] is Brush bg) HostStatePill.Background = bg;
        if (Application.Current.Resources[fgKey] is Brush fg) HostStatePillText.Foreground = fg;
        HostStatePillText.Text = LocalizationService.GetString(textKey);

        bool clientControlling = App.Engine.Coordinator.IsClientSessionActive;
        bool allowRemote = App.Engine.Coordinator.EffectiveAllowRemoteControl;
        MenuToggleAllowItem.Text = LocalizationService.GetString(
            allowRemote ? "Home_MenuDisableRemote" : "Home_MenuEnableRemote");

        bool securityLocked = streaming;
        MenuGoAssistItem.IsEnabled = !securityLocked;
        MenuOpenHostItem.IsEnabled = !securityLocked;
        MenuRenameLocalDeviceItem.IsEnabled = App.Engine.AccountService.IsLoggedIn && !securityLocked;
        MenuToggleAllowItem.IsEnabled = !securityLocked && !clientControlling;
        DockInputBtn.IsEnabled = !securityLocked;
        DockAudioBtn.IsEnabled = !securityLocked;
        DockHostPolicyBtn.IsEnabled = !securityLocked;
        DockRemoteAssistBtn.IsEnabled = !securityLocked;
        QuickLaunchAssistBtn.IsEnabled = !securityLocked;
        FlyoutToggleApproval.IsEnabled = !securityLocked;

        UpdateHeroWallpaper();
        UpdateHostControllingHeadline();
        if (streaming && ActiveHostBanner.Visibility != Visibility.Visible)
        {
            var info = App.Engine.Coordinator.ActiveHostSessionInfo;
            if (info != null && string.IsNullOrWhiteSpace(ActiveHostBannerMessage.Text))
            {
                bool isSameAccount = IsSameAccountController(info);
                string peerLabel = isSameAccount
                    ? App.Engine.AccountService.ResolveAccountDeviceName(
                        info.PeerDeviceId,
                        !string.IsNullOrWhiteSpace(info.ControllerDeviceName)
                            ? info.ControllerDeviceName
                            : info.PeerName)
                    : info.PeerName;
                ActiveHostBannerMessage.Text = $"{peerLabel} - {DeviceIdentity.FormatDeviceId(info.PeerDeviceId)}";
            }

            ActiveHostBanner.IsExpanded = false;
            ActiveHostBanner.Visibility = Visibility.Visible;
        }
        else if (!streaming && ActiveHostBanner.Visibility == Visibility.Visible)
        {
            ActiveHostBanner.Visibility = Visibility.Collapsed;
            ActiveHostBanner.IsExpanded = false;
        }

        UpdateHostControllingOverlay(streaming);
    }

    private void UpdateHostControllingOverlay(bool streaming)
    {
        if (HostControllingOverlay == null) return;

        if (streaming)
        {
            UpdateHostDurationDisplay();
            if (!_hostDurationTimer.IsEnabled)
            {
                _hostDurationTimer.Start();
            }

            if (_isControllingOverlayVisible && HostControllingOverlay.Visibility == Visibility.Visible)
            {
                return;
            }
        }
        else
        {
            _hostDurationTimer.Stop();
            if (!_isControllingOverlayVisible && HostControllingOverlay.Visibility == Visibility.Collapsed)
            {
                return;
            }
        }

        _isControllingOverlayVisible = streaming;
        _overlayFadeStoryboard?.Stop();
        if (streaming)
        {
            HostControllingOverlay.Visibility = Visibility.Visible;
        }

        double targetOpacity = streaming ? 1.0 : 0.0;
        var sb = new Storyboard();
        var fadeAnim = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(streaming ? 280 : 220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fadeAnim, HostControllingOverlay);
        Storyboard.SetTargetProperty(fadeAnim, "Opacity");
        sb.Children.Add(fadeAnim);

        _overlayFadeStoryboard = sb;
        sb.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_overlayFadeStoryboard, sb)) return;
            HostControllingOverlay.Opacity = targetOpacity;
            if (!streaming)
            {
                HostControllingOverlay.Visibility = Visibility.Collapsed;
            }

            _overlayFadeStoryboard = null;
        };
        sb.Begin();
    }

    private void UpdateHostDurationDisplay()
    {
        if (HostControllingDurationText == null) return;

        DateTimeOffset startUtc = App.Engine.Coordinator.HostSessionStartedAtUtc
                                  ?? _fallbackHostStartUtc
                                  ?? DateTimeOffset.UtcNow;
        TimeSpan elapsed = DateTimeOffset.UtcNow - startUtc;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        HostControllingDurationText.Text = elapsed.TotalHours >= 1.0
            ? $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
    }

    private void UpdateDockActionLabels()
    {
        var s = App.Engine.SettingsService.Settings;
        DockInputLabel.Text = LocalizationService.GetString(
            s.HostAllowInputControl ? "Dock_InteractiveOn" : "Dock_ViewOnlyOn");

        DockAudioLabel.Text = LocalizationService.GetString(
            s.HostCaptureAudio ? "Dock_AudioOn" : "Dock_AudioMuted");
    }

    private async void ToggleAllowRemoteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Coordinator.IsHostStreaming || App.Engine.Coordinator.IsClientSessionActive) return;
        var s = App.Engine.SettingsService.Settings;
        s.AllowRemoteControl = !s.AllowRemoteControl;
        App.Engine.SettingsService.SaveSettings();
        UpdateHostStatusPill();
        await App.Engine.SyncSignalingStatusAsync();
    }

    private void DockToggleInputControl_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Coordinator.IsHostStreaming) return;
        var s = App.Engine.SettingsService.Settings;
        s.HostAllowInputControl = !s.HostAllowInputControl;
        App.Engine.SettingsService.SaveSettings();
        UpdateDockActionLabels();
    }

    private void DockToggleAudio_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Coordinator.IsHostStreaming) return;
        var s = App.Engine.SettingsService.Settings;
        s.HostCaptureAudio = !s.HostCaptureAudio;
        App.Engine.SettingsService.SaveSettings();
        UpdateDockActionLabels();
    }

    private void DockToggleApproval_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Coordinator.IsHostStreaming) return;
        var s = App.Engine.SettingsService.Settings;
        s.RequireApprovalDialog = !s.RequireApprovalDialog;
        App.Engine.SettingsService.SaveSettings();
    }

    private void ReconnectDevice_Click(object sender, RoutedEventArgs e)
        => App.Engine.ConnectSignaling();

    private void OpenHostSettings_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Coordinator.IsHostStreaming) return;
        App.MainWindowInstance?.NavigateToHostControl();
    }

    private void GoToRemoteConnect_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Coordinator.IsHostStreaming) return;
        App.MainWindowInstance?.NavigateToRemoteConnect();
    }

    private void GoToGamepad_Click(object sender, RoutedEventArgs e)
        => App.MainWindowInstance?.NavigateToGamepadMapper();

    private void GoToSettings_Click(object sender, RoutedEventArgs e)
        => App.MainWindowInstance?.NavigateToSettings();

    private void DisconnectHostSession_Click(object sender, RoutedEventArgs e)
    {
        App.Engine.Coordinator.StopHostSession();
        ActiveHostBanner.Visibility = Visibility.Collapsed;
        ActiveHostBanner.IsExpanded = false;
        UpdateHostStatusPill();
    }

    private async void BlacklistHostController_Click(object sender, RoutedEventArgs e)
    {
        var info = App.Engine.Coordinator.ActiveHostSessionInfo;
        if (info != null && IsSameAccountController(info)) return;
        string localDevId = App.Engine.SettingsService.Identity.DeviceId;
        await App.Engine.Coordinator.BlacklistControllerOnDeviceAsync(localDevId);
        App.Engine.Coordinator.StopHostSession();
        ActiveHostBanner.Visibility = Visibility.Collapsed;
        ActiveHostBanner.IsExpanded = false;
        UpdateHostStatusPill();
    }

    private async void RenameLocalDevice_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot == null || !App.Engine.AccountService.IsLoggedIn || App.Engine.Coordinator.IsHostStreaming)
            return;

        var id = App.Engine.SettingsService.Identity;
        var localDev = App.Engine.AccountService.FindPeerDevice(id.DeviceId)
                       ?? new AccountDeviceDto
                       {
                           DeviceId = id.DeviceId,
                           DeviceName = string.IsNullOrWhiteSpace(id.DeviceName)
                               ? Environment.MachineName
                               : id.DeviceName,
                           RawDeviceName = string.IsNullOrWhiteSpace(id.DeviceName)
                               ? Environment.MachineName
                               : id.DeviceName
                       };

        var (updated, _) = await DeviceRenameDialogHelper.ShowAsync(XamlRoot, ActualTheme, localDev);
        if (updated)
        {
            UpdateDeviceHeader();
            UpdateHostControllingHeadline();
            App.MainWindowInstance?.SyncSameAccountPeerNavItems();
        }
    }
}