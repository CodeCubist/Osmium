using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Osmium.App.Services;
using Osmium.Core.Helpers;
using Osmium.Core.Models;
using Windows.ApplicationModel.DataTransfer;

namespace Osmium.App.Views;

public sealed partial class AccountPage : Page
{
    private static void CopyTextToClipboard(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    private async Task ShowTwoFaVerifyDialogAsync(string email, string twoFaToken)
    {
        if (XamlRoot == null) return;
        var acc = App.Engine.AccountService;

        var descText = new TextBlock
        {
            Text = LocalizationService.GetString("Acc_TwoFaLoginDesc"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        var codeBox = new TextBox
        {
            Header = LocalizationService.GetString("Acc_TwoFaCodeLabel"),
            PlaceholderText = LocalizationService.GetString("Acc_TwoFaCodePlaceholder"),
            MaxLength = 6
        };
        var feedbackText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 340, MaxWidth = 400 };
        panel.Children.Add(descText);
        panel.Children.Add(codeBox);
        panel.Children.Add(feedbackText);

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString("Acc_TwoFaLoginTitle"),
            Content = panel,
            PrimaryButtonText = LocalizationService.GetString("Acc_TwoFaVerifyBtn"),
            CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        bool loggedIn = false;
        bool expired = false;

        dlg.PrimaryButtonClick += async (_, args) =>
        {
            string code = new string(codeBox.Text.Where(char.IsDigit).ToArray());
            if (code.Length != 6)
            {
                args.Cancel = true;
                feedbackText.Text = LocalizationService.GetString("Acc_ErrEnterTwoFaCode");
                return;
            }

            var deferral = args.GetDeferral();
            try
            {
                feedbackText.Text = string.Empty;
                var res = await acc.VerifyTwoFaLoginAsync(twoFaToken, code);
                if (res.RequiresCaptcha && res.Challenge != null)
                {
                    var verified = await CloudCaptchaWindow.ShowChallengeAsync<AccountLoginSessionData>(
                        acc, res.Challenge);
                    if (verified == null)
                    {
                        args.Cancel = true;
                        feedbackText.Text = LocalizationService.GetString("Conn_CaptchaCancelled");
                        return;
                    }

                    res = verified;
                }

                if (res.Ok && res.Data != null && !string.IsNullOrWhiteSpace(res.Data.Stoken))
                {
                    acc.ApplyLoginSession(email, res.Data);
                    loggedIn = true;
                    return;
                }

                if (res.Code == OsmiumErrorCode.TwoFaExpired || res.HttpStatusCode == 410)
                {
                    expired = true;
                    return;
                }

                args.Cancel = true;
                feedbackText.Text = acc.FormatFriendlyError(res.Code, res.HttpStatusCode, res.RetryAfter);
                codeBox.SelectAll();
                codeBox.Focus(FocusState.Programmatic);
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dlg.ShowAsync();

        if (loggedIn)
        {
            AuthPasswordBox.Password = string.Empty;
            _wizardStep = -1;
            ShowStatus(InfoBarSeverity.Success, LocalizationService.GetString("Acc_MsgLoginSuccess"));
            RefreshViewState();
            _ = RefreshAllAccountDataAsync();
        }
        else if (expired)
        {
            ShowStatus(InfoBarSeverity.Error, LocalizationService.GetString("Acc_ErrTwoFaExpired"));
        }
    }

    private async Task ShowLoginKeyLoginDialogAsync()
    {
        if (XamlRoot == null || _isBusy || !CanAttemptAuth) return;
        var acc = App.Engine.AccountService;

        var descText = new TextBlock
        {
            Text = LocalizationService.GetString("Acc_LoginKeyDialogDesc"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        var keyBox = new Controls.TogglePasswordBox
        {
            Header = LocalizationService.GetString("Acc_LoginKeyInputLabel"),
            PlaceholderText = LocalizationService.GetString("Acc_LoginKeyInputPlaceholder")
        };
        var feedbackText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 360, MaxWidth = 420 };
        panel.Children.Add(descText);
        panel.Children.Add(keyBox);
        panel.Children.Add(feedbackText);

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString("Acc_ModeLoginKey"),
            Content = panel,
            PrimaryButtonText = LocalizationService.GetString("Acc_SubmitLogin"),
            CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        bool loggedIn = false;

        dlg.PrimaryButtonClick += async (_, args) =>
        {
            string key = keyBox.Password.Trim();
            if (string.IsNullOrWhiteSpace(key) || key.Length < 16)
            {
                args.Cancel = true;
                feedbackText.Text = LocalizationService.GetString("Acc_ErrLoginKeyInvalid");
                return;
            }

            var deferral = args.GetDeferral();
            try
            {
                feedbackText.Text = string.Empty;
                var res = await acc.LoginWithKeyAsync(key);
                if (res.RequiresCaptcha && res.Challenge != null)
                {
                    var verified = await CloudCaptchaWindow.ShowChallengeAsync<AccountLoginSessionData>(
                        acc, res.Challenge);
                    if (verified == null)
                    {
                        args.Cancel = true;
                        feedbackText.Text = LocalizationService.GetString("Conn_CaptchaCancelled");
                        return;
                    }

                    if (verified.Ok && verified.Data != null && !string.IsNullOrWhiteSpace(verified.Data.Stoken))
                    {
                        acc.ApplyLoginSession(verified.Data.Email, verified.Data);
                        loggedIn = true;
                        return;
                    }

                    res = verified;
                }

                if (res.Ok && acc.IsLoggedIn)
                {
                    loggedIn = true;
                    return;
                }

                args.Cancel = true;
                feedbackText.Text = acc.FormatFriendlyError(res.Code, res.HttpStatusCode, res.RetryAfter);
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dlg.ShowAsync();

        if (loggedIn)
        {
            _wizardStep = -1;
            ShowStatus(InfoBarSeverity.Success, LocalizationService.GetString("Acc_MsgLoginSuccess"));
            RefreshViewState();
            _ = RefreshAllAccountDataAsync();
        }
    }

    private async void BtnManageTwoFa_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || App.Engine.Coordinator.IsHostStreaming || XamlRoot == null) return;
        var acc = App.Engine.AccountService;
        if (!acc.HasPassword) return;

        if (acc.TwoFaEnabled)
        {
            await ShowRemoveTwoFaDialogAsync();
        }
        else
        {
            await ShowEnableTwoFaDialogAsync();
        }
    }

    private async Task ShowEnableTwoFaDialogAsync()
    {
        if (XamlRoot == null) return;
        var acc = App.Engine.AccountService;

        int step = 0;
        TwoFaSetupStartData? setupData = null;

        var step1Panel = new StackPanel { Spacing = 10 };
        var pwdDesc = new TextBlock
        {
            Text = LocalizationService.GetString("Acc_TwoFaVerifyPasswordDesc"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        var pwdBox = new Controls.TogglePasswordBox
        {
            Header = LocalizationService.GetString("Acc_RowPassword"),
            PlaceholderText = LocalizationService.GetString("Acc_PasswordPlaceholder")
        };
        step1Panel.Children.Add(pwdDesc);
        step1Panel.Children.Add(pwdBox);

        var step2Panel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        var scanDesc = new TextBlock
        {
            Text = LocalizationService.GetString("Acc_TwoFaScanQrDesc"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        var qrImage = new Image
        {
            Width = 176,
            Height = 176,
            Stretch = Stretch.Uniform
        };
        var qrFrame = new Border
        {
            Width = 192,
            Height = 192,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["OsmiumDividerBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = qrImage
        };
        var secretBox = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Header = LocalizationService.GetString("Acc_TwoFaSecretLabel")
        };
        var copySecretBtn = new Button
        {
            Content = LocalizationService.GetString("Acc_CopyBtn"),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var secretRow = new Grid { ColumnSpacing = 8 };
        secretRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        secretRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(secretBox, 0);
        Grid.SetColumn(copySecretBtn, 1);
        secretRow.Children.Add(secretBox);
        secretRow.Children.Add(copySecretBtn);

        var codeBox = new TextBox
        {
            Header = LocalizationService.GetString("Acc_TwoFaCodeLabel"),
            PlaceholderText = LocalizationService.GetString("Acc_TwoFaCodePlaceholder"),
            MaxLength = 6
        };
        step2Panel.Children.Add(scanDesc);
        step2Panel.Children.Add(qrFrame);
        step2Panel.Children.Add(secretRow);
        step2Panel.Children.Add(codeBox);

        var feedbackText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
        };

        var rootPanel = new StackPanel { Spacing = 10, MinWidth = 360, MaxWidth = 420 };
        rootPanel.Children.Add(step1Panel);
        rootPanel.Children.Add(step2Panel);
        rootPanel.Children.Add(feedbackText);

        copySecretBtn.Click += (_, _) =>
        {
            if (setupData != null && !string.IsNullOrWhiteSpace(setupData.Secret))
            {
                CopyTextToClipboard(setupData.Secret);
                copySecretBtn.Content = LocalizationService.GetString("Acc_CopiedBtn");
            }
        };

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString("Acc_EnableTwoFaTitle"),
            Content = rootPanel,
            PrimaryButtonText = LocalizationService.GetString("Acc_WizardNextBtn"),
            CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        bool enabledOk = false;

        dlg.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                feedbackText.Text = string.Empty;
                if (step == 0)
                {
                    args.Cancel = true;
                    string pwd = pwdBox.Password;
                    if (string.IsNullOrEmpty(pwd) || pwd.Length < 8)
                    {
                        feedbackText.Text = LocalizationService.GetString("Acc_ErrPasswordLength");
                        return;
                    }

                    var startRes = await acc.StartTwoFaSetupAsync(pwd);
                    if (!startRes.Ok || startRes.Data == null || string.IsNullOrWhiteSpace(startRes.Data.OtpAuthUri))
                    {
                        feedbackText.Text = acc.FormatFriendlyError(startRes.Code, startRes.HttpStatusCode, startRes.RetryAfter);
                        return;
                    }

                    setupData = startRes.Data;
                    bool[,] matrix = QrCodeEncoder.Encode(setupData.OtpAuthUri);
                    qrImage.Source = RenderQrMatrixToBitmap(matrix, targetPixels: 352);
                    secretBox.Text = setupData.Secret;
                    step = 1;
                    step1Panel.Visibility = Visibility.Collapsed;
                    step2Panel.Visibility = Visibility.Visible;
                    dlg.PrimaryButtonText = LocalizationService.GetString("Acc_ConfirmEnableBtn");
                    DispatcherQueue.TryEnqueue(() => codeBox.Focus(FocusState.Programmatic));
                    return;
                }

                string code = new string(codeBox.Text.Where(char.IsDigit).ToArray());
                if (code.Length != 6 || setupData == null)
                {
                    args.Cancel = true;
                    feedbackText.Text = LocalizationService.GetString("Acc_ErrEnterTwoFaCode");
                    return;
                }

                var confirmRes = await acc.ConfirmTwoFaSetupAsync(setupData.SetupToken, code);
                if (!confirmRes.Ok)
                {
                    args.Cancel = true;
                    feedbackText.Text = acc.FormatFriendlyError(confirmRes.Code, confirmRes.HttpStatusCode, confirmRes.RetryAfter);
                    return;
                }

                enabledOk = true;
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dlg.ShowAsync();

        if (enabledOk)
        {
            ShowStatus(InfoBarSeverity.Success, LocalizationService.GetString("Acc_MsgTwoFaEnabled"));
            RefreshViewState();
        }
    }

    private async Task ShowRemoveTwoFaDialogAsync()
    {
        if (XamlRoot == null) return;
        var acc = App.Engine.AccountService;

        var desc = new TextBlock
        {
            Text = LocalizationService.GetString("Acc_RemoveTwoFaDesc"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        var pwdBox = new Controls.TogglePasswordBox
        {
            Header = LocalizationService.GetString("Acc_RowPassword"),
            PlaceholderText = LocalizationService.GetString("Acc_PasswordPlaceholder")
        };
        var feedbackText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 340, MaxWidth = 400 };
        panel.Children.Add(desc);
        panel.Children.Add(pwdBox);
        panel.Children.Add(feedbackText);

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString("Acc_RemoveTwoFaTitle"),
            Content = panel,
            PrimaryButtonText = LocalizationService.GetString("Acc_RemoveTwoFaBtn"),
            CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        bool removedOk = false;

        dlg.PrimaryButtonClick += async (_, args) =>
        {
            string pwd = pwdBox.Password;
            if (string.IsNullOrEmpty(pwd) || pwd.Length < 8)
            {
                args.Cancel = true;
                feedbackText.Text = LocalizationService.GetString("Acc_ErrPasswordLength");
                return;
            }

            var deferral = args.GetDeferral();
            try
            {
                feedbackText.Text = string.Empty;
                var res = await acc.RemoveTwoFaAsync(pwd);
                if (!res.Ok)
                {
                    args.Cancel = true;
                    feedbackText.Text = acc.FormatFriendlyError(res.Code, res.HttpStatusCode, res.RetryAfter);
                    return;
                }

                removedOk = true;
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dlg.ShowAsync();

        if (removedOk)
        {
            ShowStatus(InfoBarSeverity.Success, LocalizationService.GetString("Acc_MsgTwoFaRemoved"));
            RefreshViewState();
        }
    }

    private async void BtnManageLoginKeys_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || App.Engine.Coordinator.IsHostStreaming || XamlRoot == null) return;
        await ShowLoginKeysDialogAsync();
    }

    private async Task ShowLoginKeysDialogAsync()
    {
        if (XamlRoot == null) return;
        var acc = App.Engine.AccountService;

        bool reopenAfterCreate = false;
        do
        {
            reopenAfterCreate = false;
            var listPanel = new StackPanel { Spacing = 8, MinWidth = 380 };
            var feedbackText = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };

            async Task ReloadListAsync()
            {
                listPanel.Children.Clear();
                listPanel.Children.Add(new TextBlock
                {
                    Text = LocalizationService.GetString("Acc_Loading"),
                    FontSize = 13,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });

                var res = await acc.GetLoginKeysAsync();
                listPanel.Children.Clear();

                if (!res.Ok || res.Data == null)
                {
                    feedbackText.Text = acc.FormatFriendlyError(res.Code, res.HttpStatusCode, res.RetryAfter);
                    return;
                }

                if (res.Data.Keys.Count == 0)
                {
                    listPanel.Children.Add(new TextBlock
                    {
                        Text = LocalizationService.GetString("Acc_LoginKeysEmpty"),
                        FontSize = 13,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        Margin = new Thickness(0, 8, 0, 8)
                    });
                    return;
                }

                foreach (var item in res.Data.Keys)
                {
                    var card = new Border
                    {
                        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                        BorderBrush = (Brush)Application.Current.Resources["OsmiumDividerBrush"],
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12, 10, 12, 10)
                    };

                    var rowGrid = new Grid { ColumnSpacing = 12 };
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var infoStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                    infoStack.Children.Add(new TextBlock
                    {
                        Text = item.Name,
                        FontSize = 14,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    infoStack.Children.Add(new TextBlock
                    {
                        Text = item.KeyPrefix,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    });
                    string metaText = item.LastUsedAt > 0
                        ? LocalizationService.Format("Acc_LoginKeyMetaUsedFmt", item.CreatedAtText, item.LastUsedAtText)
                        : LocalizationService.Format("Acc_LoginKeyMetaUnusedFmt", item.CreatedAtText);
                    infoStack.Children.Add(new TextBlock
                    {
                        Text = metaText,
                        FontSize = 11.5,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    });

                    var removeBtn = new Button
                    {
                        Content = LocalizationService.GetString("Acc_RemoveBtn"),
                        CornerRadius = new CornerRadius(6),
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
                    };

                    string keyId = item.Id;
                    removeBtn.Click += async (_, _) =>
                    {
                        removeBtn.IsEnabled = false;
                        feedbackText.Text = string.Empty;
                        var delRes = await acc.DeleteLoginKeyAsync(keyId);
                        if (!delRes.Ok)
                        {
                            removeBtn.IsEnabled = true;
                            feedbackText.Text = acc.FormatFriendlyError(delRes.Code, delRes.HttpStatusCode, delRes.RetryAfter);
                            return;
                        }

                        await ReloadListAsync();
                    };

                    Grid.SetColumn(infoStack, 0);
                    Grid.SetColumn(removeBtn, 1);
                    rowGrid.Children.Add(infoStack);
                    rowGrid.Children.Add(removeBtn);
                    card.Child = rowGrid;
                    listPanel.Children.Add(card);
                }
            }

            var scroll = new ScrollViewer
            {
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = listPanel
            };

            var rootPanel = new StackPanel { Spacing = 10, MinWidth = 400, MaxWidth = 460 };
            rootPanel.Children.Add(scroll);
            rootPanel.Children.Add(feedbackText);

            var dlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = LocalizationService.GetString("Acc_RowLoginKeys"),
                Content = rootPanel,
                PrimaryButtonText = LocalizationService.GetString("Acc_CreateLoginKeyBtn"),
                CloseButtonText = LocalizationService.GetString("Dialog_Close"),
                DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = acc.HasEmail
            };

            if (!acc.HasEmail)
            {
                feedbackText.Text = LocalizationService.GetString("Acc_ErrEmailRequired");
            }

            _ = ReloadListAsync();

            var result = await dlg.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                bool created = await ShowCreateLoginKeyDialogAsync();
                reopenAfterCreate = created;
            }
        } while (reopenAfterCreate);
    }

    private async Task<bool> ShowCreateLoginKeyDialogAsync()
    {
        if (XamlRoot == null) return false;
        var acc = App.Engine.AccountService;
        if (!acc.HasEmail) return false;

        int dlgStep = 0;
        bool dlgSending = false;
        DateTimeOffset dlgCooldownUntil = DateTimeOffset.MinValue;
        var dlgCooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        var nameBox = new TextBox
        {
            Header = LocalizationService.GetString("Acc_LoginKeyNameLabel"),
            PlaceholderText = LocalizationService.GetString("Acc_LoginKeyNamePlaceholder"),
            MaxLength = 40
        };
        var step0Desc = new TextBlock
        {
            Text = LocalizationService.Format("Acc_LoginKeySendOtpDescFmt", acc.CurrentEmail),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        var step0Panel = new StackPanel { Spacing = 10 };
        step0Panel.Children.Add(nameBox);
        step0Panel.Children.Add(step0Desc);

        var otpBox = new TextBox
        {
            Header = LocalizationService.GetString("Acc_RowOtp"),
            PlaceholderText = LocalizationService.GetString("Acc_OtpPlaceholder"),
            MaxLength = 6
        };
        var resendBtn = new Button
        {
            Content = LocalizationService.GetString("Acc_WizardResendBtn"),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var step1Panel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        step1Panel.Children.Add(otpBox);
        step1Panel.Children.Add(resendBtn);

        var feedbackText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };

        var rootPanel = new StackPanel { Spacing = 10, MinWidth = 360, MaxWidth = 420 };
        rootPanel.Children.Add(step0Panel);
        rootPanel.Children.Add(step1Panel);
        rootPanel.Children.Add(feedbackText);

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString("Acc_CreateLoginKeyBtn"),
            Content = rootPanel,
            PrimaryButtonText = LocalizationService.GetString("Acc_WizardSendAndNextBtn"),
            CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        void UpdateCooldownUi()
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

        dlgCooldownTimer.Tick += (_, _) => UpdateCooldownUi();

        async Task<bool> SendOtpAsync()
        {
            var startRes = await acc.StartCreateLoginKeySendCodeAsync();
            if (startRes.Code == OsmiumErrorCode.OtpCooldown)
            {
                var cd = startRes.RetryAfter.HasValue && startRes.RetryAfter.Value.TotalSeconds > 0
                    ? startRes.RetryAfter.Value
                    : TimeSpan.FromSeconds(60);
                dlgCooldownUntil = DateTimeOffset.UtcNow.Add(cd);
                UpdateCooldownUi();
                feedbackText.Text = acc.FormatFriendlyError(startRes.Code, startRes.HttpStatusCode);
                return true;
            }

            if (!startRes.RequiresCaptcha || startRes.Challenge == null)
            {
                feedbackText.Text = acc.FormatFriendlyError(startRes.Code, startRes.HttpStatusCode, startRes.RetryAfter);
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
                UpdateCooldownUi();
                feedbackText.Text = LocalizationService.Format("Acc_OtpSentFmt", acc.CurrentEmail);
                return true;
            }

            feedbackText.Text = acc.FormatFriendlyError(pollRes.Code, pollRes.HttpStatusCode, pollRes.RetryAfter);
            return false;
        }

        resendBtn.Click += async (_, _) =>
        {
            if (dlgSending || dlgCooldownUntil > DateTimeOffset.UtcNow) return;
            dlgSending = true;
            resendBtn.IsEnabled = false;
            try
            {
                await SendOtpAsync();
            }
            finally
            {
                dlgSending = false;
                UpdateCooldownUi();
            }
        };

        LoginKeyCreateData? createdKey = null;

        dlg.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (dlgStep == 0)
                {
                    args.Cancel = true;
                    if (await SendOtpAsync())
                    {
                        dlgStep = 1;
                        step0Panel.Visibility = Visibility.Collapsed;
                        step1Panel.Visibility = Visibility.Visible;
                        dlg.PrimaryButtonText = LocalizationService.GetString("Acc_CreateLoginKeyBtn");
                        dlg.IsPrimaryButtonEnabled = true;
                        DispatcherQueue.TryEnqueue(() => otpBox.Focus(FocusState.Programmatic));
                    }

                    return;
                }

                string code = new string(otpBox.Text.Where(char.IsDigit).ToArray());
                if (code.Length != 6)
                {
                    args.Cancel = true;
                    feedbackText.Text = LocalizationService.GetString("Acc_ErrEnterOtpFirst");
                    return;
                }

                string keyName = string.IsNullOrWhiteSpace(nameBox.Text)
                    ? LocalizationService.GetString("Acc_LoginKeyDefaultName")
                    : nameBox.Text.Trim();

                var createRes = await acc.CreateLoginKeyAsync(code, keyName);
                if (!createRes.Ok || createRes.Data == null || string.IsNullOrWhiteSpace(createRes.Data.LoginKey))
                {
                    args.Cancel = true;
                    feedbackText.Text = acc.FormatFriendlyError(createRes.Code, createRes.HttpStatusCode, createRes.RetryAfter);
                    return;
                }

                createdKey = createRes.Data;
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

        if (createdKey != null)
        {
            await ShowCreatedLoginKeyOnceDialogAsync(createdKey);
            return true;
        }

        return false;
    }

    private async Task ShowCreatedLoginKeyOnceDialogAsync(LoginKeyCreateData keyData)
    {
        if (XamlRoot == null) return;

        var warnText = new TextBlock
        {
            Text = LocalizationService.GetString("Acc_LoginKeyCreatedOnceHint"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        var keyBox = new TextBox
        {
            Text = keyData.LoginKey,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 380, MaxWidth = 440 };
        panel.Children.Add(warnText);
        panel.Children.Add(keyBox);

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString("Acc_LoginKeyCreatedTitle"),
            Content = panel,
            PrimaryButtonText = LocalizationService.GetString("Acc_CopyAndCloseBtn"),
            CloseButtonText = LocalizationService.GetString("Dialog_Close"),
            DefaultButton = ContentDialogButton.Primary
        };

        var res = await dlg.ShowAsync();
        if (res == ContentDialogResult.Primary)
        {
            CopyTextToClipboard(keyData.LoginKey);
            ShowStatus(InfoBarSeverity.Success, LocalizationService.GetString("Acc_MsgLoginKeyCopied"));
        }
    }
}
