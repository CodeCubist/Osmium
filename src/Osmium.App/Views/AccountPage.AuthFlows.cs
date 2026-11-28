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
    private void UpdateOtpCooldownVisuals()
    {
        var now = DateTimeOffset.UtcNow;
        bool anyActive = false;
        foreach (var kv in _otpCooldowns.ToList())
        {
            if (kv.Value <= now) _otpCooldowns.Remove(kv.Key);
            else anyActive = true;
        }

        if (anyActive)
        {
            if (!_otpTimer.IsEnabled) _otpTimer.Start();
        }
        else
        {
            if (_otpTimer.IsEnabled) _otpTimer.Stop();
        }

        int mode = CurrentAuthMode;
        string email = AuthEmailBox.Text.Trim();
        int remSec = mode is (1 or 2) ? GetOtpCooldownSeconds(mode, email) : 0;

        if (remSec > 0)
        {
            BtnSendAuthOtp.IsEnabled = false;
            BtnSendAuthOtp.Content = LocalizationService.Format("Acc_OtpResendCooldownFmt", remSec);
        }
        else
        {
            BtnSendAuthOtp.IsEnabled = CanAttemptAuth;
            BtnSendAuthOtp.Content = LocalizationService.GetString("Acc_WizardResendBtn");
        }

        if (_wizardStep == 0 && mode is (1 or 2))
        {
            if (remSec > 0)
            {
                BtnWizardPrimary.IsEnabled = false;
                BtnWizardPrimaryText.Text = LocalizationService.Format("Acc_OtpSendCooldownFmt", remSec);
                BtnWizardPrimaryIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                BtnWizardPrimary.IsEnabled = CanAttemptAuth;
                BtnWizardPrimaryText.Text = LocalizationService.GetString("Acc_WizardSendAndNextBtn");
                BtnWizardPrimaryIcon.Visibility = Visibility.Visible;
            }
        }
        else if (_wizardStep >= 0)
        {
            BtnWizardPrimary.IsEnabled = CanAttemptAuth;
        }
    }

    private async Task<bool> SendAuthOtpCoreAsync(int mode, string email)
    {
        var acc = App.Engine.AccountService;
        if (mode != 1) return false;
        var startRes = await acc.StartRegisterSendCodeAsync(email);

        if (startRes.Code == OsmiumErrorCode.OtpCooldown)
        {
            var cd = startRes.RetryAfter.HasValue && startRes.RetryAfter.Value.TotalSeconds > 0
                ? startRes.RetryAfter.Value
                : TimeSpan.FromSeconds(60);
            SetOtpCooldown(mode, email, cd);
            ShowStatus(InfoBarSeverity.Informational, acc.FormatFriendlyError(startRes.Code, startRes.HttpStatusCode));
            return true;
        }

        if (!startRes.RequiresCaptcha || startRes.Challenge == null)
        {
            ShowStatus(InfoBarSeverity.Error,
                acc.FormatFriendlyError(startRes.Code, startRes.HttpStatusCode, startRes.RetryAfter));
            return false;
        }

        var finalRes = await CloudCaptchaWindow.ShowChallengeAsync<AccountOtpSentData>(acc, startRes.Challenge);
        if (finalRes == null)
        {
            ShowStatus(InfoBarSeverity.Informational, LocalizationService.GetString("Conn_CaptchaCancelled"));
            return false;
        }

        if (finalRes.Ok)
        {
            SetOtpCooldown(mode, email, TimeSpan.FromSeconds(60));
            ShowStatus(InfoBarSeverity.Success, LocalizationService.Format("Acc_OtpSentFmt", email));
            return true;
        }

        if (finalRes.Code == OsmiumErrorCode.OtpCooldown)
        {
            var cd = finalRes.RetryAfter.HasValue && finalRes.RetryAfter.Value.TotalSeconds > 0
                ? finalRes.RetryAfter.Value
                : TimeSpan.FromSeconds(60);
            SetOtpCooldown(mode, email, cd);
        }

        ShowStatus(InfoBarSeverity.Error,
            acc.FormatFriendlyError(finalRes.Code, finalRes.HttpStatusCode, finalRes.RetryAfter));
        return false;
    }

    private async void BtnSendAuthOtp_Click(object sender, RoutedEventArgs e)
    {
        if (!CanAttemptAuth) return;
        string email = AuthEmailBox.Text.Trim();
        int mode = CurrentAuthMode;
        if (mode is not (1 or 2)) return;
        if (GetOtpCooldownSeconds(mode, email) > 0) return;
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            ShowStatus(InfoBarSeverity.Warning, LocalizationService.GetString("Acc_ErrInvalidEmail"));
            return;
        }

        SetBusy(true);
        try
        {
            if (await SendAuthOtpCoreAsync(mode, email))
            {
                AuthOtpBox.Focus(FocusState.Programmatic);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BtnHaveOtpSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _wizardStep != 0 || CurrentAuthMode is not (1 or 2)) return;
        string email = AuthEmailBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            ShowStatus(InfoBarSeverity.Warning, LocalizationService.GetString("Acc_ErrInvalidEmail"));
            AuthEmailBox.Focus(FocusState.Programmatic);
            return;
        }

        if (CurrentAuthMode == 1 && string.IsNullOrWhiteSpace(AuthUsernameBox.Text))
        {
            AuthUsernameBox.Text = Osmium.Core.Services.AccountService.GenerateRandomUsername();
        }

        StatusInfoBar.IsOpen = false;
        TransitionWizardToStep(1, forward: true);
    }

    private void BtnWizardBack_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _wizardStep < 0) return;
        StatusInfoBar.IsOpen = false;
        TransitionWizardToStep(_wizardStep - 1, forward: false);
    }

    private void BtnChangeWizardEmail_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        TransitionWizardToStep(0, forward: false);
    }

    private void AuthInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !_isBusy && _wizardStep >= 0)
        {
            e.Handled = true;
            if (BtnWizardPrimary.IsEnabled)
            {
                BtnWizardPrimary_Click(sender, new RoutedEventArgs());
            }
        }
    }

    private async void BtnWizardPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (!CanAttemptAuth || _wizardStep < 0) return;
        int mode = CurrentAuthMode;
        string email = AuthEmailBox.Text.Trim();

        if (_wizardStep == 0)
        {
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            {
                ShowStatus(InfoBarSeverity.Warning, LocalizationService.GetString("Acc_ErrInvalidEmail"));
                AuthEmailBox.Focus(FocusState.Programmatic);
                return;
            }

            if (mode == 0)
            {
                StatusInfoBar.IsOpen = false;
                TransitionWizardToStep(1, forward: true);
                return;
            }

            if (mode == 1 && string.IsNullOrWhiteSpace(AuthUsernameBox.Text))
            {
                AuthUsernameBox.Text = Osmium.Core.Services.AccountService.GenerateRandomUsername();
            }

            if (GetOtpCooldownSeconds(mode, email) > 0)
            {
                UpdateOtpCooldownVisuals();
                return;
            }

            SetBusy(true);
            try
            {
                bool sent = await SendAuthOtpCoreAsync(mode, email);
                if (sent)
                {
                    TransitionWizardToStep(1, forward: true);
                }
            }
            finally
            {
                SetBusy(false);
            }

            return;
        }

        if (mode is (1 or 2) && _wizardStep == 1)
        {
            string otp = AuthOtpBox.Text.Trim();
            if (otp.Length != 6 || !otp.All(char.IsDigit))
            {
                ShowStatus(InfoBarSeverity.Warning, LocalizationService.GetString("Acc_ErrEnterOtpFirst"));
                AuthOtpBox.Focus(FocusState.Programmatic);
                return;
            }

            StatusInfoBar.IsOpen = false;
            TransitionWizardToStep(2, forward: true);
            return;
        }

        string password = AuthPasswordBox.Password;
        if (string.IsNullOrEmpty(password) || password.Length < 8 || password.Length > 128)
        {
            ShowStatus(InfoBarSeverity.Warning, LocalizationService.GetString("Acc_ErrPasswordLength"));
            AuthPasswordBox.Focus(FocusState.Programmatic);
            return;
        }

        if (mode is (1 or 2))
        {
            if (password != AuthConfirmPasswordBox.Password)
            {
                ShowStatus(InfoBarSeverity.Warning, LocalizationService.GetString("Acc_ErrPasswordMismatch"));
                AuthConfirmPasswordBox.Focus(FocusState.Programmatic);
                return;
            }

            if (string.IsNullOrWhiteSpace(AuthOtpBox.Text))
            {
                ShowStatus(InfoBarSeverity.Warning, LocalizationService.GetString("Acc_ErrEnterOtpFirst"));
                TransitionWizardToStep(1, forward: false);
                return;
            }
        }

        var acc = App.Engine.AccountService;
        SetBusy(true);
        try
        {
            if (mode == 0)
            {
                var startRes = await acc.StartLoginAsync(email, password);
                if (!startRes.RequiresCaptcha || startRes.Challenge == null)
                {
                    ShowStatus(InfoBarSeverity.Error, acc.FormatFriendlyError(startRes.Code, startRes.HttpStatusCode));
                    return;
                }

                var loginRes =
                    await CloudCaptchaWindow.ShowChallengeAsync<AccountLoginSessionData>(acc, startRes.Challenge);
                if (loginRes == null)
                {
                    ShowStatus(InfoBarSeverity.Informational, LocalizationService.GetString("Conn_CaptchaCancelled"));
                    return;
                }

                if (loginRes.RequiresTwoFa && loginRes.TwoFaChallenge != null)
                {
                    await ShowTwoFaVerifyDialogAsync(email, loginRes.TwoFaChallenge.TwoFaToken);
                }
                else if (loginRes.Ok && loginRes.Data != null)
                {
                    acc.ApplyLoginSession(email, loginRes.Data);
                    AuthPasswordBox.Password = string.Empty;
                    _wizardStep = -1;
                    ShowStatus(InfoBarSeverity.Success, LocalizationService.GetString("Acc_MsgLoginSuccess"));
                    RefreshViewState();
                    _ = RefreshAllAccountDataAsync();
                }
                else
                {
                    ShowStatus(InfoBarSeverity.Error, acc.FormatFriendlyError(loginRes.Code, loginRes.HttpStatusCode));
                }
            }
            else if (mode == 1)
            {
                string username = string.IsNullOrWhiteSpace(AuthUsernameBox.Text)
                    ? Osmium.Core.Services.AccountService.GenerateRandomUsername()
                    : AuthUsernameBox.Text.Trim();
                var regRes = await acc.ConfirmRegisterAsync(email, username, password, AuthOtpBox.Text);
                if (regRes.Ok)
                {
                    AuthPasswordBox.Password = string.Empty;
                    AuthConfirmPasswordBox.Password = string.Empty;
                    AuthOtpBox.Text = string.Empty;
                    _wizardStep = -1;
                    ShowStatus(InfoBarSeverity.Success, LocalizationService.GetString("Acc_MsgRegisterSuccess"));
                    RefreshViewState();
                    _ = RefreshAllAccountDataAsync();
                }
                else
                {
                    ShowStatus(InfoBarSeverity.Error, acc.FormatFriendlyError(regRes.Code, regRes.HttpStatusCode));
                    if (regRes.Code is OsmiumErrorCode.OtpInvalid or OsmiumErrorCode.OtpExpired
                        or OsmiumErrorCode.OtpTooManyAttempts or OsmiumErrorCode.InvalidOtpFormat)
                    {
                        TransitionWizardToStep(1, forward: false);
                    }
                }
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ShowAccountRecoveryDialogAsync()
    {
        if (XamlRoot == null || _isBusy || !CanAttemptAuth) return;
        var acc = App.Engine.AccountService;

        int dlgStep = 0;
        bool dlgSending = false;
        string recoveryToken = string.Empty;
        List<RecoveryChannelDto> channels = [];
        DateTimeOffset dlgCooldownUntil = DateTimeOffset.MinValue;
        var dlgCooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        var stepIndicator = new TextBlock
        {
            Text = LocalizationService.Format("Acc_StepProgressFmt", 1, 3),
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };

        var lookupDesc = new TextBlock
        {
            Text = LocalizationService.GetString("Acc_RecoveryStep0Desc"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        var lookupEmailBox = new TextBox
        {
            Header = LocalizationService.GetString("Acc_RowEmail"),
            PlaceholderText = LocalizationService.GetString("Acc_EmailPlaceholder"),
            InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.EmailSmtpAddress) } }
        };
        var step0Panel = new StackPanel { Spacing = 10 };
        step0Panel.Children.Add(lookupDesc);
        step0Panel.Children.Add(lookupEmailBox);

        var channelCombo = new ComboBox
        {
            Header = LocalizationService.GetString("Acc_RecoveryChannelHeader"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var actionCombo = new ComboBox
        {
            Header = LocalizationService.GetString("Acc_RecoveryActionHeader"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };
        var maskedHintText = new TextBlock
        {
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        var confirmEmailBox = new TextBox
        {
            Header = LocalizationService.GetString("Acc_ConfirmEmailHeader"),
            PlaceholderText = LocalizationService.GetString("Acc_ConfirmEmailPlaceholder"),
            InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.EmailSmtpAddress) } }
        };
        var step1Panel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        step1Panel.Children.Add(channelCombo);
        step1Panel.Children.Add(actionCombo);
        step1Panel.Children.Add(maskedHintText);
        step1Panel.Children.Add(confirmEmailBox);

        var otpSummaryText = new TextBlock
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
        var newPwdBox = new Controls.TogglePasswordBox
        {
            Header = LocalizationService.GetString("Acc_RowPassword"),
            PlaceholderText = LocalizationService.GetString("Acc_PasswordPlaceholder")
        };
        var confirmPwdBox = new Controls.TogglePasswordBox
        {
            Header = LocalizationService.GetString("Acc_RowConfirmPassword"),
            PlaceholderText = LocalizationService.GetString("Acc_ConfirmPasswordPlaceholder")
        };
        var resendBtn = new Button
        {
            Content = LocalizationService.GetString("Acc_WizardResendBtn"),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var step2Panel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        step2Panel.Children.Add(otpSummaryText);
        step2Panel.Children.Add(otpBox);
        step2Panel.Children.Add(newPwdBox);
        step2Panel.Children.Add(confirmPwdBox);
        step2Panel.Children.Add(resendBtn);

        var feedbackText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
        };

        var rootPanel = new StackPanel { Spacing = 10, MinWidth = 360, MaxWidth = 420 };
        rootPanel.Children.Add(stepIndicator);
        rootPanel.Children.Add(step0Panel);
        rootPanel.Children.Add(step1Panel);
        rootPanel.Children.Add(step2Panel);
        rootPanel.Children.Add(feedbackText);

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString("Acc_RecoveryTitle"),
            Content = rootPanel,
            PrimaryButtonText = LocalizationService.GetString("Acc_RecoveryLookupBtn"),
            CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        RecoveryChannelDto? GetSelectedChannel()
        {
            int idx = channelCombo.SelectedIndex;
            if (idx >= 0 && idx < channels.Count) return channels[idx];
            return channels.FirstOrDefault();
        }

        bool IsRecoveryLoginSelected()
        {
            var ch = GetSelectedChannel();
            return ch != null &&
                   string.Equals(ch.Channel, "recovery", StringComparison.OrdinalIgnoreCase) &&
                   actionCombo.SelectedIndex == 1;
        }

        void RefreshChannelSelectionUi()
        {
            var ch = GetSelectedChannel();
            if (ch == null) return;
            bool isRec = string.Equals(ch.Channel, "recovery", StringComparison.OrdinalIgnoreCase);
            actionCombo.Visibility = isRec ? Visibility.Visible : Visibility.Collapsed;
            if (!isRec)
            {
                actionCombo.SelectedIndex = 0;
            }
            maskedHintText.Text = LocalizationService.Format("Acc_ConfirmEmailHintFmt", ch.Masked);
            confirmEmailBox.Text = string.Empty;
        }

        channelCombo.SelectionChanged += (_, _) => RefreshChannelSelectionUi();

        void UpdateCooldownUi()
        {
            int rem = (int)Math.Max(0, Math.Ceiling((dlgCooldownUntil - DateTimeOffset.UtcNow).TotalSeconds));
            if (rem > 0)
            {
                if (!dlgCooldownTimer.IsEnabled) dlgCooldownTimer.Start();
                resendBtn.IsEnabled = false;
                resendBtn.Content = LocalizationService.Format("Acc_OtpResendCooldownFmt", rem);
                if (dlgStep == 1)
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
                if (dlgStep == 1)
                {
                    dlg.PrimaryButtonText = LocalizationService.GetString("Acc_WizardSendAndNextBtn");
                    dlg.IsPrimaryButtonEnabled = !dlgSending;
                }
            }
        }

        dlgCooldownTimer.Tick += (_, _) => UpdateCooldownUi();

        void SetStep(int step)
        {
            dlgStep = step;
            stepIndicator.Text = LocalizationService.Format("Acc_StepProgressFmt", step + 1, 3);
            step0Panel.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
            step1Panel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
            step2Panel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
            dlg.SecondaryButtonText = step > 0 ? LocalizationService.GetString("Acc_WizardBackBtn") : string.Empty;

            if (step == 0)
            {
                dlg.PrimaryButtonText = LocalizationService.GetString("Acc_RecoveryLookupBtn");
                dlg.IsPrimaryButtonEnabled = true;
            }
            else if (step == 1)
            {
                dlg.PrimaryButtonText = LocalizationService.GetString("Acc_WizardSendAndNextBtn");
                UpdateCooldownUi();
            }
            else
            {
                bool isLogin = IsRecoveryLoginSelected();
                newPwdBox.Visibility = isLogin ? Visibility.Collapsed : Visibility.Visible;
                confirmPwdBox.Visibility = isLogin ? Visibility.Collapsed : Visibility.Visible;
                var ch = GetSelectedChannel();
                otpSummaryText.Text = LocalizationService.Format("Acc_OtpSentFmt", ch?.Masked ?? string.Empty);
                dlg.PrimaryButtonText = LocalizationService.GetString(isLogin ? "Acc_RecoveryConfirmBtn" : "Acc_SubmitReset");
                dlg.IsPrimaryButtonEnabled = true;
                UpdateCooldownUi();
                DispatcherQueue.TryEnqueue(() => otpBox.Focus(FocusState.Programmatic));
            }
        }

        async Task<bool> TriggerRecoveryOtpAsync()
        {
            var ch = GetSelectedChannel();
            if (ch == null || string.IsNullOrWhiteSpace(recoveryToken))
            {
                feedbackText.Text = LocalizationService.GetString("Acc_ErrRecoverySessionExpired");
                return false;
            }

            string confirmMail = confirmEmailBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(confirmMail) || !confirmMail.Contains('@'))
            {
                feedbackText.Text = LocalizationService.GetString("Acc_ErrInvalidEmail");
                return false;
            }

            bool isLogin = IsRecoveryLoginSelected();
            var startRes = isLogin
                ? await acc.StartRecoveryLoginSendCodeAsync(recoveryToken, confirmMail)
                : await acc.StartResetPasswordSendCodeAsync(recoveryToken, ch.Channel, confirmMail);

            if (startRes.Code == OsmiumErrorCode.OtpCooldown)
            {
                var cd = startRes.RetryAfter.HasValue && startRes.RetryAfter.Value.TotalSeconds > 0
                    ? startRes.RetryAfter.Value
                    : TimeSpan.FromSeconds(60);
                dlgCooldownUntil = DateTimeOffset.UtcNow.Add(cd);
                UpdateCooldownUi();
                feedbackText.Text = acc.FormatFriendlyError(startRes.Code, startRes.HttpStatusCode, startRes.RetryAfter);
                return true;
            }

            if (!startRes.RequiresCaptcha || startRes.Challenge == null)
            {
                feedbackText.Text = acc.FormatFriendlyError(startRes.Code, startRes.HttpStatusCode, startRes.RetryAfter);
                if (startRes.Code is OsmiumErrorCode.RecoverySessionExpired or OsmiumErrorCode.EmailConfirmTooManyAttempts)
                {
                    recoveryToken = string.Empty;
                    SetStep(0);
                }
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
                feedbackText.Text = string.Empty;
                return true;
            }

            if (pollRes.Code == OsmiumErrorCode.OtpCooldown)
            {
                var cd = pollRes.RetryAfter.HasValue && pollRes.RetryAfter.Value.TotalSeconds > 0
                    ? pollRes.RetryAfter.Value
                    : TimeSpan.FromSeconds(60);
                dlgCooldownUntil = DateTimeOffset.UtcNow.Add(cd);
                UpdateCooldownUi();
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
                await TriggerRecoveryOtpAsync();
            }
            finally
            {
                dlgSending = false;
                UpdateCooldownUi();
            }
        };

        dlg.SecondaryButtonClick += (_, args) =>
        {
            if (dlgStep > 0)
            {
                args.Cancel = true;
                feedbackText.Text = string.Empty;
                SetStep(dlgStep - 1);
            }
        };

        bool resetSucceeded = false;
        bool loginSucceeded = false;
        string? pendingTwoFaToken = null;

        dlg.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                feedbackText.Text = string.Empty;
                if (dlgStep == 0)
                {
                    args.Cancel = true;
                    string email = lookupEmailBox.Text.Trim();
                    if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
                    {
                        feedbackText.Text = LocalizationService.GetString("Acc_ErrInvalidEmail");
                        return;
                    }

                    var lookupRes = await acc.LookupAccountRecoveryAsync(email);
                    if (!lookupRes.Ok || lookupRes.Data == null || lookupRes.Data.Channels.Count == 0)
                    {
                        feedbackText.Text = acc.FormatFriendlyError(lookupRes.Code, lookupRes.HttpStatusCode, lookupRes.RetryAfter);
                        return;
                    }

                    recoveryToken = lookupRes.Data.RecoveryToken;
                    channels = lookupRes.Data.Channels;

                    channelCombo.Items.Clear();
                    foreach (var ch in channels)
                    {
                        string fmtKey = string.Equals(ch.Channel, "recovery", StringComparison.OrdinalIgnoreCase)
                            ? "Acc_RecoveryChannelRecoveryFmt"
                            : "Acc_RecoveryChannelPrimaryFmt";
                        channelCombo.Items.Add(LocalizationService.Format(fmtKey, ch.Masked));
                    }

                    actionCombo.Items.Clear();
                    actionCombo.Items.Add(LocalizationService.GetString("Acc_RecoveryActionReset"));
                    actionCombo.Items.Add(LocalizationService.GetString("Acc_RecoveryActionLogin"));
                    actionCombo.SelectedIndex = 0;

                    channelCombo.SelectedIndex = 0;
                    RefreshChannelSelectionUi();
                    SetStep(1);
                    return;
                }

                if (dlgStep == 1)
                {
                    args.Cancel = true;
                    if (await TriggerRecoveryOtpAsync())
                    {
                        SetStep(2);
                    }
                    return;
                }

                string otp = otpBox.Text.Trim();
                if (otp.Length != 6 || !otp.All(char.IsDigit))
                {
                    args.Cancel = true;
                    feedbackText.Text = LocalizationService.GetString("Acc_ErrEnterOtpFirst");
                    return;
                }

                var selectedCh = GetSelectedChannel();
                if (selectedCh == null || string.IsNullOrWhiteSpace(recoveryToken))
                {
                    args.Cancel = true;
                    feedbackText.Text = LocalizationService.GetString("Acc_ErrRecoverySessionExpired");
                    SetStep(0);
                    return;
                }

                string confirmMail = confirmEmailBox.Text.Trim();
                if (IsRecoveryLoginSelected())
                {
                    var loginRes = await acc.ConfirmRecoveryLoginAsync(recoveryToken, confirmMail, otp);
                    if (loginRes.RequiresTwoFa && loginRes.TwoFaChallenge != null)
                    {
                        pendingTwoFaToken = loginRes.TwoFaChallenge.TwoFaToken;
                        return;
                    }

                    if (loginRes.Ok && acc.IsLoggedIn)
                    {
                        loginSucceeded = true;
                        return;
                    }

                    args.Cancel = true;
                    feedbackText.Text = acc.FormatFriendlyError(loginRes.Code, loginRes.HttpStatusCode, loginRes.RetryAfter);
                    if (loginRes.Code == OsmiumErrorCode.RecoverySessionExpired)
                    {
                        recoveryToken = string.Empty;
                        SetStep(0);
                    }
                    return;
                }

                string newPwd = newPwdBox.Password;
                if (string.IsNullOrEmpty(newPwd) || newPwd.Length < 8 || newPwd.Length > 128)
                {
                    args.Cancel = true;
                    feedbackText.Text = LocalizationService.GetString("Acc_ErrPasswordLength");
                    return;
                }

                if (newPwd != confirmPwdBox.Password)
                {
                    args.Cancel = true;
                    feedbackText.Text = LocalizationService.GetString("Acc_ErrPasswordMismatch");
                    return;
                }

                var rstRes = await acc.ConfirmResetPasswordAsync(recoveryToken, selectedCh.Channel, confirmMail, otp, newPwd);
                if (!rstRes.Ok)
                {
                    args.Cancel = true;
                    feedbackText.Text = acc.FormatFriendlyError(rstRes.Code, rstRes.HttpStatusCode, rstRes.RetryAfter);
                    if (rstRes.Code == OsmiumErrorCode.RecoverySessionExpired)
                    {
                        recoveryToken = string.Empty;
                        SetStep(0);
                    }
                    return;
                }

                resetSucceeded = true;
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

        if (!string.IsNullOrWhiteSpace(pendingTwoFaToken))
        {
            await ShowTwoFaVerifyDialogAsync(string.Empty, pendingTwoFaToken);
        }
        else if (loginSucceeded)
        {
            _wizardStep = -1;
            ShowStatus(InfoBarSeverity.Success, LocalizationService.GetString("Acc_MsgLoginSuccess"));
            RefreshViewState();
            _ = RefreshAllAccountDataAsync();
        }
        else if (resetSucceeded)
        {
            _selectedAuthMode = 0;
            _wizardStep = 0;
            UpdateWizardUiState(snapPanels: true);
            ShowStatus(InfoBarSeverity.Success, LocalizationService.GetString("Acc_MsgResetSuccess"));
        }
    }

    public void SelectTab(string tab)
    {
        int idx = tab switch
        {
            "Devices" => 1,
            "History" => 2,
            "RemoteHistory" => 3,
            _ => 0
        };
        ApplyTabSelection(idx, animate: true);
    }

    private void AccountNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_loading) return;
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            int idx = tag switch
            {
                "Devices" => 1,
                "History" => 2,
                "RemoteHistory" => 3,
                _ => 0
            };
            ApplyTabSelection(idx, animate: true);
        }
    }

    private StackPanel GetTabPanel(int index) => index switch
    {
        1 => PanelDevices,
        2 => PanelHistory,
        3 => PanelRemoteHistory,
        _ => PanelProfile
    };

    private TranslateTransform GetTabTransform(int index) => index switch
    {
        1 => PanelDevicesTransform,
        2 => PanelHistoryTransform,
        3 => PanelRemoteHistoryTransform,
        _ => PanelProfileTransform
    };

    private void ApplyTabSelection(int index, bool animate)
    {
        int prevIndex = _currentTabIndex;
        _currentTabIndex = index;

        var targetItem = index switch
        {
            1 => NavTabDevices,
            2 => NavTabHistory,
            3 => NavTabRemoteHistory,
            _ => NavTabProfile
        };
        if (!ReferenceEquals(AccountNavView.SelectedItem, targetItem))
        {
            AccountNavView.SelectedItem = targetItem;
        }

        if (animate && prevIndex != index && LoggedInRootGrid.Visibility == Visibility.Visible)
        {
            int gen = ++_tabAnimGen;
            var outgoing = GetTabPanel(prevIndex);
            var incoming = GetTabPanel(index);
            for (int i = 0; i < 4; i++)
            {
                if (i != prevIndex && i != index)
                {
                    var p = GetTabPanel(i);
                    p.Visibility = Visibility.Collapsed;
                    p.Opacity = 1;
                    GetTabTransform(i).X = 0;
                }
            }

            AnimateHorizontalSwap(
                outgoing,
                GetTabTransform(prevIndex),
                incoming,
                GetTabTransform(index),
                forward: index > prevIndex,
                isStillCurrent: () => gen == _tabAnimGen,
                slideDistance: 36);
        }
        else
        {
            for (int i = 0; i < 4; i++)
            {
                var p = GetTabPanel(i);
                p.Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
                p.Opacity = 1;
                GetTabTransform(i).X = 0;
            }
        }

        if (App.Engine.AccountService.IsLoggedIn)
        {
            if (index == 1) _ = LoadDevicesTabAsync();
            else if (index == 2) _ = LoadHistoryTabAsync();
            else if (index == 3) _ = LoadRemoteHistoryTabAsync();
        }
    }

    private async Task RefreshAllAccountDataAsync()
    {
        await App.Engine.AccountService.GetAccountStatusAsync();
        await RefreshOAuthBindingsAsync();
        await LoadDevicesTabAsync();
        if (_currentTabIndex == 2)
        {
            await LoadHistoryTabAsync();
        }
        else if (_currentTabIndex == 3)
        {
            await LoadRemoteHistoryTabAsync();
        }
    }

    private async void BtnChangeAvatar_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || App.Engine.Coordinator.IsHostStreaming) return;
        string? filePath = PickImageFileWin32();
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

        SetBusy(true);
        try
        {
            string? compressedDataUri = await CompressAvatarToDataUriAsync(filePath);
            if (string.IsNullOrEmpty(compressedDataUri))
            {
                ShowStatus(InfoBarSeverity.Error, LocalizationService.GetString("Acc_ErrInvalidAvatar"));
                return;
            }

            var acc = App.Engine.AccountService;
            var res = await acc.UpdateProfileAsync(acc.CurrentUsername, compressedDataUri);
            if (res.Ok)
            {
                await ApplyAvatarPreviewAsync(compressedDataUri);
                ShowStatus(InfoBarSeverity.Success, LocalizationService.GetString("Acc_MsgProfileUpdated"));
            }
            else
            {
                ShowStatus(InfoBarSeverity.Error,
                    acc.FormatFriendlyError(res.Code, res.HttpStatusCode, res.RetryAfter));
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static async Task<string?> CompressAvatarToDataUriAsync(string filePath)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
            using IRandomAccessStream inputStream = await file.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(inputStream);

            uint srcW = decoder.PixelWidth;
            uint srcH = decoder.PixelHeight;
            if (srcW == 0 || srcH == 0) return null;

            double scale = Math.Max((double)TargetAvatarDimension / srcW, (double)TargetAvatarDimension / srcH);
            uint scaledW = Math.Max(TargetAvatarDimension, (uint)Math.Round(srcW * scale));
            uint scaledH = Math.Max(TargetAvatarDimension, (uint)Math.Round(srcH * scale));
            uint cropX = (scaledW - TargetAvatarDimension) / 2;
            uint cropY = (scaledH - TargetAvatarDimension) / 2;

            var transform = new BitmapTransform
            {
                ScaledWidth = scaledW,
                ScaledHeight = scaledH,
                InterpolationMode = BitmapInterpolationMode.Fant,
                Bounds = new BitmapBounds
                {
                    X = cropX,
                    Y = cropY,
                    Width = TargetAvatarDimension,
                    Height = TargetAvatarDimension
                }
            };

            PixelDataProvider pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
            byte[] pixels = pixelData.DetachPixelData();

            double[] qualities = [0.82, 0.68, 0.52];
            foreach (double q in qualities)
            {
                using var outStream = new InMemoryRandomAccessStream();
                var propSet = new BitmapPropertySet
                {
                    { "ImageQuality", new BitmapTypedValue(q, Windows.Foundation.PropertyType.Single) }
                };
                BitmapEncoder encoder =
                    await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream, propSet);
                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Ignore,
                    TargetAvatarDimension,
                    TargetAvatarDimension,
                    96,
                    96,
                    pixels);
                await encoder.FlushAsync();

                if (outStream.Size <= MaxCompressedAvatarBytes)
                {
                    byte[] encoded = new byte[outStream.Size];
                    outStream.Seek(0);
                    await outStream.ReadAsync(encoded.AsBuffer(), (uint)encoded.Length, InputStreamOptions.None);
                    return "data:image/jpeg;base64," + Convert.ToBase64String(encoded);
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? PickImageFileWin32()
    {
        IntPtr hwnd = App.MainWindowInstance != null
            ? WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance)
            : IntPtr.Zero;

        var ofn = new OpenFileName
        {
            lStructSize = Marshal.SizeOf<OpenFileName>(),
            hwndOwner = hwnd,
            lpstrFilter = "Images\0*.png;*.jpg;*.jpeg;*.webp;*.bmp\0\0",
            lpstrFile = new string('\0', 520),
            nMaxFile = 520,
            Flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008
        };
        if (GetOpenFileNameW(ref ofn))
        {
            int nul = ofn.lpstrFile.IndexOf('\0');
            return nul >= 0 ? ofn.lpstrFile[..nul] : ofn.lpstrFile;
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public string lpstrFile;
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName lpofn);

    private async void BtnSaveUsername_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || App.Engine.Coordinator.IsHostStreaming) return;
        string nextName = ProfileUsernameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(nextName))
        {
            ShowStatus(InfoBarSeverity.Warning, LocalizationService.GetString("Acc_ErrInvalidUsername"));
            return;
        }

        SetBusy(true);
        try
        {
            var acc = App.Engine.AccountService;
            var res = await acc.UpdateProfileAsync(nextName, acc.CurrentAvatarData);
            if (res.Ok)
            {
                ShowStatus(InfoBarSeverity.Success, LocalizationService.GetString("Acc_MsgProfileUpdated"));
                RefreshViewState();
            }
            else
            {
                ShowStatus(InfoBarSeverity.Error,
                    acc.FormatFriendlyError(res.Code, res.HttpStatusCode, res.RetryAfter));
            }
        }
        finally
        {
            SetBusy(false);
        }
    }
}