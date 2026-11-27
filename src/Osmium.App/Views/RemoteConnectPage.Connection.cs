using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Osmium.App.Services;
using Osmium.Core.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Osmium.App.Views;

public sealed partial class RemoteConnectPage : Page
{
    private void RecentSessionsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (!App.Engine.AccountService.IsLoggedIn || App.Engine.Coordinator.IsHostStreaming) return;
        if (e.ClickedItem is RecentSessionRecord rec)
        {
            TargetDeviceIdBox.Text = rec.FormattedId;
            TargetAccessCodeBox.Focus(FocusState.Programmatic);
        }
    }

    private void RemoveRecentItem_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Coordinator.IsHostStreaming) return;
        if (sender is Button btn && btn.Tag is string deviceId)
        {
            App.Engine.SettingsService.RemoveRecentSession(deviceId);
            RefreshRecentSessions();
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Coordinator.IsHostStreaming) return;
        App.Engine.SettingsService.ClearRecentSessions();
        RefreshRecentSessions();
    }

    private void ConnectInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter &&
            App.Engine.AccountService.IsLoggedIn &&
            !App.Engine.Coordinator.IsHostStreaming &&
            App.Engine.Coordinator.RemainingConnectCooldownMs <= 0)
        {
            _ = StartConnectAsync();
        }
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
        => _ = StartConnectAsync();

    private async Task StartConnectAsync()
    {
        if (_isConnecting || App.Engine.Coordinator.IsHostStreaming ||
            App.Engine.Coordinator.RemainingConnectCooldownMs > 0)
        {
            UpdateConnectButtonState();
            return;
        }

        if (!App.Engine.AccountService.IsLoggedIn || App.Engine.AccountService.IsLocked)
        {
            if (XamlRoot != null)
            {
                var authDlg = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = ActualTheme,
                    Title = LocalizationService.GetString("Assist_ErrorTitle"),
                    Content = new TextBlock
                    {
                        Text = LocalizationService.GetString("Conn_AccountRequired"),
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 420
                    },
                    PrimaryButtonText = LocalizationService.GetString("Nav_Account"),
                    CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
                    DefaultButton = ContentDialogButton.Primary
                };
                if (await authDlg.ShowAsync() == ContentDialogResult.Primary)
                {
                    App.MainWindowInstance?.NavigateToAccount();
                }
            }

            return;
        }

        _isConnecting = true;
        ConnectButton.IsEnabled = false;

        using var cts = new CancellationTokenSource();

        try
        {
            string normalizedTarget = DeviceIdentity.NormalizeDeviceId(TargetDeviceIdBox.Text);
            string formattedTarget = DeviceIdentity.FormatDeviceId(normalizedTarget);

            var connectTask = App.Engine.Coordinator.ConnectToRemoteAsync(
                TargetDeviceIdBox.Text,
                TargetAccessCodeBox.Password,
                cts.Token);

            if (connectTask.IsCompleted)
            {
                var (okImmediate, msgImmediate) = await connectTask;
                RefreshRecentSessions();
                if (!okImmediate)
                {
                    await ShowErrorDialogAsync(msgImmediate);
                }

                return;
            }

            var targetLabel = new TextBlock
            {
                Text = LocalizationService.Format("Assist_TargetLabel",
                    string.IsNullOrEmpty(formattedTarget) ? TargetDeviceIdBox.Text.Trim() : formattedTarget),
                FontSize = 12,
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap
            };

            var progressRing = new ProgressRing
            {
                IsActive = true,
                Width = 22,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center
            };

            var stageText = new TextBlock
            {
                Text = LocalizationService.GetString("Assist_Connecting"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };

            var stageScroll = new ScrollViewer
            {
                MaxHeight = 260,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalAlignment = VerticalAlignment.Center,
                Content = stageText
            };

            var statusRow = new Grid
            {
                ColumnSpacing = 12
            };
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(progressRing, 0);
            Grid.SetColumn(stageScroll, 1);
            statusRow.Children.Add(progressRing);
            statusRow.Children.Add(stageScroll);

            var bodyPanel = new StackPanel
            {
                Spacing = 12,
                MinWidth = 320,
                MaxWidth = 420
            };
            bodyPanel.Children.Add(targetLabel);
            bodyPanel.Children.Add(statusRow);

            var dlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = LocalizationService.GetString("Assist_ConnectingTitle"),
                Content = bodyPanel,
                CloseButtonText = LocalizationService.GetString("Assist_CancelBtn"),
                DefaultButton = ContentDialogButton.Close
            };

            bool userCanceled = false;
            bool closedForStream = false;
            bool switchedToError = false;

            _activeStageSink = stage =>
            {
                if (!switchedToError && !closedForStream)
                {
                    stageText.Text = stage;
                }
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
            var completedFirst = await Task.WhenAny(connectTask, dialogTask);

            if (completedFirst == connectTask)
            {
                var (ok, msg) = await connectTask;
                RefreshRecentSessions();

                if (!ok && !userCanceled && !closedForStream)
                {
                    switchedToError = true;
                    progressRing.IsActive = false;
                    progressRing.Visibility = Visibility.Collapsed;
                    statusRow.ColumnSpacing = 0;
                    stageText.IsTextSelectionEnabled = true;
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
                RefreshRecentSessions();
                if (!ok && !userCanceled)
                {
                    await ShowErrorDialogAsync(msg);
                }
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
            UpdateConnectButtonState();
            ClientLiveStatsCard.Visibility = Visibility.Collapsed;
        }
    }

    private async Task ShowErrorDialogAsync(string message)
    {
        if (XamlRoot == null) return;
        var errorText = new TextBlock
        {
            Text = message,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            MinWidth = 300,
            MaxWidth = 420
        };
        var errDlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString("Assist_ErrorTitle"),
            Content = new ScrollViewer
            {
                MaxHeight = 260,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = errorText
            },
            CloseButtonText = LocalizationService.GetString("Assist_OkBtn"),
            DefaultButton = ContentDialogButton.Close
        };
        await errDlg.ShowAsync();
    }
}