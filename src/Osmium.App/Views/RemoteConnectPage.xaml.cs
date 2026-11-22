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
    private bool _isInitializing = true;
    private bool _isConnecting = false;
    private bool _hideAccessCode = true;
    private int _copyFeedbackVersion = 0;
    private Action<string>? _activeStageSink;
    private Action? _activeStreamStartedSink;
    private readonly DispatcherTimer _cooldownTimer;

    public RemoteConnectPage()
    {
        InitializeComponent();
        _cooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _cooldownTimer.Tick += (_, _) => UpdateConnectButtonState();

        Loaded += RemoteConnectPage_Loaded;
        Unloaded += RemoteConnectPage_Unloaded;
        LocalizationService.LanguageChanged += () => DispatcherQueue.TryEnqueue(ApplyLocalization);

        var coord = App.Engine.Coordinator;
        coord.ClientStageUpdated += stage =>
            DispatcherQueue.TryEnqueue(() => _activeStageSink?.Invoke(stage));

        coord.ClientStreamStarted += () =>
            DispatcherQueue.TryEnqueue(() => _activeStreamStartedSink?.Invoke());

        coord.ClientStatsUpdated += stats =>
            DispatcherQueue.TryEnqueue(() =>
            {
                _activeStreamStartedSink?.Invoke();
                ClientLiveStatsCard.Visibility = Visibility.Visible;
                ClientLiveStatsText.Text =
                    $"{stats.VideoWidth}×{stats.VideoHeight} @ {stats.Fps:F1} FPS - {stats.BitrateMbps:F2} Mbps - {stats.DecoderName} - {stats.DecodeLatencyMs:F2} ms - RTT {stats.RttMs} ms";
            });

        coord.ClientSessionEnded += _ =>
            DispatcherQueue.TryEnqueue(() =>
            {
                ClientLiveStatsCard.Visibility = Visibility.Collapsed;
                UpdateConnectButtonState();
            });

        coord.HostSessionStarted += _ =>
            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshLocalDeviceCredentials();
                UpdateConnectButtonState();
            });

        coord.HostSessionEnded += _ =>
            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshLocalDeviceCredentials();
                UpdateConnectButtonState();
            });

        coord.RemoteSessionStateChanged += () =>
            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshLocalDeviceCredentials();
                UpdateConnectButtonState();
            });
    }

    private void OnConnectCooldownChanged(int _)
    {
        DispatcherQueue.TryEnqueue(UpdateConnectButtonState);
    }

    private void OnAccountSessionChanged()
    {
        DispatcherQueue.TryEnqueue(UpdateConnectButtonState);
    }

    private void UpdateConnectButtonState()
    {
        bool isLoggedIn = App.Engine.AccountService.IsLoggedIn;
        bool isHostControlled = App.Engine.Coordinator.IsHostStreaming;
        bool canInteractOutgoing = isLoggedIn && !isHostControlled;

        TargetDeviceIdBox.IsEnabled = canInteractOutgoing;
        TargetAccessCodeBox.IsEnabled = canInteractOutgoing;
        RecentSessionsGrid.IsEnabled = canInteractOutgoing;
        ClearHistoryBtn.IsEnabled = !isHostControlled;
        PartnerLoginHintPanel.Visibility = isLoggedIn ? Visibility.Collapsed : Visibility.Visible;

        if (!isLoggedIn || isHostControlled)
        {
            if (_cooldownTimer.IsEnabled)
            {
                _cooldownTimer.Stop();
            }

            ConnectButton.Content = LocalizationService.GetString("Assist_ConnectBtn");
            ConnectButton.IsEnabled = false;
            return;
        }

        int remainingMs = App.Engine.Coordinator.RemainingConnectCooldownMs;
        if (remainingMs > 0)
        {
            int waitSeconds = Math.Max(1, (int)Math.Ceiling(remainingMs / 1000.0));
            ConnectButton.IsEnabled = false;
            ConnectButton.Content = LocalizationService.Format("Assist_ConnectCooldownFmt", waitSeconds);
            if (!_cooldownTimer.IsEnabled)
            {
                _cooldownTimer.Start();
            }

            return;
        }

        if (_cooldownTimer.IsEnabled)
        {
            _cooldownTimer.Stop();
        }

        ConnectButton.Content = LocalizationService.GetString("Assist_ConnectBtn");
        ConnectButton.IsEnabled = !_isConnecting;
    }

    private void OnTemporaryAccessCodeChanged(string _)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isInitializing)
            {
                RefreshLocalDeviceCredentials();
            }
        });
    }

    private void RemoteConnectPage_Loaded(object sender, RoutedEventArgs e)
    {
        App.Engine.Coordinator.ConnectCooldownChanged -= OnConnectCooldownChanged;
        App.Engine.Coordinator.ConnectCooldownChanged += OnConnectCooldownChanged;
        App.Engine.SettingsService.TemporaryAccessCodeChanged -= OnTemporaryAccessCodeChanged;
        App.Engine.SettingsService.TemporaryAccessCodeChanged += OnTemporaryAccessCodeChanged;
        App.Engine.AccountService.SessionChanged -= OnAccountSessionChanged;
        App.Engine.AccountService.SessionChanged += OnAccountSessionChanged;

        _isInitializing = true;
        ApplyLocalization();
        RefreshLocalDeviceCredentials();
        RefreshRecentSessions();
        _isInitializing = false;
        UpdateConnectButtonState();
    }

    private void RemoteConnectPage_Unloaded(object sender, RoutedEventArgs e)
    {
        App.Engine.Coordinator.ConnectCooldownChanged -= OnConnectCooldownChanged;
        App.Engine.SettingsService.TemporaryAccessCodeChanged -= OnTemporaryAccessCodeChanged;
        App.Engine.AccountService.SessionChanged -= OnAccountSessionChanged;
        _cooldownTimer.Stop();
    }

    private void ApplyLocalization()
    {
        bool prev = _isInitializing;
        _isInitializing = true;

        AssistPageTitle.Text = LocalizationService.GetString("Assist_PageTitle");
        ThisDeviceHeader.Text = LocalizationService.GetString("Assist_ThisDevice");
        AllowAssistLabel.Text = LocalizationService.GetString("Assist_AllowRemote");
        MyDeviceIdLabel.Text = LocalizationService.GetString("Assist_MyDeviceId");
        VerifyMethodLabel.Text = LocalizationService.GetString("Assist_VerifyMethod");
        AccessCodePrefixText.Text = LocalizationService.GetString("Assist_CodePrefix");
        CopyShareButton.Content = LocalizationService.GetString("Assist_CopyShare");

        LocalizationService.SetComboItems(
            VerifyMethodCombo,
            ("Assist_OptTemp", "0"),
            ("Assist_OptTempConfirm", "1"),
            ("Assist_OptFixed", "2"));

        PartnerHeader.Text = LocalizationService.GetString("Assist_PartnerHeader");
        PartnerLoginHintText.Text = LocalizationService.GetString("Assist_LoginRequiredHint");
        PartnerGoLoginBtn.Content = LocalizationService.GetString("Assist_GoLoginBtn");
        PartnerIdLabel.Text = LocalizationService.GetString("Assist_PartnerId");
        PartnerCodeLabel.Text = LocalizationService.GetString("Assist_PartnerCode");
        TargetDeviceIdBox.PlaceholderText = LocalizationService.GetString("Assist_PartnerIdPlaceholder");
        TargetAccessCodeBox.PlaceholderText = LocalizationService.GetString("Assist_PartnerCodePlaceholder");
        UpdateConnectButtonState();

        RecentTitleText.Text = LocalizationService.GetString("Assist_RecentTitle");
        ClearHistoryText.Text = LocalizationService.GetString("Assist_ClearHistory");
        NoRecentText.Text = LocalizationService.GetString("Assist_NoRecent");
        StopSessionBtn.Content = LocalizationService.GetString("Assist_EndSession");

        RefreshLocalDeviceCredentials();
        _isInitializing = prev;
    }

    private void RefreshLocalDeviceCredentials()
    {
        bool prevInit = _isInitializing;
        _isInitializing = true;
        try
        {
            var s = App.Engine.SettingsService.Settings;
            var id = App.Engine.SettingsService.Identity;
            bool isHostControlled = App.Engine.Coordinator.IsHostStreaming;
            bool isClientControlling = App.Engine.Coordinator.IsClientSessionActive;

            AllowRemoteSwitch.IsOn = App.Engine.Coordinator.EffectiveAllowRemoteControl;
            AllowRemoteSwitch.IsEnabled = !isHostControlled && !isClientControlling;
            MyDeviceIdText.Text = id.FormattedDeviceId;

            int methodIdx = (id.UseFixedAccessCode && !string.IsNullOrWhiteSpace(id.FixedAccessCode))
                ? 2
                : (s.RequireApprovalDialog ? 1 : 0);
            if (VerifyMethodCombo.Items.Count > methodIdx)
            {
                VerifyMethodCombo.SelectedIndex = methodIdx;
            }

            VerifyMethodCombo.IsEnabled = !isHostControlled;

            string activeCode = (methodIdx == 2 && !string.IsNullOrWhiteSpace(id.FixedAccessCode))
                ? id.FixedAccessCode
                : id.TemporaryAccessCode;

            bool effectiveHide = isHostControlled || _hideAccessCode;
            MyAccessCodeText.Text = effectiveHide ? "••••••" : activeCode;
            MyAccessCodeText.IsTextSelectionEnabled = !effectiveHide;
            CodeVisibilityIcon.Glyph = effectiveHide ? "\uE5F6" : "\uE5F3";

            ToggleCodeVisibilityBtn.IsEnabled = !isHostControlled;
            RefreshAccessCodeBtn.IsEnabled = !isHostControlled;
            CopyShareButton.IsEnabled = !isHostControlled;
        }
        finally
        {
            _isInitializing = prevInit;
        }
    }

    private void RefreshRecentSessions()
    {
        var list = App.Engine.SettingsService.Settings.RecentSessions;
        RecentSessionsGrid.ItemsSource = list.ToList();
        bool hasAny = list.Count > 0;
        RecentSessionsGrid.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryBtn.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
        NoRecentText.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void AllowRemoteSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || App.Engine.Coordinator.IsHostStreaming || App.Engine.Coordinator.IsClientSessionActive)
            return;
        App.Engine.SettingsService.Settings.AllowRemoteControl = AllowRemoteSwitch.IsOn;
        App.Engine.SettingsService.SaveSettings();
        await App.Engine.SyncSignalingStatusAsync();
    }

    private void VerifyMethodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || App.Engine.Coordinator.IsHostStreaming) return;
        var s = App.Engine.SettingsService.Settings;
        var id = App.Engine.SettingsService.Identity;

        switch (VerifyMethodCombo.SelectedIndex)
        {
            case 0:
                s.RequireApprovalDialog = false;
                id.UseFixedAccessCode = false;
                break;
            case 1:
                s.RequireApprovalDialog = true;
                id.UseFixedAccessCode = false;
                break;
            case 2:
                s.RequireApprovalDialog = false;
                if (string.IsNullOrWhiteSpace(id.FixedAccessCode))
                {
                    App.MainWindowInstance?.NavigateToHostControl();
                    return;
                }

                id.UseFixedAccessCode = true;
                break;
        }

        App.Engine.SettingsService.SaveSettings();
        App.Engine.SettingsService.SaveIdentity();
        RefreshLocalDeviceCredentials();
    }

    private void ToggleCodeVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Coordinator.IsHostStreaming) return;
        _hideAccessCode = !_hideAccessCode;
        CodeVisibilityIcon.Glyph = _hideAccessCode ? "\uE5F6" : "\uE5F3";
        RefreshLocalDeviceCredentials();
    }

    private void RefreshAccessCode_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Coordinator.IsHostStreaming) return;
        App.Engine.SettingsService.RefreshTemporaryAccessCode();
        RefreshLocalDeviceCredentials();
    }

    private async void CopyInviteBundle_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Coordinator.IsHostStreaming) return;
        var id = App.Engine.SettingsService.Identity;
        string code = (id.UseFixedAccessCode && !string.IsNullOrWhiteSpace(id.FixedAccessCode))
            ? id.FixedAccessCode
            : id.TemporaryAccessCode;

        var dp = new DataPackage();
        dp.SetText(LocalizationService.Format("Assist_ShareClipboardFmt", id.FormattedDeviceId, code));
        Clipboard.SetContent(dp);

        int ver = ++_copyFeedbackVersion;
        CopyShareButton.Content = LocalizationService.GetString("Assist_CopiedBtn");
        await Task.Delay(1800);
        if (ver == _copyFeedbackVersion)
        {
            CopyShareButton.Content = LocalizationService.GetString("Assist_CopyShare");
        }
    }

    private void StopClientSession_Click(object sender, RoutedEventArgs e)
        => App.Engine.Coordinator.StopClientSession();

    private void PartnerGoLoginBtn_Click(object sender, RoutedEventArgs e)
        => App.MainWindowInstance?.NavigateToAccount();
}