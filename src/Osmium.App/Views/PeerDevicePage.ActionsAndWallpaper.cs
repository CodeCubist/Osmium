using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Osmium.App.Helpers;
using Osmium.App.Services;
using Osmium.Core.Models;
using Osmium.Core.Network;
using Windows.ApplicationModel.DataTransfer;

namespace Osmium.App.Views;

public sealed partial class PeerDevicePage : Page
{
    private async Task UpdateWallpaperAsync()
    {
        if (HeroWallpaperImage == null || HeroWallpaperBackImage == null) return;

        var dev = App.Engine.AccountService.FindPeerDevice(_targetDeviceId);
        bool isDark = ActualTheme == ElementTheme.Dark;
        bool isOffline = dev == null || !dev.IsOnline;
        bool isBlurred = dev is { IsBeingControlled: true };
        string? cachedPath = App.Engine.DeviceWallpapers.GetWallpaperPath(_targetDeviceId);
        string identity = WallpaperHelper.GetWallpaperIdentityKey(cachedPath);

        if (_appliedWallpaperDark == isDark &&
            _appliedWallpaperOffline == isOffline &&
            _appliedWallpaperBlurred == isBlurred &&
            string.Equals(_appliedWallpaperIdentity, identity, StringComparison.Ordinal) &&
            HeroWallpaperImage.Source != null)
        {
            return;
        }

        int version = ++_wallpaperVersion;

        var src = await WallpaperHelper.GetHeroWallpaperVariantAsync(
            isDark,
            grayscale: isOffline,
            blur: isBlurred,
            preferSecondary: true,
            decodeWidth: 1280,
            customImagePath: cachedPath);
        if (version != _wallpaperVersion || src == null) return;

        _appliedWallpaperDark = isDark;
        _appliedWallpaperOffline = isOffline;
        _appliedWallpaperBlurred = isBlurred;
        _appliedWallpaperIdentity = identity;

        ApplyHeroWallpaperWithTransition(src);
    }

