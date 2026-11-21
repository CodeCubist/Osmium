using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Osmium.App.Helpers;
using Osmium.App.Services;
using Osmium.Core.Models;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace Osmium.App.Views;

public sealed partial class AccountPage : Page
{
    private async void BtnChangeEmail_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || App.Engine.Coordinator.IsHostStreaming || XamlRoot == null) return;
        var acc = App.Engine.AccountService;

        int dlgStep = 0;
        int dlgAnimGen = 0;
        bool dlgSending = false;
        DateTimeOffset dlgCooldownUntil = DateTimeOffset.MinValue;
        var dlgCooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        var stepIndicator = new TextBlock
        {
            Text = LocalizationService.Format("Acc_StepProgressFmt", 1, 2),
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };

        var step0Transform = new TranslateTransform();
        var pwdBox = new Controls.TogglePasswordBox
        {
            Header = LocalizationService.GetString("Acc_RowPassword"),
            PlaceholderText = LocalizationService.GetString("Acc_PasswordPlaceholder")
        };
        var newEmailBox = new TextBox
        {
            Header = LocalizationService.GetString("Acc_NewEmailHeader"),
            PlaceholderText = LocalizationService.GetString("Acc_EmailPlaceholder")
        };
        var haveOtpBtn = new Button
        {
            Content = LocalizationService.GetString("Acc_WizardHaveOtpBtn"),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Height = 28,
            Padding = new Thickness(4, 2, 4, 2),
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var step0Panel = new StackPanel { Spacing = 10, RenderTransform = step0Transform };
        step0Panel.Children.Add(pwdBox);
        step0Panel.Children.Add(newEmailBox);
        step0Panel.Children.Add(haveOtpBtn);

        var step1Transform = new TranslateTransform();
        var targetEmailSummary = new TextBlock
        {
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        };
        var otpBox = new TextBox
        {
            Header = LocalizationService.GetString("Acc_RowOtp"),
            PlaceholderText = LocalizationService.GetString("Acc_OtpPlaceholder"),
            MaxLength = 12
        };
        var resendBtn = new Button
        {
            Content = LocalizationService.GetString("Acc_WizardResendBtn"),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var step1Panel = new StackPanel
            { Spacing = 10, Visibility = Visibility.Collapsed, RenderTransform = step1Transform };
        step1Panel.Children.Add(targetEmailSummary);
        step1Panel.Children.Add(otpBox);
        step1Panel.Children.Add(resendBtn);

        var stageHost = new Grid { MinWidth = 340, MaxWidth = 400 };
        stageHost.SizeChanged += SlideHost_SizeChanged;
        stageHost.Children.Add(step0Panel);
        stageHost.Children.Add(step1Panel);

        var feedbackText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };

        var rootPanel = new StackPanel { Spacing = 10, MinWidth = 340, MaxWidth = 400 };
        rootPanel.Children.Add(stepIndicator);
        rootPanel.Children.Add(stageHost);
        rootPanel.Children.Add(feedbackText);

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString("Acc_ChangeEmailBtn"),
            Content = rootPanel,
            PrimaryButtonText = LocalizationService.GetString("Acc_WizardSendAndNextBtn"),
            CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        void UpdateDialogCooldownUi()
        {
            int rem = (int)Math.Max(0, Math.Ceiling((dlgCooldownUntil - DateTimeOffset.UtcNow).TotalSeconds));
            if (rem > 0)
            {
                if (!dlgCooldownTimer.IsEnabled) dlgCooldownTimer.Start();
                resendBtn.IsEnabled = false;
                resendBtn.Content = LocalizationService.Format("Acc_OtpResendCooldownFmt", rem);
                if (dlgStep == 0)
                {
                    dlg.PrimaryButtonText = LocalizationService.Format("Acc_OtpSendCooldownFmt", rem);
                    dlg.IsPrimaryButtonEnabled = false;
                }
            }
            else
            {
                if (dlgCooldownTimer.IsEnabled) dlgCooldownTimer.Stop();
                resendBtn.IsEnabled = !dlgSending;
                resendBtn.Content = LocalizationService.GetString("Acc_WizardResendBtn");
                if (dlgStep == 0)
                {
                    dlg.PrimaryButtonText = LocalizationService.GetString("Acc_WizardSendAndNextBtn");
                    dlg.IsPrimaryButtonEnabled = !dlgSending;
                }
            }
        }

        dlgCooldownTimer.Tick += (_, _) => UpdateDialogCooldownUi();

        void SwitchDialogStep(int nextStep)
        {
            bool forward = nextStep > dlgStep;
            dlgStep = nextStep;
            stepIndicator.Text = LocalizationService.Format("Acc_StepProgressFmt", dlgStep + 1, 2);
            dlg.SecondaryButtonText = dlgStep == 1 ? LocalizationService.GetString("Acc_WizardBackBtn") : string.Empty;
            if (dlgStep == 1)
            {
                dlg.PrimaryButtonText = LocalizationService.GetString("Acc_SaveBtn");
                dlg.IsPrimaryButtonEnabled = true;
            }

            UpdateDialogCooldownUi();

            int gen = ++dlgAnimGen;
            if (dlgStep == 1)
            {
                targetEmailSummary.Text =
                    $"{LocalizationService.GetString("Acc_WizardNewEmail")}: {newEmailBox.Text.Trim()}";
                AnimateHorizontalSwap(step0Panel, step0Transform, step1Panel, step1Transform, forward: forward,
                    () => gen == dlgAnimGen, 44);
                DispatcherQueue.TryEnqueue(() => otpBox.Focus(FocusState.Programmatic));
            }
            else
            {
                AnimateHorizontalSwap(step1Panel, step1Transform, step0Panel, step0Transform, forward: forward,
                    () => gen == dlgAnimGen, 44);
                DispatcherQueue.TryEnqueue(() => newEmailBox.Focus(FocusState.Programmatic));
            }
        }

        haveOtpBtn.Click += (_, _) =>
        {
            string pwd = pwdBox.Password;
            string nextMail = newEmailBox.Text.Trim();
            if (string.IsNullOrEmpty(pwd) || pwd.Length < 8)
            {
                feedbackText.Text = LocalizationService.GetString("Acc_ErrPasswordLength");
                return;
            }

            if (string.IsNullOrWhiteSpace(nextMail) || !nextMail.Contains('@'))
            {
                feedbackText.Text = LocalizationService.GetString("Acc_ErrInvalidEmail");
                return;
            }

            feedbackText.Text = string.Empty;
            SwitchDialogStep(1);
        };

        async Task<bool> TriggerChangeEmailOtpAsync()
        {
            string pwd = pwdBox.Password;
            string nextMail = newEmailBox.Text.Trim();
            if (string.IsNullOrEmpty(pwd) || pwd.Length < 8)
            {
                feedbackText.Text = LocalizationService.GetString("Acc_ErrPasswordLength");
                return false;
            }

            if (string.IsNullOrWhiteSpace(nextMail) || !nextMail.Contains('@'))
            {
                feedbackText.Text = LocalizationService.GetString("Acc_ErrInvalidEmail");
                return false;
            }

            var startRes = await acc.StartChangeEmailSendCodeAsync(pwd, nextMail);
            if (startRes.Code == OsmiumErrorCode.OtpCooldown)
            {
                var cd = startRes.RetryAfter.HasValue && startRes.RetryAfter.Value.TotalSeconds > 0
                    ? startRes.RetryAfter.Value
                    : TimeSpan.FromSeconds(60);
                dlgCooldownUntil = DateTimeOffset.UtcNow.Add(cd);
                UpdateDialogCooldownUi();
                feedbackText.Text = acc.FormatFriendlyError(startRes.Code, startRes.HttpStatusCode);
                return true;
            }

            if (!startRes.RequiresCaptcha || startRes.Challenge == null)
            {
                feedbackText.Text = acc.FormatFriendlyError(startRes.Code, startRes.HttpStatusCode);
                return false;
            }

            var pollRes = await CloudCaptchaWindow.ShowChallengeAsync<AccountOtpSentData>(acc, startRes.Challenge);
            if (pollRes == null)
            {
                feedbackText.Text = LocalizationService.GetString("Conn_CaptchaCancelled");
                return false;
            }

            if (pollRes.Ok)
            {
                dlgCooldownUntil = DateTimeOffset.UtcNow.AddSeconds(60);
                UpdateDialogCooldownUi();
                feedbackText.Text = LocalizationService.Format("Acc_OtpSentFmt", nextMail);
                return true;
            }

            if (pollRes.Code == OsmiumErrorCode.OtpCooldown ||
                (pollRes.RetryAfter.HasValue && pollRes.RetryAfter.Value.TotalSeconds > 0))
            {
                var cd = pollRes.RetryAfter.HasValue && pollRes.RetryAfter.Value.TotalSeconds > 0
                    ? pollRes.RetryAfter.Value
                    : TimeSpan.FromSeconds(60);
                dlgCooldownUntil = DateTimeOffset.UtcNow.Add(cd);
                UpdateDialogCooldownUi();
            }

            feedbackText.Text = acc.FormatFriendlyError(pollRes.Code, pollRes.HttpStatusCode);
            return false;
        }

        resendBtn.Click += async (_, _) =>
        {
            if (dlgSending || dlgCooldownUntil > DateTimeOffset.UtcNow) return;
            dlgSending = true;
            resendBtn.IsEnabled = false;
            try
            {
                await TriggerChangeEmailOtpAsync();
            }
            finally
            {
                dlgSending = false;
                UpdateDialogCooldownUi();
            }
        };

        dlg.SecondaryButtonClick += (_, args) =>
        {
            if (dlgStep == 1)
            {
                args.Cancel = true;
                SwitchDialogStep(0);
            }
        };

        dlg.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (dlgStep == 0)
                {
                    args.Cancel = true;
                    if (await TriggerChangeEmailOtpAsync())
                    {
                        SwitchDialogStep(1);
                    }

                    return;
                }

                var res = await acc.ConfirmChangeEmailAsync(pwdBox.Password, newEmailBox.Text, otpBox.Text);
                if (!res.Ok)
                {
                    args.Cancel = true;
                    feedbackText.Text = acc.FormatFriendlyError(res.Code, res.HttpStatusCode);
                }
                else
                {
                    ShowStatus(InfoBarSeverity.Success, LocalizationService.GetString("Acc_MsgEmailUpdated"));
                    RefreshViewState();
                }
            }
            finally
            {
                deferral.Complete();
            }
        };

        try
        {
            await dlg.ShowAsync();
        }
        finally
        {
            dlgCooldownTimer.Stop();
        }
    }

    private async void BtnRefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SetBusy(true);
        try
        {
            await RefreshAllAccountDataAsync();
            RefreshViewState();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnToggleLock_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || App.Engine.Coordinator.IsHostStreaming || XamlRoot == null) return;
        var acc = App.Engine.AccountService;
        bool lockTarget = !acc.IsLocked;

        SetBusy(true);
        string initialFeedback;
        DateTimeOffset lockCooldownUntil = DateTimeOffset.MinValue;
        try
        {
            var startRes = lockTarget ? await acc.SendLockCodeAsync() : await acc.SendUnlockCodeAsync();
            if (startRes.RequiresCaptcha && startRes.Challenge != null)
            {
                var pollRes = await CloudCaptchaWindow.ShowChallengeAsync<AccountOtpSentData>(acc, startRes.Challenge);
                if (pollRes == null)
                {
                    ShowStatus(InfoBarSeverity.Informational, LocalizationService.GetString("Conn_CaptchaCancelled"));
                    return;
                }

                if (!pollRes.Ok)
                {
                    ShowStatus(InfoBarSeverity.Error, acc.FormatFriendlyError(pollRes.Code, pollRes.HttpStatusCode));
                    return;
                }

                lockCooldownUntil = DateTimeOffset.UtcNow.AddSeconds(60);
                initialFeedback = LocalizationService.Format("Acc_OtpSentFmt", acc.CurrentEmail);
            }
            else if (startRes.Code == OsmiumErrorCode.OtpCooldown)
            {
                var cd = startRes.RetryAfter.HasValue && startRes.RetryAfter.Value.TotalSeconds > 0
                    ? startRes.RetryAfter.Value
                    : TimeSpan.FromSeconds(60);
                lockCooldownUntil = DateTimeOffset.UtcNow.Add(cd);
                initialFeedback = acc.FormatFriendlyError(startRes.Code, startRes.HttpStatusCode);
            }
            else
            {
                ShowStatus(InfoBarSeverity.Error, acc.FormatFriendlyError(startRes.Code, startRes.HttpStatusCode));
                return;
            }
        }
        finally
        {
            SetBusy(false);
        }

        var codeBox = new TextBox
        {
            Header = LocalizationService.GetString("Acc_RowOtp"),
            PlaceholderText = LocalizationService.GetString("Acc_OtpPlaceholder"),
            MaxLength = 12
        };
        var feedback = new TextBlock
        {
            Text = initialFeedback,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        var resendBtn = new Button
        {
            Content = LocalizationService.GetString("Acc_WizardResendBtn"),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        bool lockSending = false;
        var lockCooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        void UpdateLockCooldownUi()
        {
            int rem = (int)Math.Max(0, Math.Ceiling((lockCooldownUntil - DateTimeOffset.UtcNow).TotalSeconds));
            if (rem > 0)
            {
                if (!lockCooldownTimer.IsEnabled) lockCooldownTimer.Start();
                resendBtn.IsEnabled = false;
                resendBtn.Content = LocalizationService.Format("Acc_OtpResendCooldownFmt", rem);
            }
            else
            {
                if (lockCooldownTimer.IsEnabled) lockCooldownTimer.Stop();
                resendBtn.IsEnabled = !lockSending;
                resendBtn.Content = LocalizationService.GetString("Acc_WizardResendBtn");
            }
        }

        lockCooldownTimer.Tick += (_, _) => UpdateLockCooldownUi();
        UpdateLockCooldownUi();

        var panel = new StackPanel { Spacing = 10, MinWidth = 340, MaxWidth = 400 };
        panel.Children.Add(codeBox);
        panel.Children.Add(feedback);
        panel.Children.Add(resendBtn);

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString(lockTarget ? "Acc_LockBtn" : "Acc_UnlockBtn"),
            Content = panel,
            PrimaryButtonText = LocalizationService.GetString("Dialog_Ok"),
            CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        resendBtn.Click += async (_, _) =>
        {
            if (lockSending || lockCooldownUntil > DateTimeOffset.UtcNow) return;
            lockSending = true;
            resendBtn.IsEnabled = false;
            try
            {
                var retryStart = lockTarget ? await acc.SendLockCodeAsync() : await acc.SendUnlockCodeAsync();
                if (retryStart.RequiresCaptcha && retryStart.Challenge != null)
                {
                    var pollRes =
                        await CloudCaptchaWindow.ShowChallengeAsync<AccountOtpSentData>(acc, retryStart.Challenge);
                    if (pollRes == null)
                    {
                        feedback.Text = LocalizationService.GetString("Conn_CaptchaCancelled");
                        return;
                    }

                    if (pollRes.Ok)
                    {
                        lockCooldownUntil = DateTimeOffset.UtcNow.AddSeconds(60);
                        feedback.Text = LocalizationService.Format("Acc_OtpSentFmt", acc.CurrentEmail);
                    }
                    else
                    {
                        if (pollRes.Code == OsmiumErrorCode.OtpCooldown ||
                            (pollRes.RetryAfter.HasValue && pollRes.RetryAfter.Value.TotalSeconds > 0))
                        {
                            var cd = pollRes.RetryAfter.HasValue && pollRes.RetryAfter.Value.TotalSeconds > 0
                                ? pollRes.RetryAfter.Value
                                : TimeSpan.FromSeconds(60);
                            lockCooldownUntil = DateTimeOffset.UtcNow.Add(cd);
                        }

                        feedback.Text = acc.FormatFriendlyError(pollRes.Code, pollRes.HttpStatusCode);
                    }
                }
                else
                {
                    if (retryStart.Code == OsmiumErrorCode.OtpCooldown)
                    {
                        var cd = retryStart.RetryAfter.HasValue && retryStart.RetryAfter.Value.TotalSeconds > 0
                            ? retryStart.RetryAfter.Value
                            : TimeSpan.FromSeconds(60);
                        lockCooldownUntil = DateTimeOffset.UtcNow.Add(cd);
                    }

                    feedback.Text = acc.FormatFriendlyError(retryStart.Code, retryStart.HttpStatusCode);
                }
            }
            finally
            {
                lockSending = false;
                UpdateLockCooldownUi();
            }
        };

        dlg.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var res = lockTarget
                    ? await acc.ConfirmLockAsync(codeBox.Text)
                    : await acc.ConfirmUnlockAsync(codeBox.Text);
                if (!res.Ok)
                {
                    args.Cancel = true;
                    feedback.Text = acc.FormatFriendlyError(res.Code, res.HttpStatusCode);
                }
                else
                {
                    RefreshViewState();
                }
            }
            finally
            {
                deferral.Complete();
            }
        };

        try
        {
            await dlg.ShowAsync();
        }
        finally
        {
            lockCooldownTimer.Stop();
        }
    }

    private async void BtnManageBlacklist_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || App.Engine.Coordinator.IsHostStreaming || XamlRoot == null) return;
        var acc = App.Engine.AccountService;
        var listPanel = new StackPanel { Spacing = 8, MinWidth = 340, MaxWidth = 420 };

        async Task ReloadBlacklistAsync()
        {
            listPanel.Children.Clear();
            var res = await acc.GetBlacklistAsync();
            var items = res.Data?.Items ?? [];
            if (items.Count == 0)
            {
                listPanel.Children.Add(new TextBlock
                {
                    Text = LocalizationService.GetString("Acc_BlacklistEmpty"),
                    FontSize = 13,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });
                return;
            }

            foreach (var item in items)
            {
                var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var txt = new TextBlock
                {
                    Text = $"{item.BlockedUsername} - {item.LocalTimeText}",
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                };
                var unblockBtn = new Button
                {
                    Content = LocalizationService.GetString("Acc_UnblockBtn"),
                    CornerRadius = new CornerRadius(6)
                };
                string blockedId = item.BlockedUserId;
                unblockBtn.Click += async (_, _) =>
                {
                    unblockBtn.IsEnabled = false;
                    await acc.RemoveBlacklistUserAsync(blockedId);
                    await ReloadBlacklistAsync();
                };

                Grid.SetColumn(txt, 0);
                Grid.SetColumn(unblockBtn, 1);
                row.Children.Add(txt);
                row.Children.Add(unblockBtn);
                listPanel.Children.Add(row);
            }
        }

        await ReloadBlacklistAsync();

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString("Acc_ManageBlacklistBtn"),
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = listPanel
            },
            CloseButtonText = LocalizationService.GetString("Assist_OkBtn"),
            DefaultButton = ContentDialogButton.Close
        };
        await dlg.ShowAsync();
    }

    private async void BtnLogout_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || App.Engine.Coordinator.IsHostStreaming || XamlRoot == null) return;
        var revokeAllCheck = new CheckBox
        {
            Content = LocalizationService.GetString("Acc_LogoutRevokeAllCheck"),
            IsChecked = false
        };
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString("Acc_LogoutBtn"),
            Content = revokeAllCheck,
            PrimaryButtonText = LocalizationService.GetString("Acc_LogoutBtn"),
            CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true);
        try
        {
            await App.Engine.AccountService.LogoutAsync(revokeAllCheck.IsChecked == true);
            _wizardStep = -1;
            RefreshViewState();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnDeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || App.Engine.Coordinator.IsHostStreaming || XamlRoot == null) return;
        var acc = App.Engine.AccountService;
        var pwdBox = new Controls.TogglePasswordBox
        {
            Header = LocalizationService.GetString("Acc_RowPassword"),
            PlaceholderText = LocalizationService.GetString("Acc_PasswordPlaceholder")
        };
        var errText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 340, MaxWidth = 400 };
        panel.Children.Add(pwdBox);
        panel.Children.Add(errText);

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString("Acc_DeleteAccountBtn"),
            Content = panel,
            PrimaryButtonText = LocalizationService.GetString("Dialog_Ok"),
            CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        dlg.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var res = await acc.DeleteAccountAsync(pwdBox.Password);
                if (!res.Ok)
                {
                    args.Cancel = true;
                    errText.Text = acc.FormatFriendlyError(res.Code, res.HttpStatusCode);
                }
                else
                {
                    _wizardStep = -1;
                    RefreshViewState();
                }
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dlg.ShowAsync();
    }

    private async void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        => await LoadDevicesTabAsync();

    private async Task LoadDevicesTabAsync()
    {
        var res = await App.Engine.AccountService.RefreshDevicesAsync();
        if (res.Ok && res.Data != null)
        {
            RenderDevicesList(res.Data.Devices);
        }
    }

    private void RenderDevicesList(IReadOnlyList<AccountDeviceDto> devices)
    {
        if (DevicesListPanel == null) return;
        DevicesCountText.Text = $"{devices.Count} / 10";
        DevicesListPanel.Children.Clear();

        bool hostControlled = App.Engine.Coordinator.IsHostStreaming;

        foreach (var dev in devices)
        {
            string statusText = dev.IsBeingControlled
                ? LocalizationService.GetString("Status_Streaming")
                : LocalizationService.GetString(dev.IsOnline ? "Status_Online" : "Status_Offline");
            string ipText = string.IsNullOrWhiteSpace(dev.LastIp) ? "-" : dev.LastIp;

            var card = new Controls.SettingsRowCard
            {
                Title = $"{dev.DeviceName} - {dev.FormattedDeviceId}",
                Description = $"{statusText} - {dev.OsVersion} - {ipText}",
                Glyph = "\uF35A"
            };

            var rightStack = new StackPanel
                { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

            var renameBtn = new Button
            {
                Content = LocalizationService.GetString("Set_DeviceRenameBtn"),
                CornerRadius = new CornerRadius(6),
                IsEnabled = !hostControlled
            };
            var capturedDev = dev;
            renameBtn.Click += async (_, _) =>
            {
                if (App.Engine.Coordinator.IsHostStreaming || XamlRoot == null) return;
                var (updated, cleared) = await DeviceRenameDialogHelper.ShowAsync(XamlRoot, ActualTheme, capturedDev);
                if (updated)
                {
                    App.MainWindowInstance?.SyncSameAccountPeerNavItems();
                    ShowStatus(
                        InfoBarSeverity.Success,
                        LocalizationService.GetString(cleared ? "Set_DeviceRenameCleared" : "Set_DeviceRenameSuccess"));
                }
            };
            rightStack.Children.Add(renameBtn);

            var removeBtn = new Button
            {
                Content = LocalizationService.GetString("Acc_RemoveDeviceBtn"),
                CornerRadius = new CornerRadius(6),
                IsEnabled = !hostControlled
            };
            string targetId = dev.DeviceId;
            removeBtn.Click += async (_, _) =>
            {
                removeBtn.IsEnabled = false;
                await App.Engine.AccountService.RemoveDeviceAsync(targetId);
            };
            rightStack.Children.Add(removeBtn);

            card.ActionContent = rightStack;
            DevicesListPanel.Children.Add(card);
        }
    }

    private async void BtnRefreshHistory_Click(object sender, RoutedEventArgs e)
        => await LoadHistoryTabAsync();

    private async Task LoadHistoryTabAsync()
    {
        if (HistoryListPanel == null) return;
        var res = await App.Engine.AccountService.GetLoginHistoryAsync();
        HistoryListPanel.Children.Clear();

        var records = res.Data?.Records ?? [];
        if (records.Count == 0)
        {
            HistoryListPanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.GetString("Acc_HistoryEmpty"),
                FontSize = 13,
                Margin = new Thickness(8, 8, 0, 0),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            return;
        }

        foreach (var rec in records)
        {
            string resolvedName = App.Engine.AccountService.ResolveAccountDeviceName(rec.DeviceId, rec.DeviceName);
            var card = new Controls.SettingsRowCard
            {
                Title = $"{resolvedName} - {rec.FormattedDeviceId}",
                ShowIcon = false
            };
            card.ActionContent = new TextBlock
            {
                Text = $"{rec.LocalTimeText} - {rec.Ip} - {rec.OsVersion}",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center
            };
            HistoryListPanel.Children.Add(card);
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        BtnChooseLogin.IsEnabled = !busy;
        BtnChooseRegister.IsEnabled = !busy;
        BtnChooseReset.IsEnabled = !busy;
        BtnWizardBack.IsEnabled = !busy;
        BtnHaveOtpSkip.IsEnabled = !busy;
        BtnChangeWizardEmail.IsEnabled = !busy;
        WizardBusyRing.IsActive = busy;
        WizardBusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateOtpCooldownVisuals();
        ApplyHostSecurityLockdown();
    }

    private void ShowStatus(InfoBarSeverity severity, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}