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
    private string _targetDeviceId = string.Empty;
    private bool _isConnecting;
    private int _wallpaperVersion;
    private bool? _appliedWallpaperDark;
    private bool? _appliedWallpaperOffline;
    private bool? _appliedWallpaperBlurred;
    private bool _isControllingOverlayVisible;
    private Storyboard? _wallpaperFadeStoryboard;
    private Storyboard? _overlayFadeStoryboard;
    private readonly DispatcherTimer _durationTimer = new();
    private readonly DispatcherTimer _cooldownTimer = new();
    private Action<string>? _activeStageSink;
    private Action? _activeStreamStartedSink;

    public PeerDevicePage()
    {
        InitializeComponent();
        _durationTimer.Interval = TimeSpan.FromSeconds(1);
        _durationTimer.Tick += (_, _) => UpdateControllingDuration();

        _cooldownTimer.Interval = TimeSpan.FromMilliseconds(200);
        _cooldownTimer.Tick += (_, _) => UpdateDockCooldownState();

        Loaded += PeerDevicePage_Loaded;
        Unloaded += PeerDevicePage_Unloaded;
        ActualThemeChanged += (_, _) => _ = UpdateWallpaperAsync();
        LocalizationService.LanguageChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyLocalization();
            RefreshPeerDeviceState();
        });

        PopupCornerHelper.AttachMenuFlyout(PeerHeaderFlyout, PeerStatePill);
        PopupCornerHelper.AttachMenuFlyout(DockMoreFlyout, HeroCardClipGrid);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string devId && !string.IsNullOrWhiteSpace(devId))
        {
            _targetDeviceId = DeviceIdentity.NormalizeDeviceId(devId);
        }
    }

    private void PeerDevicePage_Loaded(object sender, RoutedEventArgs e)
    {
        App.Engine.AccountService.SameAccountDevicesChanged -= OnDevicesChanged;
        App.Engine.AccountService.SameAccountDevicesChanged += OnDevicesChanged;
        App.Engine.Coordinator.ConnectCooldownChanged -= OnCooldownChanged;
        App.Engine.Coordinator.ConnectCooldownChanged += OnCooldownChanged;
        App.Engine.Coordinator.ClientStageUpdated += OnClientStageUpdated;
        App.Engine.Coordinator.ClientStreamStarted += OnClientStreamStarted;
        App.Engine.Coordinator.HostSessionStarted -= OnHostSessionStarted;
        App.Engine.Coordinator.HostSessionStarted += OnHostSessionStarted;
        App.Engine.Coordinator.HostSessionEnded -= OnHostSessionEnded;
        App.Engine.Coordinator.HostSessionEnded += OnHostSessionEnded;

        UpdateHeroRoundedClip();
        ApplyLocalization();
        RefreshPeerDeviceState();
    }

    private void PeerDevicePage_Unloaded(object sender, RoutedEventArgs e)
    {
        App.Engine.AccountService.SameAccountDevicesChanged -= OnDevicesChanged;
        App.Engine.Coordinator.ConnectCooldownChanged -= OnCooldownChanged;
        App.Engine.Coordinator.ClientStageUpdated -= OnClientStageUpdated;
        App.Engine.Coordinator.ClientStreamStarted -= OnClientStreamStarted;
        App.Engine.Coordinator.HostSessionStarted -= OnHostSessionStarted;
        App.Engine.Coordinator.HostSessionEnded -= OnHostSessionEnded;
        _durationTimer.Stop();
        _cooldownTimer.Stop();
    }

    private void OnDevicesChanged(IReadOnlyList<AccountDeviceDto> _)
        => DispatcherQueue.TryEnqueue(RefreshPeerDeviceState);

    private void OnHostSessionStarted(HostSessionActiveInfo _)
        => DispatcherQueue.TryEnqueue(RefreshPeerDeviceState);

    private void OnHostSessionEnded(string _)
        => DispatcherQueue.TryEnqueue(RefreshPeerDeviceState);

    private void OnCooldownChanged(int _)
        => DispatcherQueue.TryEnqueue(UpdateDockCooldownState);

    private void OnClientStageUpdated(string stage)
        => DispatcherQueue.TryEnqueue(() => _activeStageSink?.Invoke(stage));

    private void OnClientStreamStarted()
        => DispatcherQueue.TryEnqueue(() => _activeStreamStartedSink?.Invoke());

    private void HeroCardClipGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateHeroRoundedClip();

    private void UpdateHeroRoundedClip()
    {
        try
        {
            if (HeroCardClipGrid == null || HeroCardClipGrid.ActualWidth <= 0 || HeroCardClipGrid.ActualHeight <= 0)
                return;

            Visual visual = ElementCompositionPreview.GetElementVisual(HeroCardClipGrid);
            Compositor compositor = visual.Compositor;

            CompositionRoundedRectangleGeometry geometry = compositor.CreateRoundedRectangleGeometry();
            geometry.CornerRadius = new Vector2(12f, 12f);
            geometry.Size = new Vector2((float)HeroCardClipGrid.ActualWidth, (float)HeroCardClipGrid.ActualHeight);
            visual.Clip = compositor.CreateGeometricClip(geometry);
        }
        catch
        {
        }
    }

    private void ApplyLocalization()
    {
        MenuConnectItem.Text = LocalizationService.GetString("Peer_ConnectAction");
        MenuViewOnlyItem.Text = LocalizationService.GetString("Peer_ViewOnlyAction");
        MenuRenameItem.Text = LocalizationService.GetString("Set_DeviceRenameTitle");
        MenuCopyIdItem.Text = LocalizationService.GetString("Peer_CopyIdAction");
        MenuRefreshItem.Text = LocalizationService.GetString("Gamepad_RefreshBtn");

        BannerDisconnectBtn.Content = LocalizationService.GetString("Home_DisconnectBtn");
        BannerBlacklistBtn.Content = LocalizationService.GetString("Peer_BlacklistControllerBtn");

        DockViewOnlyLabel.Text = LocalizationService.GetString("Peer_ViewOnlyAction");
        DockDisconnectLabel.Text = LocalizationService.GetString("Home_DisconnectBtn");
        DockBlacklistLabel.Text = LocalizationService.GetString("Peer_BlacklistControllerBtn");

        FlyoutRenameDevice.Text = LocalizationService.GetString("Set_DeviceRenameTitle");
        FlyoutCopyId.Text = LocalizationService.GetString("Peer_CopyIdAction");
        FlyoutRefresh.Text = LocalizationService.GetString("Gamepad_RefreshBtn");
        FlyoutManageDevices.Text = LocalizationService.GetString("Acc_TabDevices");

        MetaOsLabel.Text = LocalizationService.GetString("Peer_MetaOs");
        MetaIpLabel.Text = LocalizationService.GetString("Peer_MetaIp");
        MetaActiveLabel.Text = LocalizationService.GetString("Peer_MetaLastActive");

        UpdateDockCooldownState();
    }

    private void UpdateDockCooldownState()
    {
        var dev = App.Engine.AccountService.FindPeerDevice(_targetDeviceId);
        bool canConnect = dev is { IsOnline: true, AllowRemote: true } && !_isConnecting;
        int remMs = App.Engine.Coordinator.RemainingConnectCooldownMs;
        if (remMs > 0)
        {
            int secs = Math.Max(1, (int)Math.Ceiling(remMs / 1000.0));
            DockConnectLabel.Text = LocalizationService.Format("Assist_ConnectCooldownFmt", secs);
            DockConnectBtn.IsEnabled = false;
            DockViewOnlyBtn.IsEnabled = false;
            MenuConnectItem.IsEnabled = false;
            MenuViewOnlyItem.IsEnabled = false;
            if (!_cooldownTimer.IsEnabled) _cooldownTimer.Start();
            return;
        }

        if (_cooldownTimer.IsEnabled) _cooldownTimer.Stop();
        DockConnectLabel.Text = LocalizationService.GetString("Peer_ConnectAction");
        DockConnectBtn.IsEnabled = canConnect;
        DockViewOnlyBtn.IsEnabled = canConnect;
        MenuConnectItem.IsEnabled = canConnect;
        MenuViewOnlyItem.IsEnabled = canConnect;
    }

    private static bool IsSelfAccountController(AccountDeviceDto dev)
    {
        string currentUid = App.Engine.AccountService.CurrentUserId;
        return !string.IsNullOrWhiteSpace(dev.ControllerUserId) &&
               !string.IsNullOrWhiteSpace(currentUid) &&
               string.Equals(dev.ControllerUserId, currentUid, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshPeerDeviceState()
    {
        var dev = App.Engine.AccountService.FindPeerDevice(_targetDeviceId);
        bool hostStreaming = App.Engine.Coordinator.IsHostStreaming;
        if (dev == null)
        {
            PeerDeviceNameText.Text = DeviceIdentity.FormatDeviceId(_targetDeviceId);
            PeerDeviceIdText.Text = string.Empty;
            SetPillState(online: false, streaming: false);
            ActivePeerBanner.Visibility = Visibility.Collapsed;
            ActivePeerBanner.IsExpanded = false;
            BannerBlacklistBtn.Visibility = Visibility.Collapsed;
            DockBlacklistBtn.Visibility = Visibility.Visible;
            DockBlacklistDivider.Visibility = Visibility.Visible;
            ColDockBlacklist.Width = new GridLength(1, GridUnitType.Star);
            DockDisconnectBtn.IsEnabled = false;
            DockBlacklistBtn.IsEnabled = false;
            MenuRenameItem.IsEnabled = false;
            FlyoutRenameDevice.IsEnabled = false;
            UpdatePeerControllingOverlay(false);
            UpdateDockCooldownState();
            _ = UpdateWallpaperAsync();
            return;
        }

        PeerDeviceNameText.Text = dev.DeviceName;
        PeerDeviceIdText.Text = dev.FormattedDeviceId;
        MetaOsValue.Text = dev.OsVersion;
        MetaIpValue.Text = string.IsNullOrWhiteSpace(dev.LastIp) ? "-" : dev.LastIp;
        MetaActiveValue.Text = dev.LastActiveText;

        MenuRenameItem.IsEnabled = !hostStreaming;
        FlyoutRenameDevice.IsEnabled = !hostStreaming;

        SetPillState(dev.IsOnline, dev.IsBeingControlled);

        bool isSelfAccount = IsSelfAccountController(dev);
        if (dev.IsBeingControlled)
        {
            string who;
            string whom;
            if (isSelfAccount)
            {
                who = App.Engine.AccountService.ResolveAccountDeviceName(
                    dev.ControllerDeviceId,
                    !string.IsNullOrWhiteSpace(dev.ControllerDeviceName)
                        ? dev.ControllerDeviceName
                        : DeviceIdentity.FormatDeviceId(dev.ControllerDeviceId ?? string.Empty));
                whom = dev.DeviceName;
            }
            else
            {
                who = !string.IsNullOrWhiteSpace(dev.ControllerUserName)
                    ? dev.ControllerUserName
                    : (!string.IsNullOrWhiteSpace(dev.ControllerDeviceName)
                        ? dev.ControllerDeviceName
                        : LocalizationService.GetString("Acc_DefaultUser"));
                whom = !string.IsNullOrWhiteSpace(dev.HostDisplayName)
                    ? dev.HostDisplayName
                    : dev.DeviceName;
            }

            string whoWhomText = LocalizationService.Format("Session_WhoControllingWhomFmt", who, whom);

            ActivePeerBannerTitle.Text = whoWhomText;
            ActivePeerBannerMessage.Text = !string.IsNullOrWhiteSpace(dev.ControllerDeviceId)
                ? DeviceIdentity.FormatDeviceId(dev.ControllerDeviceId)
                : dev.DeviceName;
            if (ActivePeerBanner.Visibility != Visibility.Visible)
            {
                ActivePeerBanner.IsExpanded = false;
                ActivePeerBanner.Visibility = Visibility.Visible;
            }

            PeerControllingTitleText.Text = whoWhomText;
        }
        else
        {
            ActivePeerBanner.Visibility = Visibility.Collapsed;
            ActivePeerBanner.IsExpanded = false;
        }

        bool hideBlacklist = dev.IsBeingControlled && isSelfAccount;
        BannerBlacklistBtn.Visibility =
            (dev.IsBeingControlled && !isSelfAccount) ? Visibility.Visible : Visibility.Collapsed;
        DockBlacklistBtn.Visibility = hideBlacklist ? Visibility.Collapsed : Visibility.Visible;
        DockBlacklistDivider.Visibility = hideBlacklist ? Visibility.Collapsed : Visibility.Visible;
        ColDockBlacklist.Width = hideBlacklist ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        DockDisconnectBtn.IsEnabled = dev.IsBeingControlled;
        DockBlacklistBtn.IsEnabled = dev.IsBeingControlled && !isSelfAccount;
        UpdatePeerControllingOverlay(dev.IsBeingControlled);
        UpdateDockCooldownState();
        _ = UpdateWallpaperAsync();
    }

    private void UpdatePeerControllingOverlay(bool streaming)
    {
        if (PeerControllingOverlay == null) return;

        if (streaming)
        {
            UpdateControllingDuration();
            if (!_durationTimer.IsEnabled)
            {
                _durationTimer.Start();
            }

            if (_isControllingOverlayVisible && PeerControllingOverlay.Visibility == Visibility.Visible)
            {
                return;
            }
        }
        else
        {
            _durationTimer.Stop();
            if (!_isControllingOverlayVisible && PeerControllingOverlay.Visibility == Visibility.Collapsed)
            {
                return;
            }
        }

        _isControllingOverlayVisible = streaming;
        _overlayFadeStoryboard?.Stop();
        if (streaming)
        {
            PeerControllingOverlay.Visibility = Visibility.Visible;
        }

        double targetOpacity = streaming ? 1.0 : 0.0;
        var sb = new Storyboard();
        var fadeAnim = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(streaming ? 280 : 220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fadeAnim, PeerControllingOverlay);
        Storyboard.SetTargetProperty(fadeAnim, "Opacity");
        sb.Children.Add(fadeAnim);

        _overlayFadeStoryboard = sb;
        sb.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_overlayFadeStoryboard, sb)) return;
            PeerControllingOverlay.Opacity = targetOpacity;
            if (!streaming)
            {
                PeerControllingOverlay.Visibility = Visibility.Collapsed;
            }

            _overlayFadeStoryboard = null;
        };
        sb.Begin();
    }

    private void SetPillState(bool online, bool streaming)
    {
        string bgKey, fgKey, textKey;
        if (streaming)
        {
            bgKey = "OsmiumBadgeDangerBg";
            fgKey = "OsmiumBadgeDangerFg";
            textKey = "Status_Streaming";
        }
        else if (online)
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

        if (Application.Current.Resources[bgKey] is Brush bg) PeerStatePill.Background = bg;
        if (Application.Current.Resources[fgKey] is Brush fg) PeerStatePillText.Foreground = fg;
        PeerStatePillText.Text = LocalizationService.GetString(textKey);
    }

    private void UpdateControllingDuration()
    {
        var dev = App.Engine.AccountService.FindPeerDevice(_targetDeviceId);
        if (dev == null || !dev.IsBeingControlled) return;

        DateTimeOffset startUtc = dev.SessionStartedAt > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(dev.SessionStartedAt)
            : DateTimeOffset.UtcNow;
        TimeSpan elapsed = DateTimeOffset.UtcNow - startUtc;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        PeerControllingDurationText.Text = elapsed.TotalHours >= 1.0
            ? $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
    }
}