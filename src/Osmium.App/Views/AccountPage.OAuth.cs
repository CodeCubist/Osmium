using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Osmium.App.Services;
using Osmium.Core.Models;

namespace Osmium.App.Views;

public sealed partial class AccountPage : Page
{
    private CancellationTokenSource? _oauthCts;
    private bool _authApiChecking;
    private bool _authApiReachable;
    private bool _authApiFailed;
    private bool _oauthGoogleEnabled;
    private bool _oauthQqEnabled;
    private bool _oauthWechatEnabled;
    private int _oauthProbeGen;
    private OAuthBindingsData? _oauthBindings;
    private bool _oauthBindingsLoading;
    private bool _oauthBindingsFailed;
    private int _oauthBindingsGen;

    private bool CanAttemptAuth => !_isBusy && _authApiReachable && !_authApiChecking;

    private bool IsOAuthProviderAvailable(string provider) => provider switch
    {
        "google" => _oauthGoogleEnabled,
        "qq" => _oauthQqEnabled,
        "wechat" => _oauthWechatEnabled,
        _ => false
    };

    private void BtnRetryAuthApi_Click(object sender, RoutedEventArgs e)
    {
        if (_authApiChecking || _isBusy) return;
        _ = RefreshAuthAvailabilityAsync();
    }

    private async Task RefreshAuthAvailabilityAsync()
    {
        if (App.Engine.AccountService.IsLoggedIn) return;
        int gen = ++_oauthProbeGen;
        _authApiChecking = true;
        _authApiReachable = false;
        _authApiFailed = false;
        _oauthGoogleEnabled = false;
        _oauthQqEnabled = false;
        _oauthWechatEnabled = false;
        ApplyAuthAvailabilityUi();

        var res = await App.Engine.AccountService.GetOAuthProvidersAsync();
        if (gen != _oauthProbeGen) return;

        _authApiChecking = false;
        if (res.Ok && res.Data != null)
        {
            _authApiReachable = true;
            _authApiFailed = false;
            _oauthGoogleEnabled = res.Data.Google;
            _oauthQqEnabled = res.Data.Qq;
            _oauthWechatEnabled = res.Data.Wechat;
        }
        else
        {
            _authApiReachable = false;
            _authApiFailed = true;
            _oauthGoogleEnabled = false;
            _oauthQqEnabled = false;
            _oauthWechatEnabled = false;
            if (_inQrLoginFlow)
            {
                _inQrLoginFlow = false;
                StopQrLoginSession(cancelRemote: false);
                QrLoginFlowPanel.Visibility = Visibility.Collapsed;
            }

            if (_wizardStep >= 0)
            {
                _wizardStep = -1;
                UpdateWizardUiState(snapPanels: true);
            }
        }

        ApplyAuthAvailabilityUi();
    }

