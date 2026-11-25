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
    private const int MaxCompressedAvatarBytes = 48 * 1024;
    private const uint TargetAvatarDimension = 128;

    private bool _loading = true;
    private bool _isBusy;
    private int _currentTabIndex;
    private int _selectedAuthMode;
    private int _wizardStep = -1;
    private int _wizardAnimGen;
    private int _tabAnimGen;
    private string? _appliedAvatarDataUri;
    private Storyboard? _avatarFadeStoryboard;
    private IReadOnlyList<AccountRemoteHistoryRecordDto>? _cachedRemoteHistory;
    private readonly Dictionary<string, DateTimeOffset> _otpCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _otpTimer = new();

    public AccountPage()
    {
        InitializeComponent();
        _otpTimer.Interval = TimeSpan.FromSeconds(1);
        _otpTimer.Tick += (_, _) => UpdateOtpCooldownVisuals();

        Loaded += AccountPage_Loaded;
        Unloaded += AccountPage_Unloaded;
        LocalizationService.LanguageChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyLocalization();
            RefreshViewState();
        });
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string tab && !string.IsNullOrWhiteSpace(tab))
        {
            _currentTabIndex = tab switch
            {
                "Devices" => 1,
                "History" => 2,
                "RemoteHistory" => 3,
                _ => 0
            };
        }
    }

    private void AccountPage_Loaded(object sender, RoutedEventArgs e)
    {
        App.Engine.AccountService.SessionChanged -= OnAccountSessionChanged;
        App.Engine.AccountService.SessionChanged += OnAccountSessionChanged;
        App.Engine.AccountService.SameAccountDevicesChanged -= OnSameAccountDevicesChanged;
        App.Engine.AccountService.SameAccountDevicesChanged += OnSameAccountDevicesChanged;
        App.Engine.Coordinator.HostSessionStarted -= OnHostSessionChanged;
        App.Engine.Coordinator.HostSessionStarted += OnHostSessionChanged;
        App.Engine.Coordinator.HostSessionEnded -= OnHostSessionEnded;
        App.Engine.Coordinator.HostSessionEnded += OnHostSessionEnded;

        _loading = true;
        ApplyLocalization();
        RefreshViewState();
        ApplyTabSelection(_currentTabIndex, animate: false);
        _loading = false;

        if (App.Engine.AccountService.IsLoggedIn)
        {
            _ = RefreshAllAccountDataAsync();
        }
        else
        {
            _ = RefreshAuthAvailabilityAsync();
        }
    }

    private void AccountPage_Unloaded(object sender, RoutedEventArgs e)
    {
        App.Engine.AccountService.SessionChanged -= OnAccountSessionChanged;
        App.Engine.AccountService.SameAccountDevicesChanged -= OnSameAccountDevicesChanged;
        App.Engine.Coordinator.HostSessionStarted -= OnHostSessionChanged;
        App.Engine.Coordinator.HostSessionEnded -= OnHostSessionEnded;
        _otpTimer.Stop();
        StopQrLoginSession(cancelRemote: true);
        StopOAuthLoginFlow();
    }

    private void SlideHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            fe.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height))
            };
        }
    }

    private void OnAccountSessionChanged()
        => DispatcherQueue.TryEnqueue(RefreshViewState);

    private void OnSameAccountDevicesChanged(IReadOnlyList<AccountDeviceDto> devices)
        => DispatcherQueue.TryEnqueue(() => RenderDevicesList(devices));

    private void OnHostSessionChanged(Osmium.Core.Network.HostSessionActiveInfo _)
        => DispatcherQueue.TryEnqueue(ApplyHostSecurityLockdown);

    private void OnHostSessionEnded(string _)
        => DispatcherQueue.TryEnqueue(ApplyHostSecurityLockdown);

    private int CurrentAuthMode => _selectedAuthMode;
    private int TotalWizardSteps => CurrentAuthMode == 0 ? 2 : 3;

    private static string MakeOtpCooldownKey(int mode, string email)
        => $"{mode}:{email.Trim().ToLowerInvariant()}";

    private int GetOtpCooldownSeconds(int mode, string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return 0;
        string key = MakeOtpCooldownKey(mode, email);
        if (!_otpCooldowns.TryGetValue(key, out var until)) return 0;
        int rem = (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds);
        if (rem <= 0)
        {
            _otpCooldowns.Remove(key);
            return 0;
        }

        return rem;
    }

    private void SetOtpCooldown(int mode, string email, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(email) || duration.TotalSeconds <= 0) return;
        _otpCooldowns[MakeOtpCooldownKey(mode, email)] = DateTimeOffset.UtcNow.Add(duration);
        if (!_otpTimer.IsEnabled) _otpTimer.Start();
        UpdateOtpCooldownVisuals();
    }

    private StackPanel GetWizardPanelForStep(int mode, int step)
    {
        if (step < 0) return StepPanelModeSelect;
        if (mode == 0)
        {
            return step == 0 ? StepPanelIdentity : StepPanelPassword;
        }

        return step switch
        {
            0 => StepPanelIdentity,
            1 => StepPanelOtp,
            _ => StepPanelPassword
        };
    }

    private TranslateTransform GetWizardTransformForPanel(StackPanel panel)
    {
        if (ReferenceEquals(panel, StepPanelIdentity)) return StepIdentityTransform;
        if (ReferenceEquals(panel, StepPanelOtp)) return StepOtpTransform;
        return StepPasswordTransform;
    }

    private void SnapWizardStepPanels(int mode, int step)
    {
        StackPanel? activeStepPanel = step >= 0 ? GetWizardPanelForStep(mode, step) : null;
        StackPanel[] stepPanels = [StepPanelIdentity, StepPanelOtp, StepPanelPassword];
        foreach (var p in stepPanels)
        {
            bool isActive = ReferenceEquals(p, activeStepPanel);
            p.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
            p.Opacity = 1;
            p.IsHitTestVisible = true;
            GetWizardTransformForPanel(p).X = 0;
        }
    }

    private void ApplyLocalization()
    {
        AccountTitleText.Text = LocalizationService.GetString("Acc_PageTitle");

        ModeLoginTitleText.Text = LocalizationService.GetString("Acc_ModeLogin");
        ModeLoginDescText.Text = LocalizationService.GetString("Acc_ModeLoginDesc");
        ModeQrLoginTitleText.Text = LocalizationService.GetString("Acc_ModeQrLogin");
        ModeQrLoginDescText.Text = LocalizationService.GetString("Acc_ModeQrLoginDesc");
        ModeRegisterTitleText.Text = LocalizationService.GetString("Acc_ModeRegister");
        ModeRegisterDescText.Text = LocalizationService.GetString("Acc_ModeRegisterDesc");
        ModeResetTitleText.Text = LocalizationService.GetString("Acc_ModeReset");
        ModeResetDescText.Text = LocalizationService.GetString("Acc_ModeResetDesc");
        OAuthSectionTitleText.Text = LocalizationService.GetString("Acc_OAuthSectionTitle");
        OAuthGoogleBtnText.Text = LocalizationService.GetString("Acc_OAuthGoogleBtn");
        OAuthQqBtnText.Text = LocalizationService.GetString("Acc_OAuthQqBtn");
        OAuthWechatBtnText.Text = LocalizationService.GetString("Acc_OAuthWechatBtn");

        BtnQrBackText.Text = LocalizationService.GetString("Acc_WizardBackBtn");
        BtnQrRefresh.Content = LocalizationService.GetString("Acc_QrRefreshBtn");
        BtnQrOverlayRefresh.Content = LocalizationService.GetString("Acc_QrRefreshBtn");
        RefreshQrLocalization();

        RowAuthEmail.Title = LocalizationService.GetString("Acc_RowEmail");
        AuthEmailBox.PlaceholderText = LocalizationService.GetString("Acc_EmailPlaceholder");

        RowAuthUsername.Title = LocalizationService.GetString("Acc_RowUsername");
        AuthUsernameBox.PlaceholderText = LocalizationService.GetString("Acc_UsernamePlaceholder");
        BtnRandomAuthUsername.Content = LocalizationService.GetString("Acc_RandomNameBtn");

        RowOtpEmailSummary.Title = LocalizationService.GetString("Acc_WizardCurrentEmail");
        RowPwdEmailSummary.Title = LocalizationService.GetString("Acc_WizardCurrentEmail");
        BtnChangeWizardEmail.Content = LocalizationService.GetString("Acc_WizardChangeBtn");

        RowAuthPassword.Title = LocalizationService.GetString("Acc_RowPassword");
        AuthPasswordBox.PlaceholderText = LocalizationService.GetString("Acc_PasswordPlaceholder");

        RowAuthConfirmPassword.Title = LocalizationService.GetString("Acc_RowConfirmPassword");
        AuthConfirmPasswordBox.PlaceholderText = LocalizationService.GetString("Acc_ConfirmPasswordPlaceholder");

        RowAuthOtp.Title = LocalizationService.GetString("Acc_RowOtp");
        AuthOtpBox.PlaceholderText = LocalizationService.GetString("Acc_OtpPlaceholder");

        BtnWizardBackText.Text = LocalizationService.GetString("Acc_WizardBackBtn");
        BtnHaveOtpSkip.Content = LocalizationService.GetString("Acc_WizardHaveOtpBtn");

        NavTabProfile.Content = LocalizationService.GetString("Acc_TabProfile");
        NavTabDevices.Content = LocalizationService.GetString("Acc_TabDevices");
        NavTabHistory.Content = LocalizationService.GetString("Acc_TabHistory");
        NavTabRemoteHistory.Content = LocalizationService.GetString("Acc_TabRemoteHistory");

        RowProfileAvatar.Title = LocalizationService.GetString("Acc_RowAvatar");
        BtnChangeAvatar.Content = LocalizationService.GetString("Acc_ChangeAvatarBtn");

        RowProfileUsername.Title = LocalizationService.GetString("Acc_RowUsername");
        BtnRandomProfileUsername.Content = LocalizationService.GetString("Acc_RandomNameBtn");
        BtnSaveUsername.Content = LocalizationService.GetString("Acc_SaveBtn");

        RowProfileEmail.Title = LocalizationService.GetString("Acc_RowEmail");
        BtnChangeEmail.Content = LocalizationService.GetString("Acc_ChangeEmailBtn");
        RowSetPassword.Title = LocalizationService.GetString("Acc_RowSetPassword");
        BtnSetPassword.Content = LocalizationService.GetString("Acc_SetPasswordBtn");

        RowAccountLock.Title = LocalizationService.GetString("Acc_RowAccountLock");
        BtnRefreshStatus.Content = LocalizationService.GetString("Gamepad_RefreshBtn");

        RowAccountBlacklist.Title = LocalizationService.GetString("Acc_RowBlacklist");
        BtnManageBlacklist.Content = LocalizationService.GetString("Acc_ManageBlacklistBtn");

        RowAccountSession.Title = LocalizationService.GetString("Acc_RowSessionActions");
        BtnLogout.Content = LocalizationService.GetString("Acc_LogoutBtn");
        BtnDeleteAccount.Content = LocalizationService.GetString("Acc_DeleteAccountBtn");

        RowDevicesHeader.Title = LocalizationService.GetString("Acc_TabDevices");
        BtnRefreshDevices.Content = LocalizationService.GetString("Gamepad_RefreshBtn");

        RowHistoryHeader.Title = LocalizationService.GetString("Acc_TabHistory");
        BtnRefreshHistory.Content = LocalizationService.GetString("Gamepad_RefreshBtn");

        RowRemoteHistoryHeader.Title = LocalizationService.GetString("Acc_TabRemoteHistory");
        RowRemoteHistoryHeader.Description = LocalizationService.GetString("Acc_RemoteHistoryDesc");
        BtnRefreshRemoteHistory.Content = LocalizationService.GetString("Gamepad_RefreshBtn");
        if (_cachedRemoteHistory != null)
        {
            RenderRemoteHistoryList(_cachedRemoteHistory);
        }

        UpdateWizardUiState(snapPanels: true);
        ApplyAuthAvailabilityUi();
        ApplyOAuthBindingsUi();
    }

    private void RefreshViewState()
    {
        var acc = App.Engine.AccountService;
        bool loggedIn = acc.IsLoggedIn;

        UnauthScrollViewer.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        LoggedInRootGrid.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;

        if (!loggedIn)
        {
            _cachedRemoteHistory = null;
            _oauthBindings = null;
            _oauthBindingsLoading = false;
            _oauthBindingsFailed = false;
            ApplyOAuthBindingsUi();
            if (string.IsNullOrWhiteSpace(AuthEmailBox.Text) && !string.IsNullOrWhiteSpace(acc.CurrentEmail))
            {
                AuthEmailBox.Text = acc.CurrentEmail;
            }

            if (_inQrLoginFlow)
            {
                ModeSelectCard.Visibility = Visibility.Collapsed;
                WizardFlowPanel.Visibility = Visibility.Collapsed;
                QrLoginFlowPanel.Visibility = Visibility.Visible;
                QrLoginFlowPanel.Opacity = 1;
                QrLoginFlowTransform.X = 0;
                return;
            }

            if (_wizardStep >= TotalWizardSteps) _wizardStep = -1;
            UpdateWizardUiState(snapPanels: true);
            ApplyAuthAvailabilityUi();
            if (!_authApiChecking && !_authApiReachable && !_authApiFailed)
            {
                _ = RefreshAuthAvailabilityAsync();
            }

            return;
        }

        if (_inQrLoginFlow)
        {
            _inQrLoginFlow = false;
            StopQrLoginSession(cancelRemote: false);
            QrLoginFlowPanel.Visibility = Visibility.Collapsed;
        }

        bool hasEmail = acc.HasEmail;
        bool hasPassword = acc.HasPassword;
        bool profileComplete = acc.IsProfileComplete;

        ProfileEmailText.Text = !hasEmail || string.IsNullOrWhiteSpace(acc.CurrentEmail)
            ? LocalizationService.GetString("Acc_EmailUnbound")
            : acc.CurrentEmail;
        BtnChangeEmail.Content = LocalizationService.GetString(hasEmail ? "Acc_ChangeEmailBtn" : "Acc_AddEmailBtn");

        ProfileIncompleteBar.Visibility = profileComplete ? Visibility.Collapsed : Visibility.Visible;
        ProfileIncompleteBar.IsOpen = !profileComplete;
        if (!profileComplete)
        {
            ProfileIncompleteBar.Title = LocalizationService.GetString("Acc_ProfileIncompleteTitle");
            ProfileIncompleteBar.Message = LocalizationService.GetString(
                !hasEmail ? "Acc_ProfileIncompleteNoEmailDesc" : "Acc_ProfileIncompleteNoPasswordDesc");
        }

        bool showSetPasswordRow = !hasPassword;
        DividerSetPassword.Visibility = showSetPasswordRow ? Visibility.Visible : Visibility.Collapsed;
        RowSetPassword.Visibility = showSetPasswordRow ? Visibility.Visible : Visibility.Collapsed;
        if (showSetPasswordRow)
        {
            RowSetPassword.Description = LocalizationService.GetString(
                hasEmail ? "Acc_SetPasswordReadyDesc" : "Acc_SetPasswordNeedEmailDesc");
        }

        if (!ProfileUsernameBox.FocusState.HasFlag(FocusState.Keyboard) &&
            !string.IsNullOrWhiteSpace(acc.CurrentUsername))
        {
            ProfileUsernameBox.Text = acc.CurrentUsername;
        }

        bool locked = acc.IsLocked;
        AccountLockStatusText.Text = LocalizationService.GetString(locked ? "Acc_StatusLocked" : "Acc_StatusNormal");
        BtnToggleLock.Content = LocalizationService.GetString(locked ? "Acc_UnlockBtn" : "Acc_LockBtn");

        _ = ApplyAvatarPreviewAsync(acc.CurrentAvatarData);
        RenderDevicesList(acc.SameAccountDevices);
        ApplyHostSecurityLockdown();
        if (_oauthBindings == null && !_oauthBindingsLoading && !_oauthBindingsFailed)
        {
            _ = RefreshOAuthBindingsAsync();
        }
    }

    private void ApplyHostSecurityLockdown()
    {
        bool hostControlled = App.Engine.Coordinator.IsHostStreaming;
        var acc = App.Engine.AccountService;
        bool accountLocked = acc.IsLocked;
        bool profileComplete = acc.IsProfileComplete;
        bool hasEmail = acc.HasEmail;
        bool hasPassword = acc.HasPassword;

        BtnChangeAvatar.IsEnabled = !hostControlled && !accountLocked && profileComplete && !_isBusy;
        ProfileUsernameBox.IsEnabled = !hostControlled && !accountLocked && profileComplete && !_isBusy;
        BtnRandomProfileUsername.IsEnabled = !hostControlled && !accountLocked && profileComplete && !_isBusy;
        BtnSaveUsername.IsEnabled = !hostControlled && !accountLocked && profileComplete && !_isBusy;
        BtnChangeEmail.IsEnabled = !hostControlled && !accountLocked && (!hasEmail || profileComplete) && !_isBusy;
        BtnSetPassword.IsEnabled = !hostControlled && !accountLocked && hasEmail && !hasPassword && !_isBusy;
        BtnToggleLock.IsEnabled = !hostControlled && hasEmail && !_isBusy;
        BtnManageBlacklist.IsEnabled = !hostControlled && !_isBusy;
        BtnLogout.IsEnabled = !hostControlled && !_isBusy;
        BtnDeleteAccount.IsEnabled = !hostControlled && !accountLocked && hasPassword && !_isBusy;
        ApplyOAuthBindingsUi();
    }

    private async Task ApplyAvatarPreviewAsync(string? dataUri)
    {
        if (string.IsNullOrWhiteSpace(dataUri) ||
            !dataUri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            _avatarFadeStoryboard?.Stop();
            _avatarFadeStoryboard = null;
            _appliedAvatarDataUri = null;
            AvatarEllipse.Opacity = 0;
            AvatarEllipse.Visibility = Visibility.Collapsed;
            AvatarFallbackIcon.Visibility = Visibility.Visible;
            return;
        }

        if (string.Equals(_appliedAvatarDataUri, dataUri, StringComparison.Ordinal) &&
            AvatarEllipse.Visibility == Visibility.Visible &&
            AvatarEllipse.Opacity >= 0.99)
        {
            return;
        }

        try
        {
            int comma = dataUri.IndexOf(',');
            if (comma < 0) return;
            byte[] bytes = Convert.FromBase64String(dataUri[(comma + 1)..]);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);

            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);

            _appliedAvatarDataUri = dataUri;
            _avatarFadeStoryboard?.Stop();
            AvatarImageBrush.ImageSource = bmp;
            AvatarEllipse.Opacity = 0;
            AvatarEllipse.Visibility = Visibility.Visible;
            AvatarFallbackIcon.Visibility = Visibility.Collapsed;

            var sb = new Storyboard();
            var fadeAnim = new DoubleAnimation
            {
                From = 0,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(280)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeAnim, AvatarEllipse);
            Storyboard.SetTargetProperty(fadeAnim, "Opacity");
            sb.Children.Add(fadeAnim);

            _avatarFadeStoryboard = sb;
            sb.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_avatarFadeStoryboard, sb)) return;
                AvatarEllipse.Opacity = 1.0;
                _avatarFadeStoryboard = null;
            };
            sb.Begin();
        }
        catch
        {
            _avatarFadeStoryboard?.Stop();
            _avatarFadeStoryboard = null;
            _appliedAvatarDataUri = null;
            AvatarEllipse.Opacity = 0;
            AvatarEllipse.Visibility = Visibility.Collapsed;
            AvatarFallbackIcon.Visibility = Visibility.Visible;
        }
    }

    private void ModeChoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanAttemptAuth) return;
        if (sender is not Button btn || !int.TryParse(btn.Tag?.ToString(), out int mode)) return;

        if (mode == 3)
        {
            StatusInfoBar.IsOpen = false;
            EnterQrLoginFlow();
            return;
        }

        _selectedAuthMode = mode;
        AuthPasswordBox.Password = string.Empty;
        AuthConfirmPasswordBox.Password = string.Empty;
        AuthPasswordBox.IsPasswordRevealed = false;
        AuthConfirmPasswordBox.IsPasswordRevealed = false;
        AuthOtpBox.Text = string.Empty;
        StatusInfoBar.IsOpen = false;
        TransitionWizardToStep(0, forward: true);
    }

    private void AuthEmailBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _wizardStep != 0) return;
        UpdateWizardUiState(snapPanels: false);
        UpdateOtpCooldownVisuals();
    }

    private void UpdateWizardUiState(bool snapPanels)
    {
        int mode = CurrentAuthMode;
        bool isLogin = mode == 0;
        bool isRegister = mode == 1;
        bool isReset = mode == 2;
        int totalSteps = TotalWizardSteps;
        if (_wizardStep >= totalSteps) _wizardStep = totalSteps - 1;
        if (_wizardStep < -1) _wizardStep = -1;

        bool isModeSelect = _wizardStep < 0;

        if (!isModeSelect)
        {
            DividerAuthUsername.Visibility = isRegister ? Visibility.Visible : Visibility.Collapsed;
            RowAuthUsername.Visibility = isRegister ? Visibility.Visible : Visibility.Collapsed;
            if (isRegister && string.IsNullOrWhiteSpace(AuthUsernameBox.Text))
            {
                AuthUsernameBox.Text = Osmium.Core.Services.AccountService.GenerateRandomUsername();
            }

            DividerAuthConfirmPwd.Visibility = (isRegister || isReset) ? Visibility.Visible : Visibility.Collapsed;
            RowAuthConfirmPassword.Visibility = (isRegister || isReset) ? Visibility.Visible : Visibility.Collapsed;

            string email = AuthEmailBox.Text.Trim();
            OtpEmailSummaryText.Text = string.IsNullOrEmpty(email) ? "-" : email;
            PwdEmailSummaryText.Text = isRegister && !string.IsNullOrWhiteSpace(AuthUsernameBox.Text)
                ? $"{email} - {AuthUsernameBox.Text.Trim()}"
                : (string.IsNullOrEmpty(email) ? "-" : email);

            StepLabel1.Text = LocalizationService.GetString("Acc_StepAccount");
            if (isLogin)
            {
                StepLabel2.Text = LocalizationService.GetString("Acc_StepPassword");
                StepConnector2.Visibility = Visibility.Collapsed;
                StepNode3.Visibility = Visibility.Collapsed;
            }
            else
            {
                StepLabel2.Text = LocalizationService.GetString("Acc_StepVerify");
                StepLabel3.Text =
                    LocalizationService.GetString(isRegister ? "Acc_StepSetPassword" : "Acc_StepNewPassword");
                StepConnector2.Visibility = Visibility.Visible;
                StepNode3.Visibility = Visibility.Visible;
            }

            StepProgressText.Text = LocalizationService.Format("Acc_StepProgressFmt", _wizardStep + 1, totalSteps);
            ApplyStepNodeStyle(StepBadge1, StepNum1, StepCheck1, StepLabel1, nodeIndex: 0, activeStep: _wizardStep);
            ApplyStepNodeStyle(StepBadge2, StepNum2, StepCheck2, StepLabel2, nodeIndex: 1, activeStep: _wizardStep);
            ApplyStepNodeStyle(StepBadge3, StepNum3, StepCheck3, StepLabel3, nodeIndex: 2, activeStep: _wizardStep);

            var accentBrush = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush;
            var dividerBrush = Application.Current.Resources["OsmiumDividerBrush"] as Brush;
            if (accentBrush != null && dividerBrush != null)
            {
                StepConnector1.Background = _wizardStep >= 1 ? accentBrush : dividerBrush;
                StepConnector2.Background = _wizardStep >= 2 ? accentBrush : dividerBrush;
            }

            BtnWizardBack.Visibility = Visibility.Visible;
            bool showHaveOtpSkip = (isRegister || isReset) && _wizardStep == 0;
            BtnHaveOtpSkip.Visibility = showHaveOtpSkip ? Visibility.Visible : Visibility.Collapsed;
            BtnHaveOtpSkip.IsEnabled = !_isBusy;

            bool isFinalStep = _wizardStep == totalSteps - 1;
            BtnWizardPrimaryIcon.Visibility = isFinalStep ? Visibility.Collapsed : Visibility.Visible;

            if (isLogin)
            {
                BtnWizardPrimaryText.Text =
                    LocalizationService.GetString(_wizardStep == 0 ? "Acc_WizardNextBtn" : "Acc_SubmitLogin");
            }
            else if (_wizardStep == 0)
            {
                BtnWizardPrimaryText.Text = LocalizationService.GetString("Acc_WizardSendAndNextBtn");
            }
            else if (_wizardStep == 1)
            {
                BtnWizardPrimaryText.Text = LocalizationService.GetString("Acc_WizardNextBtn");
            }
            else
            {
                BtnWizardPrimaryText.Text =
                    LocalizationService.GetString(isRegister ? "Acc_SubmitRegister" : "Acc_SubmitReset");
            }
        }

        if (snapPanels)
        {
            ModeSelectCard.Visibility = isModeSelect ? Visibility.Visible : Visibility.Collapsed;
            ModeSelectCard.Opacity = 1;
            ModeSelectCard.IsHitTestVisible = true;
            ModeSelectCardTransform.X = 0;

            WizardFlowPanel.Visibility = isModeSelect ? Visibility.Collapsed : Visibility.Visible;
            WizardFlowPanel.Opacity = 1;
            WizardFlowPanel.IsHitTestVisible = true;
            WizardFlowTransform.X = 0;

            SnapWizardStepPanels(mode, _wizardStep);
        }
    }

    private void ApplyStepNodeStyle(
        Border badge,
        TextBlock numText,
        FontIcon checkIcon,
        TextBlock label,
        int nodeIndex,
        int activeStep)
    {
        bool completed = activeStep > nodeIndex;
        bool active = activeStep == nodeIndex;

        var accentBg = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush;
        var subtleBg = Application.Current.Resources["OsmiumSubtlePillBrush"] as Brush;
        var onAccentFg = Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] as Brush;
        var primaryFg = Application.Current.Resources["TextFillColorPrimaryBrush"] as Brush;
        var secondaryFg = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush;

        if (completed || active)
        {
            if (accentBg != null) badge.Background = accentBg;
            if (onAccentFg != null) numText.Foreground = onAccentFg;
        }
        else
        {
            if (subtleBg != null) badge.Background = subtleBg;
            if (secondaryFg != null) numText.Foreground = secondaryFg;
        }

        numText.Visibility = completed ? Visibility.Collapsed : Visibility.Visible;
        checkIcon.Visibility = completed ? Visibility.Visible : Visibility.Collapsed;

        label.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        if (active || completed)
        {
            if (primaryFg != null) label.Foreground = primaryFg;
        }
        else
        {
            if (secondaryFg != null) label.Foreground = secondaryFg;
        }
    }

    private void TransitionWizardToStep(int targetStep, bool forward)
    {
        int mode = CurrentAuthMode;
        int prevStep = _wizardStep;
        int clamped = Math.Clamp(targetStep, -1, TotalWizardSteps - 1);
        bool wasModeSelect = prevStep < 0;
        bool isNowModeSelect = clamped < 0;

        _wizardStep = clamped;
        UpdateWizardUiState(snapPanels: false);
        UpdateOtpCooldownVisuals();

        int gen = ++_wizardAnimGen;
        if (wasModeSelect != isNowModeSelect)
        {
            SnapWizardStepPanels(mode, clamped);

            UIElement outgoingRoot = wasModeSelect ? ModeSelectCard : WizardFlowPanel;
            TranslateTransform outgoingTransform = wasModeSelect ? ModeSelectCardTransform : WizardFlowTransform;
            UIElement incomingRoot = isNowModeSelect ? ModeSelectCard : WizardFlowPanel;
            TranslateTransform incomingTransform = isNowModeSelect ? ModeSelectCardTransform : WizardFlowTransform;

            AnimateHorizontalSwap(
                outgoingRoot,
                outgoingTransform,
                incomingRoot,
                incomingTransform,
                forward,
                isStillCurrent: () => gen == _wizardAnimGen,
                slideDistance: 48);

            if (!isNowModeSelect)
            {
                DispatcherQueue.TryEnqueue(FocusActiveWizardControl);
            }

            return;
        }

        if (isNowModeSelect)
        {
            ModeSelectCard.Visibility = Visibility.Visible;
            ModeSelectCard.Opacity = 1;
            ModeSelectCardTransform.X = 0;
            WizardFlowPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ModeSelectCard.Visibility = Visibility.Collapsed;
        WizardFlowPanel.Visibility = Visibility.Visible;
        WizardFlowPanel.Opacity = 1;
        WizardFlowTransform.X = 0;

        var fromPanel = GetWizardPanelForStep(mode, prevStep);
        var toPanel = GetWizardPanelForStep(mode, clamped);
        if (ReferenceEquals(fromPanel, toPanel))
        {
            SnapWizardStepPanels(mode, clamped);
            FocusActiveWizardControl();
            return;
        }

        StackPanel[] stepPanels = [StepPanelIdentity, StepPanelOtp, StepPanelPassword];
        foreach (var p in stepPanels)
        {
            if (!ReferenceEquals(p, toPanel))
            {
                p.Visibility = Visibility.Collapsed;
                p.Opacity = 1;
                p.IsHitTestVisible = true;
                GetWizardTransformForPanel(p).X = 0;
            }
        }

        AnimateHorizontalSwap(
            fromPanel,
            GetWizardTransformForPanel(fromPanel),
            toPanel,
            GetWizardTransformForPanel(toPanel),
            forward,
            isStillCurrent: () => gen == _wizardAnimGen,
            slideDistance: 48);

        DispatcherQueue.TryEnqueue(FocusActiveWizardControl);
    }

    private static void AnimateHorizontalSwap(
        UIElement outgoing,
        TranslateTransform outgoingTransform,
        UIElement incoming,
        TranslateTransform incomingTransform,
        bool forward,
        Func<bool> isStillCurrent,
        double slideDistance = 48)
    {
        double inStartX = forward ? slideDistance : -slideDistance;

        outgoing.Visibility = Visibility.Collapsed;
        outgoing.Opacity = 1;
        outgoing.IsHitTestVisible = true;
        outgoingTransform.X = 0;

        incoming.IsHitTestVisible = true;
        incomingTransform.X = inStartX;
        incoming.Opacity = 0;
        incoming.Visibility = Visibility.Visible;

        var sb = new Storyboard();

        var inSlide = new DoubleAnimation
        {
            From = inStartX,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(inSlide, incomingTransform);
        Storyboard.SetTargetProperty(inSlide, "X");

        var inFade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(210)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(inFade, incoming);
        Storyboard.SetTargetProperty(inFade, "Opacity");

        sb.Children.Add(inSlide);
        sb.Children.Add(inFade);

        sb.Completed += (_, _) =>
        {
            if (!isStillCurrent()) return;
            incoming.Opacity = 1;
            incoming.IsHitTestVisible = true;
            incomingTransform.X = 0;
        };

        sb.Begin();
    }

    private void FocusActiveWizardControl()
    {
        var active = GetWizardPanelForStep(CurrentAuthMode, _wizardStep);
        if (ReferenceEquals(active, StepPanelIdentity))
        {
            AuthEmailBox.Focus(FocusState.Programmatic);
        }
        else if (ReferenceEquals(active, StepPanelOtp))
        {
            AuthOtpBox.Focus(FocusState.Programmatic);
        }
        else if (ReferenceEquals(active, StepPanelPassword))
        {
            AuthPasswordBox.Focus(FocusState.Programmatic);
        }
    }

    private void BtnRandomAuthUsername_Click(object sender, RoutedEventArgs e)
        => AuthUsernameBox.Text = Osmium.Core.Services.AccountService.GenerateRandomUsername();

    private void BtnRandomProfileUsername_Click(object sender, RoutedEventArgs e)
        => ProfileUsernameBox.Text = Osmium.Core.Services.AccountService.GenerateRandomUsername();
}