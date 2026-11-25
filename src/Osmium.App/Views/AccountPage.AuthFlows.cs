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
        var startRes = mode == 1
            ? await acc.StartRegisterSendCodeAsync(email)
            : await acc.StartResetPasswordSendCodeAsync(email);

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

                if (loginRes.Ok && loginRes.Data != null)
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
            else
            {
                var rstRes = await acc.ConfirmResetPasswordAsync(email, AuthOtpBox.Text, password);
                if (rstRes.Ok)
                {
                    AuthPasswordBox.Password = string.Empty;
                    AuthConfirmPasswordBox.Password = string.Empty;
                    AuthOtpBox.Text = string.Empty;
                    _selectedAuthMode = 0;
                    _wizardStep = 0;
                    UpdateWizardUiState(snapPanels: true);
                    TransitionWizardToStep(1, forward: true);
                    ShowStatus(InfoBarSeverity.Success, LocalizationService.GetString("Acc_MsgResetSuccess"));
                }
                else
                {
                    ShowStatus(InfoBarSeverity.Error,
                        acc.FormatFriendlyError(rstRes.Code, rstRes.HttpStatusCode, rstRes.RetryAfter));
                    if (rstRes.Code is OsmiumErrorCode.OtpInvalid or OsmiumErrorCode.OtpExpired
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
                ShowStatus(InfoBarSeverity.Error, acc.FormatFriendlyError(res.Code, res.HttpStatusCode, res.RetryAfter));
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
                ShowStatus(InfoBarSeverity.Error, acc.FormatFriendlyError(res.Code, res.HttpStatusCode, res.RetryAfter));
            }
        }
        finally
        {
            SetBusy(false);
        }
    }
}