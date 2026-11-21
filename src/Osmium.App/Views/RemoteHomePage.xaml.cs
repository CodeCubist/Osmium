using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Osmium.App.Helpers;
using Osmium.App.Services;
using Osmium.Core.Models;
using Osmium.Core.Network;

namespace Osmium.App.Views;

public sealed partial class RemoteHomePage : Page
{
    private int _wallpaperVersion;
    private bool? _appliedWallpaperDark;
    private bool? _appliedWallpaperOffline;
    private bool? _appliedWallpaperBlurred;
    private bool _isControllingOverlayVisible;
    private Storyboard? _wallpaperFadeStoryboard;
    private Storyboard? _overlayFadeStoryboard;
    private readonly DispatcherTimer _hostDurationTimer = new();
    private DateTimeOffset? _fallbackHostStartUtc;

    public RemoteHomePage()
    {
        InitializeComponent();
        _hostDurationTimer.Interval = TimeSpan.FromSeconds(1);
        _hostDurationTimer.Tick += (_, _) => UpdateHostDurationDisplay();

        Loaded += RemoteHomePage_Loaded;
        Unloaded += RemoteHomePage_Unloaded;
        ActualThemeChanged += (_, _) => UpdateHeroWallpaper();
        LocalizationService.LanguageChanged += () => DispatcherQueue.TryEnqueue(ApplyLocalization);

        PopupCornerHelper.AttachMenuFlyout(DeviceHeaderFlyout, HostStatePill);
        PopupCornerHelper.AttachMenuFlyout(DockMoreFlyout, HeroCardClipGrid);

        var engine = App.Engine;

        engine.Signaling.StateChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(UpdateHostStatusPill);

        engine.Coordinator.HostSessionStarted += info =>
            DispatcherQueue.TryEnqueue(() =>
            {
                _fallbackHostStartUtc = DateTimeOffset.UtcNow;
                bool isSameAccount = IsSameAccountController(info);
                string peerLabel = isSameAccount
                    ? App.Engine.AccountService.ResolveAccountDeviceName(
                        info.PeerDeviceId,
                        !string.IsNullOrWhiteSpace(info.ControllerDeviceName)
                            ? info.ControllerDeviceName
                            : info.PeerName)
                    : info.PeerName;
                ActiveHostBanner.IsOpen = true;
                ActiveHostBanner.Message = $"{peerLabel} · {DeviceIdentity.FormatDeviceId(info.PeerDeviceId)}";
                UpdateHostControllingHeadline();
                UpdateHostStatusPill();
            });

        engine.Coordinator.HostStatsUpdated += stats =>
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ActiveHostBanner.IsOpen)
                {
                    ActiveHostBanner.Message =
                        $"{stats.CaptureWidth}×{stats.CaptureHeight} @ {stats.Fps:F1} FPS · {stats.BitrateMbps:F2} Mbps · RTT {stats.RttMs} ms";
                }
            });

        engine.Coordinator.HostSessionEnded += _ =>
            DispatcherQueue.TryEnqueue(() =>
            {
                _fallbackHostStartUtc = null;
                ActiveHostBanner.IsOpen = false;
                UpdateHostControllingHeadline();
                UpdateHostStatusPill();
            });
    }

    private void OnAccountStateChanged()
        => DispatcherQueue.TryEnqueue(() =>
        {
            UpdateDeviceHeader();
            UpdateHostControllingHeadline();
        });

    private void OnAccountDevicesChanged(IReadOnlyList<AccountDeviceDto> _)
        => OnAccountStateChanged();

    private void RemoteHomePage_Loaded(object sender, RoutedEventArgs e)
    {
        App.Engine.AccountService.SessionChanged -= OnAccountStateChanged;
        App.Engine.AccountService.SessionChanged += OnAccountStateChanged;
        App.Engine.AccountService.SameAccountDevicesChanged -= OnAccountDevicesChanged;
        App.Engine.AccountService.SameAccountDevicesChanged += OnAccountDevicesChanged;

        UpdateHeroRoundedClip();
        ApplyLocalization();
        UpdateDeviceHeader();
        UpdateHostStatusPill();
        UpdateDockActionLabels();
    }

    private void RemoteHomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        App.Engine.AccountService.SessionChanged -= OnAccountStateChanged;
        App.Engine.AccountService.SameAccountDevicesChanged -= OnAccountDevicesChanged;
        _hostDurationTimer.Stop();
    }

    private void HeroCardClipGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateHeroRoundedClip();
    }

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

    private async void UpdateHeroWallpaper()
    {
        if (HeroWallpaperImage == null || HeroWallpaperBackImage == null) return;

        bool isDark = ActualTheme == ElementTheme.Dark;
        bool isOffline = App.Engine.Signaling.State != SignalingConnectionState.Connected;
        bool isBlurred = App.Engine.Coordinator.IsHostStreaming;

        if (_appliedWallpaperDark == isDark &&
            _appliedWallpaperOffline == isOffline &&
            _appliedWallpaperBlurred == isBlurred &&
            HeroWallpaperImage.Source != null)
        {
            return;
        }

        _appliedWallpaperDark = isDark;
        _appliedWallpaperOffline = isOffline;
        _appliedWallpaperBlurred = isBlurred;
        int version = ++_wallpaperVersion;

        var targetSource = await WallpaperHelper.GetHeroWallpaperVariantAsync(
            isDark, grayscale: isOffline, blur: isBlurred, preferSecondary: false, decodeWidth: 1280);
        if (version != _wallpaperVersion || targetSource == null) return;

        ApplyHeroWallpaperWithTransition(targetSource);
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

    private static bool IsSameAccountController(HostSessionActiveInfo info)
    {
        if (info.IsSameAccount) return true;
        var acc = App.Engine.AccountService;
        return acc.IsLoggedIn &&
               !string.IsNullOrWhiteSpace(info.ControllerUserId) &&
               string.Equals(info.ControllerUserId, acc.CurrentUserId, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateDeviceHeader()
    {
        DeviceNameText.Text = App.Engine.AccountService.GetEffectiveLocalDeviceName();
        bool loggedIn = App.Engine.AccountService.IsLoggedIn;
        MenuRenameLocalDeviceItem.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
        MenuRenameLocalDeviceItem.IsEnabled = loggedIn && !App.Engine.Coordinator.IsHostStreaming;
    }

    private void UpdateHostControllingHeadline()
    {
        var info = App.Engine.Coordinator.ActiveHostSessionInfo;
        if (App.Engine.Coordinator.IsHostStreaming && info != null)
        {
            bool isSameAccount = IsSameAccountController(info);
            string localName = App.Engine.AccountService.GetEffectiveLocalDeviceName();
            string who;
            string whom;
            if (isSameAccount)
            {
                string rawCtrl = !string.IsNullOrWhiteSpace(info.ControllerDeviceName)
                    ? info.ControllerDeviceName
                    : info.PeerName;
                who = App.Engine.AccountService.ResolveAccountDeviceName(info.PeerDeviceId, rawCtrl);
                whom = localName;
            }
            else
            {
                who = string.IsNullOrWhiteSpace(info.ControllerDisplayName)
                    ? info.PeerName
                    : info.ControllerDisplayName;
                whom = string.IsNullOrWhiteSpace(info.HostDisplayName) ? localName : info.HostDisplayName;
            }

            string headline = LocalizationService.Format("Session_WhoControllingWhomFmt", who, whom);
            ActiveHostBanner.Title = headline;
            HostControllingTitleText.Text = headline;
            BlacklistHostControllerBtn.Visibility =
                App.Engine.AccountService.IsLoggedIn &&
                !string.IsNullOrWhiteSpace(info.ControllerUserId) &&
                !isSameAccount
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
        else
        {
            ActiveHostBanner.Title = LocalizationService.GetString("Title_HostStreaming");
            HostControllingTitleText.Text = LocalizationService.GetString("Home_HostControllingOverlay");
            BlacklistHostControllerBtn.Visibility = Visibility.Collapsed;
        }
    }
}