    private void ApplyHeroWallpaperWithTransition(ImageSource targetSource)
    {
        if (HeroWallpaperImage == null || HeroWallpaperBackImage == null) return;

        if (HeroWallpaperImage.Source == null)
        {
            _wallpaperFadeStoryboard?.Stop();
            HeroWallpaperBackImage.Source = null;
            HeroWallpaperBackImage.Opacity = 0;
            HeroWallpaperImage.Opacity = 0;
            HeroWallpaperImage.Source = targetSource;

            var firstFade = new Storyboard();
            var firstAnim = new DoubleAnimation
            {
                From = 0,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(380)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(firstAnim, HeroWallpaperImage);
            Storyboard.SetTargetProperty(firstAnim, "Opacity");
            firstFade.Children.Add(firstAnim);

            _wallpaperFadeStoryboard = firstFade;
            firstFade.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_wallpaperFadeStoryboard, firstFade)) return;
                HeroWallpaperBackImage.Source = targetSource;
                HeroWallpaperBackImage.Opacity = 1.0;
                HeroWallpaperImage.Opacity = 1.0;
                _wallpaperFadeStoryboard = null;
            };
            firstFade.Begin();
            return;
        }

        if (ReferenceEquals(HeroWallpaperImage.Source, targetSource))
        {
            return;
        }

        if (_wallpaperFadeStoryboard == null || HeroWallpaperImage.Opacity >= 0.45)
        {
            HeroWallpaperBackImage.Source = HeroWallpaperImage.Source;
        }

        HeroWallpaperBackImage.Opacity = 1.0;

        _wallpaperFadeStoryboard?.Stop();
        HeroWallpaperImage.Opacity = 0;
        HeroWallpaperImage.Source = targetSource;

        var storyboard = new Storyboard();
        var fadeAnim = new DoubleAnimation
        {
            From = 0,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(420)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(fadeAnim, HeroWallpaperImage);
        Storyboard.SetTargetProperty(fadeAnim, "Opacity");
        storyboard.Children.Add(fadeAnim);

        _wallpaperFadeStoryboard = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_wallpaperFadeStoryboard, storyboard)) return;
            HeroWallpaperBackImage.Source = targetSource;
            HeroWallpaperBackImage.Opacity = 1.0;
            HeroWallpaperImage.Opacity = 1.0;
            _wallpaperFadeStoryboard = null;
        };
        storyboard.Begin();
    }

    private void ConnectInteractive_Click(object sender, RoutedEventArgs e)
        => _ = StartSameAccountConnectAsync(viewOnly: false);

    private void ConnectViewOnly_Click(object sender, RoutedEventArgs e)
        => _ = StartSameAccountConnectAsync(viewOnly: true);

    private async Task StartSameAccountConnectAsync(bool viewOnly)
    {
        if (_isConnecting || App.Engine.Coordinator.IsHostStreaming ||
            App.Engine.Coordinator.RemainingConnectCooldownMs > 0)
        {
            UpdateDockCooldownState();
            return;
        }

        _isConnecting = true;
        UpdateDockCooldownState();

        using var cts = new CancellationTokenSource();
        try
        {
            var connectTask =
                App.Engine.Coordinator.ConnectToSameAccountDeviceAsync(_targetDeviceId, viewOnly, cts.Token);
            if (connectTask.IsCompleted)
            {
                var (okImm, msgImm) = await connectTask;
                if (!okImm) await ShowErrorDialogAsync(msgImm);
                return;
            }

            var progressRing = new ProgressRing
                { IsActive = true, Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
            var stageText = new TextBlock
            {
                Text = LocalizationService.GetString("Assist_Connecting"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            var statusRow = new Grid { ColumnSpacing = 12, MinWidth = 300, MaxWidth = 420 };
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(progressRing, 0);
            Grid.SetColumn(stageText, 1);
            statusRow.Children.Add(progressRing);
            statusRow.Children.Add(stageText);

            var dlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = LocalizationService.GetString("Assist_ConnectingTitle"),
                Content = statusRow,
                CloseButtonText = LocalizationService.GetString("Assist_CancelBtn"),
                DefaultButton = ContentDialogButton.Close
            };

            bool userCanceled = false;
            bool closedForStream = false;
            bool switchedToError = false;

            _activeStageSink = s =>
            {
                if (!switchedToError && !closedForStream) stageText.Text = s;
            };
            _activeStreamStartedSink = () =>
            {
                if (!closedForStream && !switchedToError)
                {
                    closedForStream = true;
                    dlg.Hide();
                }
            };
            dlg.Closing += (_, _) =>
            {
                if (!closedForStream && !switchedToError)
                {
                    userCanceled = true;
                    cts.Cancel();
                }
            };

            var dialogTask = dlg.ShowAsync().AsTask();
            var finished = await Task.WhenAny(connectTask, dialogTask);
            if (finished == connectTask)
            {
                var (ok, msg) = await connectTask;
                if (!ok && !userCanceled && !closedForStream)
                {
                    switchedToError = true;
                    progressRing.IsActive = false;
                    progressRing.Visibility = Visibility.Collapsed;
                    statusRow.ColumnSpacing = 0;
                    dlg.Title = LocalizationService.GetString("Assist_ErrorTitle");
                    stageText.Text = msg;
                    dlg.CloseButtonText = LocalizationService.GetString("Assist_OkBtn");
                    await dialogTask;
                }
                else
                {
                    closedForStream = true;
                    dlg.Hide();
                    await dialogTask;
                }
            }
            else
            {
                await dialogTask;
                var (ok, msg) = await connectTask;
                if (!ok && !userCanceled) await ShowErrorDialogAsync(msg);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(ex.Message);
        }
        finally
        {
            _activeStageSink = null;
            _activeStreamStartedSink = null;
            _isConnecting = false;
            UpdateDockCooldownState();
        }
    }

    private async void DisconnectPeerSession_Click(object sender, RoutedEventArgs e)
    {
        await App.Engine.Coordinator.ForceDisconnectAccountDeviceAsync(_targetDeviceId);
        await App.Engine.AccountService.RefreshDevicesAsync();
    }

    private async void BlacklistController_Click(object sender, RoutedEventArgs e)
    {
        var dev = App.Engine.AccountService.FindPeerDevice(_targetDeviceId);
        if (dev != null && IsSelfAccountController(dev)) return;
        await App.Engine.Coordinator.BlacklistControllerOnDeviceAsync(_targetDeviceId);
        await App.Engine.AccountService.RefreshDevicesAsync();
    }

    private async void RenamePeerDevice_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot == null || App.Engine.Coordinator.IsHostStreaming) return;
        var dev = App.Engine.AccountService.FindPeerDevice(_targetDeviceId);
        if (dev == null) return;

        var (updated, _) = await DeviceRenameDialogHelper.ShowAsync(XamlRoot, ActualTheme, dev);
        if (updated)
        {
            RefreshPeerDeviceState();
            App.MainWindowInstance?.SyncSameAccountPeerNavItems();
        }
    }

    private void CopyDeviceId_Click(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(DeviceIdentity.FormatDeviceId(_targetDeviceId));
        Clipboard.SetContent(dp);
    }

    private async void RefreshDevice_Click(object sender, RoutedEventArgs e)
    {
        await App.Engine.AccountService.RefreshDevicesAsync();
    }

    private void ManageDevices_Click(object sender, RoutedEventArgs e)
        => App.MainWindowInstance?.NavigateToSettings("Devices");

    private void PowerWake_Click(object sender, RoutedEventArgs e)
        => _ = ExecuteRemotePowerActionAsync("wake", requireConfirm: false);

    private void PowerRestart_Click(object sender, RoutedEventArgs e)
        => _ = ExecuteRemotePowerActionAsync("restart", requireConfirm: true);

    private void PowerShutdown_Click(object sender, RoutedEventArgs e)
        => _ = ExecuteRemotePowerActionAsync("shutdown", requireConfirm: true);

    private async Task ExecuteRemotePowerActionAsync(string action, bool requireConfirm)
    {
        if (_isPowerBusy || XamlRoot == null) return;
        var dev = App.Engine.AccountService.FindPeerDevice(_targetDeviceId);
        if (dev == null || !dev.IsComputerDevice || !dev.AllowRemotePower) return;

        _isPowerBusy = true;
        UpdateDockPowerState(dev);
        try
        {
            await RemotePowerDialogHelper.ExecuteAsync(XamlRoot, ActualTheme, dev, action, requireConfirm);
        }
        finally
        {
            _isPowerBusy = false;
            RefreshPeerDeviceState();
        }
    }

    private async Task ShowErrorDialogAsync(string message)
    {
        if (XamlRoot == null) return;
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString("Assist_ErrorTitle"),
            Content = new ScrollViewer
            {
                MaxHeight = 260,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = message,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    MinWidth = 300,
                    MaxWidth = 420
                }
            },
            CloseButtonText = LocalizationService.GetString("Assist_OkBtn"),
            DefaultButton = ContentDialogButton.Close
        };
        await dlg.ShowAsync();
    }
}