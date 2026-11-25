using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Osmium.App.Helpers;
using Osmium.App.Services;
using Osmium.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace Osmium.App.Views;

public sealed partial class QuickLaunchPage : Page
{
    private readonly List<QuickLaunchAppDto> _apps = [];
    private readonly Dictionary<string, BitmapImage> _iconCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BitmapImage> _grayIconCache = new(StringComparer.Ordinal);
    private string _selectedDeviceFilter = "ALL";
    private bool _isLoading;
    private bool _isRefreshingAll;
    private bool _isLaunching;
    private bool _suppressToggle;
    private bool _suppressFilter;
    private Action<string>? _activeStageSink;
    private Action? _activeStreamStartedSink;

    public QuickLaunchPage()
    {
        InitializeComponent();
        Loaded += QuickLaunchPage_Loaded;
        Unloaded += QuickLaunchPage_Unloaded;
        ActualThemeChanged += (_, _) =>
        {
            if (!_inAddWizard) RenderLauncherGrid();
            else UpdateWizardStepVisuals();
        };
        LocalizationService.LanguageChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyLocalization();
            RebuildDeviceFilterCombo();
            if (_inAddWizard)
            {
                UpdateWizardStepVisuals();
                if (_wizardStep == 0) PopulateWizardHostChoices();
            }
            else
            {
                RenderLauncherGrid();
            }
        });
    }

    private void QuickLaunchPage_Loaded(object sender, RoutedEventArgs e)
    {
        App.Engine.AccountService.SessionChanged -= OnSessionChanged;
        App.Engine.AccountService.SessionChanged += OnSessionChanged;
        App.Engine.AccountService.SameAccountDevicesChanged -= OnDevicesChanged;
        App.Engine.AccountService.SameAccountDevicesChanged += OnDevicesChanged;
        App.Engine.Signaling.QuickLaunchAppsSyncReceived -= OnAppsSyncReceived;
        App.Engine.Signaling.QuickLaunchAppsSyncReceived += OnAppsSyncReceived;
        App.Engine.Coordinator.HostSessionStarted -= OnHostSessionChanged;
        App.Engine.Coordinator.HostSessionStarted += OnHostSessionChanged;
        App.Engine.Coordinator.HostSessionEnded -= OnHostSessionEnded;
        App.Engine.Coordinator.HostSessionEnded += OnHostSessionEnded;
        App.Engine.Coordinator.ClientStageUpdated += OnClientStageUpdated;
        App.Engine.Coordinator.ClientStreamStarted += OnClientStreamStarted;

        SnapToLauncherMainImmediate();
        ApplyLocalization();
        SyncLocalHostToggle();
        RebuildDeviceFilterCombo();
        _ = LoadAppsAsync();
    }

    private void QuickLaunchPage_Unloaded(object sender, RoutedEventArgs e)
    {
        App.Engine.AccountService.SessionChanged -= OnSessionChanged;
        App.Engine.AccountService.SameAccountDevicesChanged -= OnDevicesChanged;
        App.Engine.Signaling.QuickLaunchAppsSyncReceived -= OnAppsSyncReceived;
        App.Engine.Coordinator.HostSessionStarted -= OnHostSessionChanged;
        App.Engine.Coordinator.HostSessionEnded -= OnHostSessionEnded;
        App.Engine.Coordinator.ClientStageUpdated -= OnClientStageUpdated;
        App.Engine.Coordinator.ClientStreamStarted -= OnClientStreamStarted;
    }

    private void OnSessionChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!App.Engine.AccountService.IsLoggedIn && _inAddWizard)
        {
            SnapToLauncherMainImmediate();
        }

        SyncLocalHostToggle();
        RebuildDeviceFilterCombo();
        _ = LoadAppsAsync();
    });

    private void OnDevicesChanged(IReadOnlyList<AccountDeviceDto> _) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_isRefreshingAll) return;
        if (_inAddWizard)
        {
            if (_wizardStep == 0) PopulateWizardHostChoices();
            return;
        }

        RebuildDeviceFilterCombo();
        RenderLauncherGrid();
    });

    private void OnAppsSyncReceived() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_isRefreshingAll) _ = LoadAppsAsync();
    });

    private void OnHostSessionChanged(Osmium.Core.Network.HostSessionActiveInfo _) => DispatcherQueue.TryEnqueue(SyncLocalHostToggle);
    private void OnHostSessionEnded(string _) => DispatcherQueue.TryEnqueue(SyncLocalHostToggle);
    private void OnClientStageUpdated(string stage) => DispatcherQueue.TryEnqueue(() => _activeStageSink?.Invoke(stage));
    private void OnClientStreamStarted() => DispatcherQueue.TryEnqueue(() => _activeStreamStartedSink?.Invoke());

    private void ApplyLocalization()
    {
        PageTitleText.Text = LocalizationService.GetString("QL_PageTitle");
        RefreshBtnText.Text = LocalizationService.GetString("Gamepad_RefreshBtn");
        AddAppBtnText.Text = LocalizationService.GetString("QL_AddAppBtn");
        HostToggleTitleText.Text = LocalizationService.GetString("Host_RowAllowQuickLaunch");
        HostToggleDescText.Text = LocalizationService.GetString("Host_RowAllowQuickLaunchDesc");
        LoggedOutTitleText.Text = LocalizationService.GetString("QL_LoggedOutTitle");
        LoggedOutDescText.Text = LocalizationService.GetString("QL_LoggedOutDesc");
        GoLoginBtn.Content = LocalizationService.GetString("QL_GoLoginBtn");
        LoadingText.Text = LocalizationService.GetString("QL_Loading");
        EmptyTitleText.Text = LocalizationService.GetString("QL_EmptyTitle");
        EmptyDescText.Text = LocalizationService.GetString("QL_EmptyDesc");
        EmptyAddBtn.Content = LocalizationService.GetString("QL_AddAppBtn");

        WizardPageTitleText.Text = LocalizationService.GetString("QL_AddDialogTitle");
        QlStepLabel1.Text = LocalizationService.GetString("QL_StepSelectHost");
        QlStepLabel2.Text = LocalizationService.GetString("QL_StepSelectDir");
        QlStepLabel3.Text = LocalizationService.GetString("QL_StepConfirm");
        WizardDrivesBtn.Content = LocalizationService.GetString("QL_DrivesBtn");
        WizardUpBtn.Content = LocalizationService.GetString("QL_UpBtn");
        WizardOpenPathBtn.Content = LocalizationService.GetString("QL_BrowseBtn");
        WizardPathBox.PlaceholderText = LocalizationService.GetString("QL_PathPlaceholder");
        WizardNoHostText.Text = LocalizationService.GetString("QL_NoEligibleHostDesc");
        WizardAppNameLabel.Text = LocalizationService.GetString("QL_AppNameLabel");
        BtnQlWizardBackText.Text = LocalizationService.GetString("QL_StepBackBtn");
        UpdateWizardStepVisuals();
    }

    private void SyncLocalHostToggle()
    {
        bool prev = _suppressToggle;
        _suppressToggle = true;
        LocalQuickLaunchToggle.IsOn = App.Engine.SettingsService.Settings.HostAllowQuickLaunch;
        LocalQuickLaunchToggle.IsEnabled = !App.Engine.Coordinator.IsHostStreaming;
        _suppressToggle = prev;
    }

    private async void LocalQuickLaunchToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle || App.Engine.Coordinator.IsHostStreaming) return;
        var s = App.Engine.SettingsService.Settings;
        s.HostAllowQuickLaunch = LocalQuickLaunchToggle.IsOn;
        App.Engine.SettingsService.SaveSettings();
        _isRefreshingAll = true;
        try
        {
            await App.Engine.SyncSignalingStatusAsync();
        }
        finally
        {
            _isRefreshingAll = false;
        }

        RenderLauncherGrid();
    }

    private void RebuildDeviceFilterCombo()
    {
        bool prev = _suppressFilter;
        _suppressFilter = true;
        try
        {
            string current = _selectedDeviceFilter;
            DeviceFilterCombo.Items.Clear();
            DeviceFilterCombo.Items.Add(new ComboBoxItem
            {
                Content = LocalizationService.GetString("QL_FilterAllDevices"),
                Tag = "ALL"
            });

            var seen = new HashSet<string>(StringComparer.Ordinal);
            string localId = DeviceIdentity.NormalizeDeviceId(App.Engine.SettingsService.Identity.DeviceId);
            foreach (var app in _apps)
            {
                string devId = DeviceIdentity.NormalizeDeviceId(app.DeviceId);
                if (string.IsNullOrEmpty(devId) || !seen.Add(devId)) continue;
                string label = ResolveDeviceDisplayName(devId, app.DeviceName);
                if (string.Equals(devId, localId, StringComparison.Ordinal))
                {
                    label += $" ({LocalizationService.GetString("QL_ThisPcTag")})";
                }

                DeviceFilterCombo.Items.Add(new ComboBoxItem
                {
                    Content = label,
                    Tag = devId
                });
            }

            int match = 0;
            for (int i = 0; i < DeviceFilterCombo.Items.Count; i++)
            {
                if (DeviceFilterCombo.Items[i] is ComboBoxItem ci &&
                    string.Equals(ci.Tag?.ToString(), current, StringComparison.Ordinal))
                {
                    match = i;
                    break;
                }
            }

            DeviceFilterCombo.SelectedIndex = match;
            _selectedDeviceFilter = (DeviceFilterCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ALL";
        }
        finally
        {
            _suppressFilter = prev;
        }
    }

    private void DeviceFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilter) return;
        if (DeviceFilterCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _selectedDeviceFilter = tag;
            RenderLauncherGrid();
        }
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        => _ = RefreshAllAsync();

    private async Task RefreshAllAsync()
    {
        if (_isRefreshingAll || _isLoading) return;
        _isRefreshingAll = true;
        RefreshBtn.IsEnabled = false;
        try
        {
            if (App.Engine.AccountService.IsLoggedIn)
            {
                await App.Engine.AccountService.RefreshDevicesAsync();
            }

            await LoadAppsAsync();
        }
        finally
        {
            _isRefreshingAll = false;
            RefreshBtn.IsEnabled = App.Engine.AccountService.IsLoggedIn;
        }
    }

    private void GoLoginBtn_Click(object sender, RoutedEventArgs e)
        => App.MainWindowInstance?.NavigateToAccount();

    private async Task LoadAppsAsync()
    {
        if (!App.Engine.AccountService.IsLoggedIn)
        {
            _apps.Clear();
            AddAppBtn.IsEnabled = false;
            DeviceFilterCombo.IsEnabled = false;
            RefreshBtn.IsEnabled = false;
            LoggedOutStatePanel.Visibility = Visibility.Visible;
            LoadingPanel.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            AppsScrollViewer.Visibility = Visibility.Collapsed;
            return;
        }

        AddAppBtn.IsEnabled = true;
        DeviceFilterCombo.IsEnabled = true;
        RefreshBtn.IsEnabled = !_isRefreshingAll;
        LoggedOutStatePanel.Visibility = Visibility.Collapsed;

        if (_isLoading) return;
        _isLoading = true;
        if (_apps.Count == 0)
        {
            LoadingPanel.Visibility = Visibility.Visible;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            AppsScrollViewer.Visibility = Visibility.Collapsed;
        }

        try
        {
            var res = await App.Engine.AccountService.GetQuickLaunchAppsAsync();
            if (res.Ok && res.Data != null)
            {
                _apps.Clear();
                _apps.AddRange(res.Data.Apps);
            }
        }
        finally
        {
            _isLoading = false;
            LoadingPanel.Visibility = Visibility.Collapsed;
            RebuildDeviceFilterCombo();
            RenderLauncherGrid();
        }
    }

    private string ResolveDeviceDisplayName(string deviceId, string fallback)
    {
        string norm = DeviceIdentity.NormalizeDeviceId(deviceId);
        string localId = DeviceIdentity.NormalizeDeviceId(App.Engine.SettingsService.Identity.DeviceId);
        if (string.Equals(norm, localId, StringComparison.Ordinal))
        {
            return App.Engine.AccountService.GetEffectiveLocalDeviceName();
        }

        var peer = App.Engine.AccountService.FindPeerDevice(norm);
        if (peer != null && !string.IsNullOrWhiteSpace(peer.DeviceName))
        {
            return peer.DeviceName;
        }

        return !string.IsNullOrWhiteSpace(fallback) ? fallback : DeviceIdentity.FormatDeviceId(norm);
    }

    private (bool IsLocal, bool IsCached, bool IsOnline, bool AllowQuickLaunch, bool AllowRemote, bool IsBusy) GetLiveDeviceStatus(QuickLaunchAppDto app)
    {
        string norm = DeviceIdentity.NormalizeDeviceId(app.DeviceId);
        string localId = DeviceIdentity.NormalizeDeviceId(App.Engine.SettingsService.Identity.DeviceId);
        if (string.Equals(norm, localId, StringComparison.Ordinal))
        {
            return (
                IsLocal: true,
                IsCached: true,
                IsOnline: true,
                AllowQuickLaunch: App.Engine.SettingsService.Settings.HostAllowQuickLaunch,
                AllowRemote: App.Engine.Coordinator.EffectiveAllowRemoteControl,
                IsBusy: App.Engine.Coordinator.IsHostStreaming);
        }

        var peer = App.Engine.AccountService.FindPeerDevice(norm);
        if (peer != null)
        {
            return (
                IsLocal: false,
                IsCached: true,
                IsOnline: peer.IsOnline,
                AllowQuickLaunch: peer.AllowQuickLaunch,
                AllowRemote: peer.AllowRemote,
                IsBusy: peer.IsBeingControlled);
        }

        bool hasDevicesSnapshot = App.Engine.AccountService.SameAccountDevices.Count > 0;
        bool isCached = !hasDevicesSnapshot && app.DeviceCached;
        return (false, isCached, isCached && app.DeviceOnline, isCached && app.AllowQuickLaunch, isCached && app.AllowRemote, false);
    }

    private void RenderLauncherGrid()
    {
        if (!App.Engine.AccountService.IsLoggedIn) return;

        var filtered = _selectedDeviceFilter == "ALL"
            ? _apps
            : _apps.Where(a => string.Equals(DeviceIdentity.NormalizeDeviceId(a.DeviceId), _selectedDeviceFilter, StringComparison.Ordinal)).ToList();

        AppsGridView.Items.Clear();
        if (filtered.Count == 0)
        {
            AppsScrollViewer.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Visible;
            return;
        }

        EmptyStatePanel.Visibility = Visibility.Collapsed;
        AppsScrollViewer.Visibility = Visibility.Visible;

        bool isDark = ActualTheme == ElementTheme.Dark;
        foreach (var app in filtered)
        {
            AppsGridView.Items.Add(BuildAppIconTile(app, isDark));
        }
    }

    private UIElement BuildAppIconTile(QuickLaunchAppDto app, bool isDark)
    {
        var status = GetLiveDeviceStatus(app);
        bool isCacheLost = !status.IsOnline && !status.IsCached;
        bool ready = status.IsOnline && status.AllowQuickLaunch && (status.IsLocal || (status.AllowRemote && !status.IsBusy));
        string deviceLabel = status.IsLocal
            ? LocalizationService.GetString("QL_ThisPcTag")
            : ResolveDeviceDisplayName(app.DeviceId, app.DeviceName);

        var iconBorder = new Border
        {
            Width = 68,
            Height = 68,
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(isDark
                ? Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2E)
                : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(isDark
                ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x18, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var iconContentGrid = new Grid();
        var fallbackPathIcon = new PathIcon
        {
            Data = CreateGeometry(AppWindowIconGeometry),
            Width = 20,
            Height = 20,
            Opacity = isCacheLost ? 0.85 : 0.72
        };
        if (isCacheLost)
        {
            fallbackPathIcon.Foreground = new SolidColorBrush(isDark
                ? Color.FromArgb(0xFF, 0xD4, 0xD4, 0xD8)
                : Color.FromArgb(0xFF, 0x4B, 0x4B, 0x4F));
        }

        var fallbackIcon = new Viewbox
        {
            Width = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = fallbackPathIcon
        };

        iconContentGrid.Children.Add(fallbackIcon);

        if (!string.IsNullOrWhiteSpace(app.IconData))
        {
            var img = new Image
            {
                Width = 46,
                Height = 46,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconContentGrid.Children.Add(img);
            var cache = isCacheLost ? _grayIconCache : _iconCache;
            if (cache.TryGetValue(app.IconData, out var cached))
            {
                img.Source = cached;
                fallbackIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                _ = LoadIconIntoImageAsync(app.IconData, img, fallbackIcon, grayscale: isCacheLost);
            }
        }

        Color dotColor = !status.IsOnline || !status.AllowQuickLaunch
            ? Color.FromArgb(0xFF, 0x8E, 0x8E, 0x93)
            : (status.IsBusy
                ? Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B)
                : Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E));

        var statusDot = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(dotColor),
            BorderBrush = new SolidColorBrush(isDark
                ? Color.FromArgb(0xFF, 0x20, 0x20, 0x22)
                : Color.FromArgb(0xFF, 0xF6, 0xF6, 0xF8)),
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -2, -2)
        };

        iconBorder.Child = iconContentGrid;

        var iconHost = new Grid
        {
            Width = 68,
            Height = 68,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        iconHost.Children.Add(iconBorder);
        iconHost.Children.Add(statusDot);

        var nameText = new TextBlock
        {
            Text = app.Name,
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.WrapWholeWords,
            MaxLines = 2,
            MaxWidth = 92,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var devText = new TextBlock
        {
            Text = deviceLabel,
            FontSize = 10.5,
            Opacity = 0.62,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            MaxWidth = 92,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var stack = new StackPanel
        {
            Spacing = 6,
            Width = 96,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = isCacheLost ? 1.0 : (ready ? 1.0 : 0.68)
        };
        stack.Children.Add(iconHost);
        stack.Children.Add(nameText);
        stack.Children.Add(devText);

        var tileBtn = new Button
        {
            Content = stack,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(6, 8, 6, 8),
            Width = 108,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Top
        };

        var flyout = new MenuFlyout();
        var launchItem = new MenuFlyoutItem
        {
            Text = LocalizationService.GetString(status.IsLocal ? "QL_MenuLaunchLocal" : "QL_MenuLaunchRemote")
        };
        launchItem.Click += (_, _) => _ = HandleAppTileClickedAsync(app);

        var deleteItem = new MenuFlyoutItem
        {
            Text = LocalizationService.GetString("QL_MenuDelete")
        };
        deleteItem.Click += (_, _) => _ = ConfirmDeleteAppAsync(app);

        flyout.Items.Add(launchItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(deleteItem);
        tileBtn.ContextFlyout = flyout;
        PopupCornerHelper.AttachMenuFlyout(flyout, tileBtn);

        string tip = $"{app.Name}\n{deviceLabel}\n{app.ExePath}";
        ToolTipService.SetToolTip(tileBtn, tip);
        tileBtn.Click += (_, _) => _ = HandleAppTileClickedAsync(app);

        return tileBtn;
    }

    private async Task LoadIconIntoImageAsync(string dataUri, Image target, UIElement fallback, bool grayscale = false)
    {
        var bmp = grayscale
            ? await DecodeGrayscaleDataUriToBitmapAsync(dataUri)
            : await DecodeDataUriToBitmapAsync(dataUri);
        if (bmp != null)
        {
            if (grayscale) _grayIconCache[dataUri] = bmp;
            else _iconCache[dataUri] = bmp;
            target.Source = bmp;
            fallback.Visibility = Visibility.Collapsed;
        }
    }

    private static async Task<BitmapImage?> DecodeGrayscaleDataUriToBitmapAsync(string? dataUri)
    {
        if (string.IsNullOrWhiteSpace(dataUri)) return null;
        int comma = dataUri.IndexOf(',');
        if (comma <= 0 || comma >= dataUri.Length - 1) return null;
        try
        {
            byte[] bytes = Convert.FromBase64String(dataUri[(comma + 1)..]);
            if (bytes.Length == 0) return null;

            using var inStream = new InMemoryRandomAccessStream();
            await inStream.WriteAsync(bytes.AsBuffer());
            inStream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(inStream);
            uint w = decoder.PixelWidth;
            uint h = decoder.PixelHeight;
            if (w == 0 || h == 0) return null;

            var pixelProvider = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            byte[] pixels = pixelProvider.DetachPixelData();
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte lum = (byte)Math.Clamp((int)Math.Round(0.2126 * r + 0.7152 * g + 0.0722 * b), 0, 255);
                pixels[i] = lum;
                pixels[i + 1] = lum;
                pixels[i + 2] = lum;
            }

            using var outStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                w,
                h,
                96,
                96,
                pixels);
            await encoder.FlushAsync();

            outStream.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(outStream);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<BitmapImage?> DecodeDataUriToBitmapAsync(string? dataUri)
    {
        if (string.IsNullOrWhiteSpace(dataUri)) return null;
        int comma = dataUri.IndexOf(',');
        if (comma <= 0 || comma >= dataUri.Length - 1) return null;
        try
        {
            byte[] bytes = Convert.FromBase64String(dataUri[(comma + 1)..]);
            if (bytes.Length == 0) return null;
            using var ms = new InMemoryRandomAccessStream();
            await ms.WriteAsync(bytes.AsBuffer());
            ms.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(ms);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private async Task HandleAppTileClickedAsync(QuickLaunchAppDto app)
    {
        if (_isLaunching || XamlRoot == null) return;
        if (!App.Engine.AccountService.IsLoggedIn) return;

        string targetId = DeviceIdentity.NormalizeDeviceId(app.DeviceId);
        string localId = DeviceIdentity.NormalizeDeviceId(App.Engine.SettingsService.Identity.DeviceId);
        if (string.Equals(targetId, localId, StringComparison.Ordinal))
        {
            var dlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = app.Name,
                Content = new TextBlock
                {
                    Text = LocalizationService.GetString("QL_SelfDevicePrompt"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 400
                },
                PrimaryButtonText = LocalizationService.GetString("QL_MenuLaunchLocal"),
                CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                if (!App.Engine.QuickLaunch.TryLaunchLocalExecutable(app.ExePath, out string err, app.Name))
                {
                    await ShowMessageDialogAsync(
                        LocalizationService.GetString("Assist_ErrorTitle"),
                        FormatQuickLaunchError(OsmiumErrorCode.QuickLaunchNotFound, err));
                }
            }
            return;
        }

        if (App.Engine.Coordinator.IsHostStreaming)
        {
            await ShowMessageDialogAsync(
                LocalizationService.GetString("Assist_ErrorTitle"),
                LocalizationService.GetString("QL_ErrLocalHosting"));
            return;
        }

        int cooldownMs = App.Engine.Coordinator.RemainingConnectCooldownMs;
        if (cooldownMs > 0)
        {
            int secs = Math.Max(1, (int)Math.Ceiling(cooldownMs / 1000.0));
            await ShowMessageDialogAsync(
                LocalizationService.GetString("Assist_ErrorTitle"),
                LocalizationService.Format("Assist_ConnectCooldownFmt", secs));
            return;
        }

        var peer = App.Engine.AccountService.FindPeerDevice(targetId);
        if (peer == null)
        {
            await ShowMessageDialogAsync(
                LocalizationService.GetString("Assist_ErrorTitle"),
                LocalizationService.GetString("QL_ErrNotOwnDevice"));
            return;
        }

        if (!peer.IsOnline)
        {
            await ShowMessageDialogAsync(
                LocalizationService.GetString("Assist_ErrorTitle"),
                LocalizationService.GetString("QL_ErrDeviceOffline"));
            return;
        }

        if (!peer.AllowQuickLaunch)
        {
            await ShowMessageDialogAsync(
                LocalizationService.GetString("Assist_ErrorTitle"),
                LocalizationService.GetString("QL_ErrDisabled"));
            return;
        }

        if (!peer.AllowRemote)
        {
            await ShowMessageDialogAsync(
                LocalizationService.GetString("Assist_ErrorTitle"),
                LocalizationService.GetString("QL_ErrRemoteDisabled"));
            return;
        }

        _isLaunching = true;
        using var cts = new CancellationTokenSource();
        try
        {
            var progressRing = new ProgressRing
            {
                IsActive = true,
                Width = 22,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center
            };
            var stageText = new TextBlock
            {
                Text = LocalizationService.GetString("QL_StageChecking"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            var statusRow = new Grid { ColumnSpacing = 12, MinWidth = 320, MaxWidth = 420 };
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(progressRing, 0);
            Grid.SetColumn(stageText, 1);
            statusRow.Children.Add(progressRing);
            statusRow.Children.Add(stageText);

            var dlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = LocalizationService.Format("QL_LaunchingTitleFmt", app.Name),
                Content = statusRow,
                CloseButtonText = LocalizationService.GetString("Assist_CancelBtn"),
                DefaultButton = ContentDialogButton.Close
            };

            bool userCanceled = false;
            bool closedForStream = false;
            bool switchedToError = false;

            _activeStageSink = s =>
            {
                if (!switchedToError && !closedForStream) stageText.Text = s;
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

            async Task<(bool Ok, string Message)> RunFullLaunchAsync()
            {
                var startRes = await App.Engine.QuickLaunch.StartRemoteAppAsync(
                    targetId,
                    app.Id,
                    app.ExePath,
                    app.Name,
                    status =>
                    {
                        if (string.Equals(status, "launching", StringComparison.OrdinalIgnoreCase))
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (!switchedToError && !closedForStream)
                                {
                                    stageText.Text = LocalizationService.GetString("QL_StageWaitingForeground");
                                }
                            });
                        }
                    },
                    cts.Token);

                if (!startRes.Ok || !string.Equals(startRes.Status, "ready", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, FormatQuickLaunchError(startRes.Code, startRes.Reason));
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!switchedToError && !closedForStream)
                    {
                        stageText.Text = LocalizationService.GetString("QL_StageStartingRemote");
                    }
                });

                return await App.Engine.Coordinator.ConnectToSameAccountDeviceAsync(targetId, viewOnly: false, cts.Token);
            }

            var workTask = RunFullLaunchAsync();
            var dialogTask = dlg.ShowAsync().AsTask();
            var finished = await Task.WhenAny(workTask, dialogTask);
            if (finished == workTask)
            {
                var (ok, msg) = await workTask;
                if (!ok && !userCanceled && !closedForStream)
                {
                    switchedToError = true;
                    progressRing.IsActive = false;
                    progressRing.Visibility = Visibility.Collapsed;
                    statusRow.ColumnSpacing = 0;
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
                try
                {
                    var (ok, msg) = await workTask;
                    if (!ok && !userCanceled) await ShowMessageDialogAsync(LocalizationService.GetString("Assist_ErrorTitle"), msg);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await ShowMessageDialogAsync(LocalizationService.GetString("Assist_ErrorTitle"), ex.Message);
        }
        finally
        {
            _activeStageSink = null;
            _activeStreamStartedSink = null;
            _isLaunching = false;
        }
    }

    private async Task ConfirmDeleteAppAsync(QuickLaunchAppDto app)
    {
        if (XamlRoot == null) return;
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString("QL_DeleteTitle"),
            Content = new TextBlock
            {
                Text = LocalizationService.Format("QL_DeleteConfirmFmt", app.Name),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380
            },
            PrimaryButtonText = LocalizationService.GetString("QL_MenuDelete"),
            CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var res = await App.Engine.AccountService.DeleteQuickLaunchAppAsync(app.Id);
        if (res.Ok)
        {
            _apps.RemoveAll(a => string.Equals(a.Id, app.Id, StringComparison.Ordinal));
            RebuildDeviceFilterCombo();
            RenderLauncherGrid();
        }
        else
        {
            await ShowMessageDialogAsync(
                LocalizationService.GetString("Assist_ErrorTitle"),
                FormatQuickLaunchError(res.Code, string.Empty));
        }
    }

    private string FormatQuickLaunchError(int code, string reason)
    {
        if (string.Equals(reason, "DIRECTORY_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reason, "PATH_NOT_FOUND", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationService.GetString("QL_ErrDirNotFound");
        }

        if (string.Equals(reason, "BINARY_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reason, "NO_EXECUTABLE_IN_DIR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reason, "NOT_VALID_EXE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reason, "INVALID_EXECUTABLE", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationService.GetString("QL_ErrBinaryNotFound");
        }

        if (string.Equals(reason, "PROCESS_EXITED_EARLY", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationService.GetString("QL_ErrExitedEarly");
        }

        if (string.Equals(reason, "FOREGROUND_TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reason, "LAUNCH_TIMEOUT", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationService.GetString("QL_ErrForegroundTimeout");
        }

        if (string.Equals(reason, "HOST_BUSY", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationService.GetString("QL_ErrHostBusy");
        }

        if (string.Equals(reason, "ACCESS_DENIED", StringComparison.OrdinalIgnoreCase))
        {
            return LocalizationService.GetString("QL_ErrAccessDenied");
        }

        return code switch
        {
            OsmiumErrorCode.DeviceOffline => LocalizationService.GetString("QL_ErrDeviceOffline"),
            OsmiumErrorCode.Forbidden => LocalizationService.GetString("QL_ErrNotOwnDevice"),
            OsmiumErrorCode.QuickLaunchDisabled => LocalizationService.GetString("QL_ErrDisabled"),
            OsmiumErrorCode.RemoteDisabled => LocalizationService.GetString("QL_ErrRemoteDisabled"),
            OsmiumErrorCode.ConcurrentOperation => LocalizationService.GetString("QL_ErrHostBusy"),
            OsmiumErrorCode.QuickLaunchNotFound => LocalizationService.GetString("QL_ErrBinaryNotFound"),
            OsmiumErrorCode.QuickLaunchLimitExceeded => LocalizationService.GetString("QL_ErrLimitExceeded"),
            OsmiumErrorCode.SelfConnect => LocalizationService.GetString("QL_SelfDevicePrompt"),
            _ => LocalizationService.GetString("QL_ErrGeneric")
        };
    }

    private async Task ShowMessageDialogAsync(string title, string message)
    {
        if (XamlRoot == null) return;
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            CloseButtonText = LocalizationService.GetString("Assist_OkBtn"),
            DefaultButton = ContentDialogButton.Close
        };
        await dlg.ShowAsync();
    }
}