    private void ApplyAuthAvailabilityUi()
    {
        if (BtnChooseLogin == null) return;
        bool loggedIn = App.Engine.AccountService.IsLoggedIn;
        bool showUnavailable = !loggedIn && _authApiFailed;
        AuthApiUnavailableBar.Visibility = showUnavailable ? Visibility.Visible : Visibility.Collapsed;
        AuthApiUnavailableBar.IsOpen = showUnavailable;
        if (showUnavailable)
        {
            AuthApiUnavailableBar.Title = LocalizationService.GetString("Acc_AuthServiceUnavailableTitle");
            AuthApiUnavailableBar.Message = LocalizationService.GetString("Acc_AuthServiceUnavailableDesc");
        }

        BtnRetryAuthApi.Content = LocalizationService.GetString("Acc_RetryBtn");
        BtnRetryAuthApi.IsEnabled = !_authApiChecking && !_isBusy;

        bool canAuth = CanAttemptAuth;
        BtnChooseLogin.IsEnabled = canAuth;
        BtnChooseQrLogin.IsEnabled = canAuth;
        BtnChooseRegister.IsEnabled = canAuth;
        BtnChooseReset.IsEnabled = canAuth;

        var visibleButtons = new List<Button>(3);
        if (_authApiReachable && _oauthGoogleEnabled) visibleButtons.Add(BtnOAuthGoogle);
        if (_authApiReachable && _oauthQqEnabled) visibleButtons.Add(BtnOAuthQq);
        if (_authApiReachable && _oauthWechatEnabled) visibleButtons.Add(BtnOAuthWechat);

        BtnOAuthGoogle.Visibility = (_authApiReachable && _oauthGoogleEnabled) ? Visibility.Visible : Visibility.Collapsed;
        BtnOAuthQq.Visibility = (_authApiReachable && _oauthQqEnabled) ? Visibility.Visible : Visibility.Collapsed;
        BtnOAuthWechat.Visibility = (_authApiReachable && _oauthWechatEnabled) ? Visibility.Visible : Visibility.Collapsed;

        BtnOAuthGoogle.IsEnabled = canAuth && _oauthGoogleEnabled;
        BtnOAuthQq.IsEnabled = canAuth && _oauthQqEnabled;
        BtnOAuthWechat.IsEnabled = canAuth && _oauthWechatEnabled;

        if (visibleButtons.Count == 0)
        {
            OAuthSectionDivider.Visibility = Visibility.Collapsed;
            OAuthSectionPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            OAuthButtonsGrid.ColumnDefinitions.Clear();
            for (int i = 0; i < visibleButtons.Count; i++)
            {
                OAuthButtonsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(visibleButtons[i], i);
            }

            OAuthSectionDivider.Visibility = Visibility.Visible;
            OAuthSectionPanel.Visibility = Visibility.Visible;
        }

        UpdateOtpCooldownVisuals();
    }

    private void BtnOAuthGoogle_Click(object sender, RoutedEventArgs e)
        => _ = StartOAuthLoginFlowAsync("google");

    private void BtnOAuthQq_Click(object sender, RoutedEventArgs e)
        => _ = StartOAuthLoginFlowAsync("qq");

    private void BtnOAuthWechat_Click(object sender, RoutedEventArgs e)
        => _ = StartOAuthLoginFlowAsync("wechat");

    private void StopOAuthLoginFlow()
    {
        var oldCts = _oauthCts;
        _oauthCts = null;
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch
        {
        }
    }

    private static string GetOAuthProviderDisplayName(string provider) => provider switch
    {
        "google" => LocalizationService.GetString("Acc_OAuthGoogleBtn"),
        "qq" => LocalizationService.GetString("Acc_OAuthQqBtn"),
        "wechat" => LocalizationService.GetString("Acc_OAuthWechatBtn"),
        _ => provider
    };

    private static void OpenUrlInSystemBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private async Task StartOAuthLoginFlowAsync(string provider)
    {
        if (!CanAttemptAuth || !IsOAuthProviderAvailable(provider) || XamlRoot == null) return;
        var acc = App.Engine.AccountService;
        SetBusy(true);
        StatusInfoBar.IsOpen = false;
        OAuthStartData? session = null;
        try
        {
            var startRes = await acc.StartOAuthLoginAsync(provider);
            if (!startRes.Ok || startRes.Data == null || string.IsNullOrWhiteSpace(startRes.Data.AuthorizeUrl))
            {
                ShowStatus(InfoBarSeverity.Error,
                    acc.FormatFriendlyError(startRes.Code, startRes.HttpStatusCode, startRes.RetryAfter));
                return;
            }

            session = startRes.Data;
        }
        finally
        {
            SetBusy(false);
        }

        OpenUrlInSystemBrowser(session.AuthorizeUrl);

        string providerName = GetOAuthProviderDisplayName(provider);
        var ring = new ProgressRing
        {
            Width = 28,
            Height = 28,
            IsActive = true,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var waitingText = new TextBlock
        {
            Text = LocalizationService.Format("Acc_OAuthWaitingDescFmt", providerName),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 360
        };
        var tipText = new TextBlock
        {
            Text = LocalizationService.GetString("Acc_OAuthBrowserTip"),
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 360
        };
        var contentPanel = new StackPanel
        {
            Spacing = 12,
            MinWidth = 320,
            MaxWidth = 380,
            Padding = new Thickness(0, 6, 0, 4)
        };
        contentPanel.Children.Add(ring);
        contentPanel.Children.Add(waitingText);
        contentPanel.Children.Add(tipText);

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.Format("Acc_OAuthDialogTitleFmt", providerName),
            Content = contentPanel,
            SecondaryButtonText = LocalizationService.GetString("Acc_OAuthReopenBrowserBtn"),
            CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        string authUrl = session.AuthorizeUrl;
        dlg.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            OpenUrlInSystemBrowser(authUrl);
        };

        StopOAuthLoginFlow();
        var cts = new CancellationTokenSource();
        _oauthCts = cts;
        var ct = cts.Token;

        AccountLoginSessionData? completedSession = null;
        string? terminalError = null;
        bool finishedByPoll = false;
        var activeSession = session;

        _ = Task.Run(async () =>
        {
            int intervalMs = Math.Clamp(activeSession.PollIntervalMs, 1000, 3000);
            int netErrs = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(intervalMs, ct);
                    if (ct.IsCancellationRequested) break;

                    var pollRes = await acc.PollOAuthStatusAsync(activeSession.PollUrl, activeSession.PollSecret);
                    if (ct.IsCancellationRequested) break;

                    if (pollRes.Code == OsmiumErrorCode.NetworkError)
                    {
                        netErrs++;
                        if (netErrs >= 10)
                        {
                            terminalError = acc.FormatFriendlyError(pollRes.Code, pollRes.HttpStatusCode);
                            finishedByPoll = true;
                            DispatcherQueue.TryEnqueue(() => dlg.Hide());
                            return;
                        }

                        continue;
                    }

                    netErrs = 0;

                    if (pollRes.Ok && pollRes.Data != null)
                    {
                        if (string.Equals(pollRes.Data.State, "completed", StringComparison.OrdinalIgnoreCase) &&
                            pollRes.Data.Session != null)
                        {
                            completedSession = pollRes.Data.Session;
                            finishedByPoll = true;
                            DispatcherQueue.TryEnqueue(() => dlg.Hide());
                            return;
                        }

                        continue;
                    }

                    terminalError = acc.FormatFriendlyError(pollRes.Code, pollRes.HttpStatusCode, pollRes.RetryAfter);
                    finishedByPoll = true;
                    DispatcherQueue.TryEnqueue(() => dlg.Hide());
                    return;
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, ct);

        try
        {
            await dlg.ShowAsync();
        }
        finally
        {
            StopOAuthLoginFlow();
        }

        if (completedSession != null)
        {
            acc.ApplyLoginSession(completedSession.Email, completedSession);
            _wizardStep = -1;
            if (!acc.IsProfileComplete)
            {
                ShowStatus(
                    InfoBarSeverity.Warning,
                    LocalizationService.GetString(acc.HasEmail ? "Acc_OAuthNeedPwdToast" : "Acc_OAuthNeedEmailPwdToast"));
            }
            else
            {
                ShowStatus(InfoBarSeverity.Success, LocalizationService.GetString("Acc_MsgLoginSuccess"));
            }

            RefreshViewState();
            _ = RefreshAllAccountDataAsync();
            _ = App.Engine.SyncSignalingStatusAsync();
        }
        else if (!string.IsNullOrEmpty(terminalError))
        {
            ShowStatus(InfoBarSeverity.Error, terminalError);
        }
        else if (!finishedByPoll)
        {
            _ = acc.CancelOAuthLoginAsync(activeSession.State, activeSession.PollSecret);
            ShowStatus(InfoBarSeverity.Informational, LocalizationService.GetString("Acc_OAuthCancelled"));
        }
    }

    private void BtnRetryOAuthBindings_Click(object sender, RoutedEventArgs e)
    {
        if (_oauthBindingsLoading || _isBusy) return;
        _ = RefreshOAuthBindingsAsync();
    }

    private async Task RefreshOAuthBindingsAsync()
    {
        var acc = App.Engine.AccountService;
        if (!acc.IsLoggedIn)
        {
            _oauthBindings = null;
            _oauthBindingsLoading = false;
            _oauthBindingsFailed = false;
            ApplyOAuthBindingsUi();
            return;
        }

        int gen = ++_oauthBindingsGen;
        _oauthBindingsLoading = true;
        _oauthBindingsFailed = false;
        ApplyOAuthBindingsUi();
        try
        {
            var res = await acc.GetOAuthBindingsAsync();
            if (gen != _oauthBindingsGen || !acc.IsLoggedIn) return;
            if (res.Ok && res.Data != null)
            {
                _oauthBindings = res.Data;
                _oauthBindingsFailed = false;
            }
            else if (_oauthBindings == null)
            {
                _oauthBindingsFailed = true;
            }
        }
        finally
        {
            if (gen == _oauthBindingsGen)
            {
                _oauthBindingsLoading = false;
            }
        }

        ApplyOAuthBindingsUi();
    }

    private void ApplyOAuthBindingsUi()
    {
        if (ThirdPartyBindingsCard == null) return;
        var acc = App.Engine.AccountService;
        if (!acc.IsLoggedIn)
        {
            ThirdPartyBindingsCard.Visibility = Visibility.Collapsed;
            return;
        }

        ThirdPartyBindingsHeaderText.Text = LocalizationService.GetString("Acc_OAuthBindingsHeader");
        BtnRetryOAuthBindings.Content = LocalizationService.GetString("Acc_RetryBtn");

        if (_oauthBindings == null)
        {
            if (!_oauthBindingsLoading && !_oauthBindingsFailed)
            {
                ThirdPartyBindingsCard.Visibility = Visibility.Collapsed;
                return;
            }

            ThirdPartyBindingsCard.Visibility = Visibility.Visible;
            RowOAuthBindingsStatus.Visibility = Visibility.Visible;
            OAuthBindingsStatusText.Text = LocalizationService.GetString(
                _oauthBindingsFailed ? "Acc_OAuthBindingsLoadFailed" : "Acc_OAuthBindingsLoading");
            BtnRetryOAuthBindings.Visibility = _oauthBindingsFailed ? Visibility.Visible : Visibility.Collapsed;
            BtnRetryOAuthBindings.IsEnabled = !_oauthBindingsLoading && !_isBusy;
            RowBindGoogle.Visibility = Visibility.Collapsed;
            DividerBindQq.Visibility = Visibility.Collapsed;
            RowBindQq.Visibility = Visibility.Collapsed;
            DividerBindWechat.Visibility = Visibility.Collapsed;
            RowBindWechat.Visibility = Visibility.Collapsed;
            return;
        }

        RowOAuthBindingsStatus.Visibility = Visibility.Collapsed;
        BtnRetryOAuthBindings.Visibility = Visibility.Collapsed;

        var avail = _oauthBindings.Available;
        var bound = _oauthBindings.Bound;
        bool showGoogle = avail.Google || bound.Google;
        bool showQq = avail.Qq || bound.Qq;
        bool showWechat = avail.Wechat || bound.Wechat;

        if (!showGoogle && !showQq && !showWechat)
        {
            ThirdPartyBindingsCard.Visibility = Visibility.Collapsed;
            return;
        }

        ThirdPartyBindingsCard.Visibility = Visibility.Visible;

        bool hostControlled = App.Engine.Coordinator.IsHostStreaming;
        bool canModify = !hostControlled && !acc.IsLocked && acc.IsProfileComplete && !_isBusy;

        string boundText = LocalizationService.GetString("Acc_OAuthBoundStatus");
        string unboundText = LocalizationService.GetString("Acc_OAuthUnboundStatus");
        string bindBtnText = LocalizationService.GetString("Acc_OAuthBindBtn");
        string unbindBtnText = LocalizationService.GetString("Acc_OAuthUnbindBtn");

        RowBindGoogle.Visibility = showGoogle ? Visibility.Visible : Visibility.Collapsed;
        BindGoogleTitleText.Text = LocalizationService.GetString("Acc_OAuthGoogleBtn");
        BindGoogleStatusText.Text = bound.Google ? boundText : unboundText;
        BtnBindGoogle.Content = bound.Google ? unbindBtnText : bindBtnText;
        BtnBindGoogle.IsEnabled = canModify && (bound.Google || avail.Google);

        RowBindQq.Visibility = showQq ? Visibility.Visible : Visibility.Collapsed;
        DividerBindQq.Visibility = (showGoogle && showQq) ? Visibility.Visible : Visibility.Collapsed;
        BindQqTitleText.Text = LocalizationService.GetString("Acc_OAuthQqBtn");
        BindQqStatusText.Text = bound.Qq ? boundText : unboundText;
        BtnBindQq.Content = bound.Qq ? unbindBtnText : bindBtnText;
        BtnBindQq.IsEnabled = canModify && (bound.Qq || avail.Qq);

        RowBindWechat.Visibility = showWechat ? Visibility.Visible : Visibility.Collapsed;
        DividerBindWechat.Visibility = ((showGoogle || showQq) && showWechat) ? Visibility.Visible : Visibility.Collapsed;
        BindWechatTitleText.Text = LocalizationService.GetString("Acc_OAuthWechatBtn");
        BindWechatStatusText.Text = bound.Wechat ? boundText : unboundText;
        BtnBindWechat.Content = bound.Wechat ? unbindBtnText : bindBtnText;
        BtnBindWechat.IsEnabled = canModify && (bound.Wechat || avail.Wechat);
    }

    private void BtnBindGoogle_Click(object sender, RoutedEventArgs e)
        => _ = HandleOAuthBindingActionAsync("google");

    private void BtnBindQq_Click(object sender, RoutedEventArgs e)
        => _ = HandleOAuthBindingActionAsync("qq");

    private void BtnBindWechat_Click(object sender, RoutedEventArgs e)
        => _ = HandleOAuthBindingActionAsync("wechat");

    private bool IsOAuthProviderBound(string provider) => _oauthBindings != null && provider switch
    {
        "google" => _oauthBindings.Bound.Google,
        "qq" => _oauthBindings.Bound.Qq,
        "wechat" => _oauthBindings.Bound.Wechat,
        _ => false
    };

    private async Task HandleOAuthBindingActionAsync(string provider)
    {
        if (_isBusy || App.Engine.Coordinator.IsHostStreaming || XamlRoot == null) return;
        var acc = App.Engine.AccountService;
        if (!acc.IsLoggedIn || acc.IsLocked || !acc.IsProfileComplete) return;

        if (IsOAuthProviderBound(provider))
        {
            await UnbindOAuthProviderFlowAsync(provider);
        }
        else
        {
            await StartOAuthBindFlowAsync(provider);
        }
    }

    private async Task UnbindOAuthProviderFlowAsync(string provider)
    {
        if (XamlRoot == null) return;
        string providerName = GetOAuthProviderDisplayName(provider);
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString("Acc_OAuthUnbindConfirmTitle"),
            Content = new TextBlock
            {
                Text = LocalizationService.Format("Acc_OAuthUnbindConfirmBodyFmt", providerName),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360
            },
            PrimaryButtonText = LocalizationService.GetString("Acc_OAuthUnbindBtn"),
            CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var acc = App.Engine.AccountService;
        SetBusy(true);
        try
        {
            var res = await acc.UnbindOAuthProviderAsync(provider);
            if (res.Ok)
            {
                ShowStatus(InfoBarSeverity.Success, LocalizationService.Format("Acc_MsgOAuthUnboundFmt", providerName));
                await RefreshOAuthBindingsAsync();
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

    private async Task StartOAuthBindFlowAsync(string provider)
    {
        if (XamlRoot == null) return;
        var acc = App.Engine.AccountService;
        SetBusy(true);
        StatusInfoBar.IsOpen = false;
        OAuthStartData? session = null;
        try
        {
            var startRes = await acc.StartOAuthBindAsync(provider);
            if (!startRes.Ok || startRes.Data == null || string.IsNullOrWhiteSpace(startRes.Data.AuthorizeUrl))
            {
                ShowStatus(InfoBarSeverity.Error,
                    acc.FormatFriendlyError(startRes.Code, startRes.HttpStatusCode, startRes.RetryAfter));
                return;
            }

            session = startRes.Data;
        }
        finally
        {
            SetBusy(false);
        }

        OpenUrlInSystemBrowser(session.AuthorizeUrl);

        string providerName = GetOAuthProviderDisplayName(provider);
        var ring = new ProgressRing
        {
            Width = 28,
            Height = 28,
            IsActive = true,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var waitingText = new TextBlock
        {
            Text = LocalizationService.Format("Acc_OAuthBindWaitingDescFmt", providerName),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 360
        };
        var tipText = new TextBlock
        {
            Text = LocalizationService.GetString("Acc_OAuthBindBrowserTip"),
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 360
        };
        var contentPanel = new StackPanel
        {
            Spacing = 12,
            MinWidth = 320,
            MaxWidth = 380,
            Padding = new Thickness(0, 6, 0, 4)
        };
        contentPanel.Children.Add(ring);
        contentPanel.Children.Add(waitingText);
        contentPanel.Children.Add(tipText);

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.Format("Acc_OAuthBindDialogTitleFmt", providerName),
            Content = contentPanel,
            SecondaryButtonText = LocalizationService.GetString("Acc_OAuthReopenBrowserBtn"),
            CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        string authUrl = session.AuthorizeUrl;
        dlg.SecondaryButtonClick += (dialog, args) =>
        {
            args.Cancel = true;
            OpenUrlInSystemBrowser(authUrl);
        };

        StopOAuthLoginFlow();
        var cts = new CancellationTokenSource();
        _oauthCts = cts;
        var ct = cts.Token;

        bool bindSucceeded = false;
        string? terminalError = null;
        bool finishedByPoll = false;
        var activeSession = session;

        _ = Task.Run(async () =>
        {
            int intervalMs = Math.Clamp(activeSession.PollIntervalMs, 1000, 3000);
            int netErrs = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(intervalMs, ct);
                    if (ct.IsCancellationRequested) break;

                    var pollRes = await acc.PollOAuthStatusAsync(activeSession.PollUrl, activeSession.PollSecret);
                    if (ct.IsCancellationRequested) break;

                    if (pollRes.Code == OsmiumErrorCode.NetworkError)
                    {
                        netErrs++;
                        if (netErrs >= 10)
                        {
                            terminalError = acc.FormatFriendlyError(pollRes.Code, pollRes.HttpStatusCode);
                            finishedByPoll = true;
                            DispatcherQueue.TryEnqueue(() => dlg.Hide());
                            return;
                        }

                        continue;
                    }

                    netErrs = 0;

                    if (pollRes.Ok && pollRes.Data != null)
                    {
                        if (string.Equals(pollRes.Data.State, "completed", StringComparison.OrdinalIgnoreCase) &&
                            pollRes.Data.Bound)
                        {
                            bindSucceeded = true;
                            finishedByPoll = true;
                            DispatcherQueue.TryEnqueue(() => dlg.Hide());
                            return;
                        }

                        continue;
                    }

                    terminalError = acc.FormatFriendlyError(pollRes.Code, pollRes.HttpStatusCode, pollRes.RetryAfter);
                    finishedByPoll = true;
                    DispatcherQueue.TryEnqueue(() => dlg.Hide());
                    return;
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, ct);

        try
        {
            await dlg.ShowAsync();
        }
        finally
        {
            StopOAuthLoginFlow();
        }

        if (bindSucceeded)
        {
            ShowStatus(InfoBarSeverity.Success, LocalizationService.Format("Acc_MsgOAuthBoundFmt", providerName));
            await RefreshOAuthBindingsAsync();
        }
        else if (!string.IsNullOrEmpty(terminalError))
        {
            ShowStatus(InfoBarSeverity.Error, terminalError);
            await RefreshOAuthBindingsAsync();
        }
        else if (!finishedByPoll)
        {
            _ = acc.CancelOAuthLoginAsync(activeSession.State, activeSession.PollSecret);
            ShowStatus(InfoBarSeverity.Informational, LocalizationService.GetString("Acc_OAuthBindCancelled"));
        }
    }
}
