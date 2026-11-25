using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Osmium.App.Services;
using Osmium.Core.Models;
using Windows.System;
using Windows.UI;

namespace Osmium.App.Views;

public sealed partial class QuickLaunchPage : Page
{
    private const string DriveIconGeometry =
        "M2,5.5 A1.5,1.5 0 0 1 3.5,4 H16.5 A1.5,1.5 0 0 1 18,5.5 V14.5 A1.5,1.5 0 0 1 16.5,16 H3.5 A1.5,1.5 0 0 1 2,14.5 Z M3.5,5.5 V11.5 H16.5 V5.5 Z M14,13.75 A0.85,0.85 0 1 0 15.7,13.75 A0.85,0.85 0 1 0 14,13.75 Z";

    private const string FolderIconGeometry =
        "M2,4.5 A1.5,1.5 0 0 1 3.5,3 H7.6 A1.5,1.5 0 0 1 8.8,3.6 L10,5 H16.5 A1.5,1.5 0 0 1 18,6.5 V15.5 A1.5,1.5 0 0 1 16.5,17 H3.5 A1.5,1.5 0 0 1 2,15.5 Z";

    private const string AppWindowIconGeometry =
        "M2,4.5 A1.5,1.5 0 0 1 3.5,3 H16.5 A1.5,1.5 0 0 1 18,4.5 V15.5 A1.5,1.5 0 0 1 16.5,17 H3.5 A1.5,1.5 0 0 1 2,15.5 Z M3.5,7 V15.5 H16.5 V7 Z";

    private sealed class SelectableHostPc
    {
        public required string DeviceId { get; init; }
        public required string DisplayName { get; init; }
        public required string Subtitle { get; init; }
    }

    private sealed class BrowserListItem
    {
        public required string Name { get; init; }
        public required string FullPath { get; init; }
        public required bool IsDirectory { get; init; }
    }

    private bool _inAddWizard;
    private int _wizardStep;
    private int _wizardAnimGen;
    private bool _wizardBusy;
    private string _wizardHostDeviceId = string.Empty;
    private string _wizardHostDisplayName = string.Empty;
    private string _wizardParentPath = string.Empty;
    private string? _wizardValidatedExePath;
    private string? _wizardValidatedIconData;

    private static Geometry CreateGeometry(string pathData)
        => (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), pathData);

    private void SlideHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement fe && e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            fe.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            };
        }
    }

    private void SnapToLauncherMainImmediate()
    {
        _inAddWizard = false;
        _wizardStep = 0;
        _wizardBusy = false;
        HideWizardError();

        LauncherMainView.Visibility = Visibility.Visible;
        LauncherMainView.Opacity = 1;
        LauncherMainTransform.X = 0;

        AddWizardView.Visibility = Visibility.Collapsed;
        AddWizardView.Opacity = 1;
        AddWizardTransform.X = 0;
    }

    private void AddAppBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Engine.AccountService.IsLoggedIn || _inAddWizard) return;
        OpenAddWizard();
    }

    private void OpenAddWizard()
    {
        _inAddWizard = true;
        _wizardStep = 0;
        _wizardHostDeviceId = string.Empty;
        _wizardHostDisplayName = string.Empty;
        _wizardParentPath = string.Empty;
        _wizardValidatedExePath = null;
        _wizardValidatedIconData = null;
        WizardPathBox.Text = string.Empty;
        WizardAppNameBox.Text = string.Empty;
        WizardBrowserList.Items.Clear();
        WizardCandidateHintText.Visibility = Visibility.Collapsed;
        HideWizardError();
        SetWizardBusy(false);

        PopulateWizardHostChoices();
        SnapWizardStepPanelsImmediate(0);
        UpdateWizardStepVisuals();

        int gen = ++_wizardAnimGen;
        AnimateHorizontalSwap(
            LauncherMainView,
            LauncherMainTransform,
            AddWizardView,
            AddWizardTransform,
            forward: true,
            isStillCurrent: () => gen == _wizardAnimGen);
    }

    private void CloseAddWizard(bool reloadApps)
    {
        if (!_inAddWizard) return;
        _inAddWizard = false;
        _wizardBusy = false;
        HideWizardError();

        int gen = ++_wizardAnimGen;
        AnimateHorizontalSwap(
            AddWizardView,
            AddWizardTransform,
            LauncherMainView,
            LauncherMainTransform,
            forward: false,
            isStillCurrent: () => gen == _wizardAnimGen);

        if (reloadApps)
        {
            _ = LoadAppsAsync();
        }
    }

    private void PopulateWizardHostChoices()
    {
        WizardHostListPanel.Children.Clear();
        var hosts = BuildEligibleHostList();

        if (hosts.Count == 0)
        {
            WizardNoHostPanel.Visibility = Visibility.Visible;
            return;
        }

        WizardNoHostPanel.Visibility = Visibility.Collapsed;
        for (int i = 0; i < hosts.Count; i++)
        {
            if (i > 0)
            {
                WizardHostListPanel.Children.Add(new Border
                {
                    Style = (Style)Application.Current.Resources["OsmiumHorizontalDividerStyle"]
                });
            }

            var host = hosts[i];
            var rowGrid = new Grid { ColumnSpacing = 14, MinHeight = 38 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pcIcon = new FontIcon
            {
                Glyph = "\uF35A",
                FontSize = 22,
                Width = 28,
                Height = 28,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            if (Application.Current.Resources.TryGetValue("FluentIconsFont", out object? ffObj) && ffObj is FontFamily ff)
            {
                pcIcon.FontFamily = ff;
            }

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            textStack.Children.Add(new TextBlock
            {
                Text = host.DisplayName,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium
            });
            textStack.Children.Add(new TextBlock
            {
                Text = host.Subtitle,
                FontSize = 12,
                Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush
            });

            var chevron = new FontIcon
            {
                Glyph = "\uF2AF",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush
            };
            if (ffObj is FontFamily ff2)
            {
                chevron.FontFamily = ff2;
            }

            Grid.SetColumn(pcIcon, 0);
            Grid.SetColumn(textStack, 1);
            Grid.SetColumn(chevron, 2);
            rowGrid.Children.Add(pcIcon);
            rowGrid.Children.Add(textStack);
            rowGrid.Children.Add(chevron);

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(18, 14, 18, 14),
                Content = rowGrid
            };
            btn.Click += (_, _) => SelectWizardHost(host);
            WizardHostListPanel.Children.Add(btn);
        }
    }

    private void SelectWizardHost(SelectableHostPc host)
    {
        if (_wizardBusy) return;
        _wizardHostDeviceId = host.DeviceId;
        _wizardHostDisplayName = host.DisplayName;
        _wizardParentPath = string.Empty;
        _wizardValidatedExePath = null;
        _wizardValidatedIconData = null;
        WizardPathBox.Text = string.Empty;
        WizardSelectedHostCaption.Text = host.DisplayName;
        WizardCandidateHintText.Visibility = Visibility.Collapsed;
        HideWizardError();

        TransitionWizardStep(1, forward: true);
        _ = BrowseWizardPathAsync(string.Empty);
    }

    private void TransitionWizardStep(int targetStep, bool forward)
    {
        int prev = _wizardStep;
        _wizardStep = Math.Clamp(targetStep, 0, 2);
        UpdateWizardStepVisuals();

        var fromPanel = GetWizardStepPanel(prev);
        var toPanel = GetWizardStepPanel(_wizardStep);
        if (ReferenceEquals(fromPanel, toPanel))
        {
            SnapWizardStepPanelsImmediate(_wizardStep);
            return;
        }

        StackPanel[] all = [StepPanelSelectHost, StepPanelSelectPath, StepPanelConfirmAdd];
        foreach (var p in all)
        {
            if (!ReferenceEquals(p, toPanel))
            {
                p.Visibility = Visibility.Collapsed;
                p.Opacity = 1;
                GetWizardStepTransform(p).X = 0;
            }
        }

        int gen = ++_wizardAnimGen;
        AnimateHorizontalSwap(
            fromPanel,
            GetWizardStepTransform(fromPanel),
            toPanel,
            GetWizardStepTransform(toPanel),
            forward,
            isStillCurrent: () => gen == _wizardAnimGen);
    }

    private void SnapWizardStepPanelsImmediate(int step)
    {
        StepPanelSelectHost.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        StepPanelSelectPath.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepPanelConfirmAdd.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepSelectHostTransform.X = 0;
        StepSelectPathTransform.X = 0;
        StepConfirmAddTransform.X = 0;
        StepPanelSelectHost.Opacity = 1;
        StepPanelSelectPath.Opacity = 1;
        StepPanelConfirmAdd.Opacity = 1;
    }

    private StackPanel GetWizardStepPanel(int step) => step switch
    {
        0 => StepPanelSelectHost,
        1 => StepPanelSelectPath,
        _ => StepPanelConfirmAdd
    };

    private TranslateTransform GetWizardStepTransform(StackPanel panel)
    {
        if (ReferenceEquals(panel, StepPanelSelectHost)) return StepSelectHostTransform;
        if (ReferenceEquals(panel, StepPanelSelectPath)) return StepSelectPathTransform;
        return StepConfirmAddTransform;
    }

    private void UpdateWizardStepVisuals()
    {
        ApplyWizardStepNode(QlStepBadge1, QlStepNum1, QlStepCheck1, QlStepLabel1, 0, _wizardStep);
        ApplyWizardStepNode(QlStepBadge2, QlStepNum2, QlStepCheck2, QlStepLabel2, 1, _wizardStep);
        ApplyWizardStepNode(QlStepBadge3, QlStepNum3, QlStepCheck3, QlStepLabel3, 2, _wizardStep);

        var accentBg = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush;
        var dividerBg = Application.Current.Resources["OsmiumDividerBrush"] as Brush;
        if (accentBg != null && dividerBg != null)
        {
            QlStepConnector1.Background = _wizardStep >= 1 ? accentBg : dividerBg;
            QlStepConnector2.Background = _wizardStep >= 2 ? accentBg : dividerBg;
        }

        QlStepProgressText.Text = $"{_wizardStep + 1} / 3";

        if (_wizardStep == 0)
        {
            BtnQlWizardPrimary.Visibility = Visibility.Collapsed;
        }
        else if (_wizardStep == 1)
        {
            BtnQlWizardPrimary.Visibility = Visibility.Visible;
            BtnQlWizardPrimaryText.Text = LocalizationService.GetString("QL_StepNextBtn");
            BtnQlWizardPrimaryIcon.Visibility = Visibility.Visible;
        }
        else
        {
            BtnQlWizardPrimary.Visibility = Visibility.Visible;
            BtnQlWizardPrimaryText.Text = LocalizationService.GetString("QL_ConfirmAddBtn");
            BtnQlWizardPrimaryIcon.Visibility = Visibility.Collapsed;
        }

        UpdateWizardPrimaryEnabled();
    }

    private static void ApplyWizardStepNode(
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

    private void ShowWizardError(string message)
    {
        WizardErrorBar.Message = message;
        WizardErrorBar.IsOpen = true;
        WizardErrorBar.Visibility = Visibility.Visible;
    }

    private void HideWizardError()
    {
        WizardErrorBar.IsOpen = false;
        WizardErrorBar.Visibility = Visibility.Collapsed;
    }

    private void WizardErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
        => WizardErrorBar.Visibility = Visibility.Collapsed;

    private void SetWizardBusy(bool busy)
    {
        _wizardBusy = busy;
        WizardBusyRing.IsActive = busy;
        WizardBusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        WizardBrowserRing.IsActive = busy && _wizardStep == 1;
        WizardBrowserRing.Visibility = busy && _wizardStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        WizardBrowserList.Opacity = busy && _wizardStep == 1 ? 0.45 : 1.0;
        BtnQlWizardBack.IsEnabled = !busy;
        WizardDrivesBtn.IsEnabled = !busy;
        WizardOpenPathBtn.IsEnabled = !busy;
        WizardUpBtn.IsEnabled = !busy && !string.IsNullOrEmpty(WizardPathBox.Text);
        WizardPathBox.IsEnabled = !busy;
        WizardAppNameBox.IsEnabled = !busy;
        UpdateWizardPrimaryEnabled();
    }

    private void UpdateWizardPrimaryEnabled()
    {
        if (_wizardStep == 1)
        {
            BtnQlWizardPrimary.IsEnabled = !_wizardBusy;
        }
        else if (_wizardStep == 2)
        {
            BtnQlWizardPrimary.IsEnabled = !_wizardBusy &&
                                           !string.IsNullOrWhiteSpace(_wizardValidatedExePath) &&
                                           !string.IsNullOrWhiteSpace(WizardAppNameBox.Text);
        }
    }

    private void BtnQlWizardBack_Click(object sender, RoutedEventArgs e)
    {
        if (_wizardBusy) return;
        HideWizardError();
        if (_wizardStep <= 0)
        {
            CloseAddWizard(reloadApps: false);
            return;
        }

        if (_wizardStep == 2)
        {
            TransitionWizardStep(1, forward: false);
            return;
        }

        PopulateWizardHostChoices();
        TransitionWizardStep(0, forward: false);
    }

    private void BtnQlWizardPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_wizardBusy) return;
        if (_wizardStep == 1)
        {
            _ = ValidateAndAdvanceAsync(WizardPathBox.Text.Trim());
        }
        else if (_wizardStep == 2)
        {
            _ = SubmitWizardAddAsync();
        }
    }

    private void WizardDrivesBtn_Click(object sender, RoutedEventArgs e)
        => _ = BrowseWizardPathAsync(string.Empty);

    private void WizardUpBtn_Click(object sender, RoutedEventArgs e)
        => _ = BrowseWizardPathAsync(_wizardParentPath);

    private void WizardOpenPathBtn_Click(object sender, RoutedEventArgs e)
        => _ = BrowseWizardPathAsync(WizardPathBox.Text.Trim());

    private void WizardPathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || _wizardBusy) return;
        e.Handled = true;
        string raw = WizardPathBox.Text.Trim();
        if (raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            _ = ValidateAndAdvanceAsync(raw);
        }
        else
        {
            _ = BrowseWizardPathAsync(raw);
        }
    }

    private void WizardAppNameBox_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateWizardPrimaryEnabled();

    private void WizardAppNameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !_wizardBusy && BtnQlWizardPrimary.IsEnabled)
        {
            e.Handled = true;
            _ = SubmitWizardAddAsync();
        }
    }

    private void WizardBrowserList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_wizardBusy) return;
        if (e.ClickedItem is FrameworkElement fe && fe.Tag is BrowserListItem item)
        {
            if (item.IsDirectory)
            {
                _ = BrowseWizardPathAsync(item.FullPath);
            }
            else
            {
                WizardPathBox.Text = item.FullPath;
                _ = ValidateAndAdvanceAsync(item.FullPath);
            }
        }
    }

    private async Task BrowseWizardPathAsync(string targetPath)
    {
        if (_wizardBusy) return;
        HideWizardError();
        WizardCandidateHintText.Visibility = Visibility.Collapsed;
        SetWizardBusy(true);
        try
        {
            var res = await App.Engine.QuickLaunch.BrowseAsync(_wizardHostDeviceId, targetPath);
            if (!res.Ok)
            {
                ShowWizardError(FormatQuickLaunchError(res.Code, res.Reason));
                return;
            }

            WizardPathBox.Text = res.CurrentPath;
            _wizardParentPath = res.ParentPath;
            WizardBrowserList.Items.Clear();

            foreach (var dir in res.Directories)
            {
                WizardBrowserList.Items.Add(BuildBrowserRow(new BrowserListItem
                {
                    Name = dir.Name,
                    FullPath = dir.Path,
                    IsDirectory = true
                }));
            }

            foreach (var exe in res.Executables)
            {
                WizardBrowserList.Items.Add(BuildBrowserRow(new BrowserListItem
                {
                    Name = exe.Name,
                    FullPath = exe.Path,
                    IsDirectory = false
                }));
            }
        }
        finally
        {
            SetWizardBusy(false);
        }
    }

    private async Task ValidateAndAdvanceAsync(string targetPath)
    {
        if (_wizardBusy) return;
        HideWizardError();
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            ShowWizardError(LocalizationService.GetString("QL_ErrEnterPath"));
            return;
        }

        SetWizardBusy(true);
        try
        {
            var res = await App.Engine.QuickLaunch.ValidateAsync(_wizardHostDeviceId, targetPath);
            if (!res.Ok)
            {
                ShowWizardError(FormatQuickLaunchError(res.Code, res.Reason));
                return;
            }

            if (res.NeedsExeSelection && res.Candidates.Count > 0)
            {
                WizardCandidateHintText.Text = LocalizationService.GetString("QL_SelectExeCandidateLabel");
                WizardCandidateHintText.Visibility = Visibility.Visible;
                WizardBrowserList.Items.Clear();
                foreach (var c in res.Candidates)
                {
                    WizardBrowserList.Items.Add(BuildBrowserRow(new BrowserListItem
                    {
                        Name = c.Name,
                        FullPath = c.Path,
                        IsDirectory = false
                    }));
                }

                return;
            }

            WizardCandidateHintText.Visibility = Visibility.Collapsed;
            _wizardValidatedExePath = res.ResolvedExePath;
            _wizardValidatedIconData = res.IconData;

            WizardConfirmHostText.Text = _wizardHostDisplayName;
            WizardConfirmPathText.Text = res.ResolvedExePath;
            WizardAppNameBox.Text = string.IsNullOrWhiteSpace(res.SuggestedName)
                ? Path.GetFileNameWithoutExtension(res.ResolvedExePath)
                : res.SuggestedName;

            WizardPreviewIcon.Source = null;
            WizardPreviewFallback.Visibility = Visibility.Visible;
            if (!string.IsNullOrWhiteSpace(res.IconData))
            {
                var bmp = await DecodeDataUriToBitmapAsync(res.IconData);
                if (bmp != null)
                {
                    _iconCache[res.IconData] = bmp;
                    WizardPreviewIcon.Source = bmp;
                    WizardPreviewFallback.Visibility = Visibility.Collapsed;
                }
            }

            TransitionWizardStep(2, forward: true);
            DispatcherQueue.TryEnqueue(() => WizardAppNameBox.Focus(FocusState.Programmatic));
        }
        finally
        {
            SetWizardBusy(false);
        }
    }

    private async Task SubmitWizardAddAsync()
    {
        if (_wizardBusy || string.IsNullOrWhiteSpace(_wizardValidatedExePath) ||
            string.IsNullOrWhiteSpace(WizardAppNameBox.Text))
        {
            return;
        }

        HideWizardError();
        SetWizardBusy(true);
        try
        {
            var saveRes = await App.Engine.AccountService.AddQuickLaunchAppAsync(
                _wizardHostDeviceId,
                WizardAppNameBox.Text.Trim(),
                _wizardValidatedExePath,
                _wizardValidatedIconData);
            if (!saveRes.Ok)
            {
                ShowWizardError(FormatQuickLaunchError(saveRes.Code, string.Empty));
                return;
            }

            CloseAddWizard(reloadApps: true);
        }
        finally
        {
            if (_inAddWizard)
            {
                SetWizardBusy(false);
            }
        }
    }

    private List<SelectableHostPc> BuildEligibleHostList()
    {
        var list = new List<SelectableHostPc>();
        string localId = DeviceIdentity.NormalizeDeviceId(App.Engine.SettingsService.Identity.DeviceId);
        if (App.Engine.SettingsService.Settings.HostAllowQuickLaunch)
        {
            string localName = App.Engine.AccountService.GetEffectiveLocalDeviceName();
            list.Add(new SelectableHostPc
            {
                DeviceId = localId,
                DisplayName = $"{localName} ({LocalizationService.GetString("QL_ThisPcTag")})",
                Subtitle = DeviceIdentity.FormatDeviceId(localId)
            });
        }

        foreach (var peer in App.Engine.AccountService.SameAccountDevices)
        {
            string normId = DeviceIdentity.NormalizeDeviceId(peer.DeviceId);
            if (string.IsNullOrEmpty(normId) || string.Equals(normId, localId, StringComparison.Ordinal)) continue;
            if (!peer.IsComputerDevice || !peer.IsOnline || !peer.AllowQuickLaunch) continue;
            list.Add(new SelectableHostPc
            {
                DeviceId = normId,
                DisplayName = string.IsNullOrWhiteSpace(peer.DeviceName)
                    ? DeviceIdentity.FormatDeviceId(normId)
                    : peer.DeviceName,
                Subtitle = DeviceIdentity.FormatDeviceId(normId)
            });
        }

        return list;
    }

    private static FrameworkElement BuildBrowserRow(BrowserListItem item)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Padding = new Thickness(6, 5, 6, 5),
            Tag = item
        };

        bool isDrive = item.IsDirectory && item.FullPath.Length <= 3 &&
                       item.FullPath.EndsWith(":\\", StringComparison.Ordinal);
        string geo = isDrive ? DriveIconGeometry : (item.IsDirectory ? FolderIconGeometry : AppWindowIconGeometry);
        var pathIcon = new PathIcon
        {
            Data = CreateGeometry(geo),
            Width = 20,
            Height = 20
        };
        if (item.IsDirectory && !isDrive)
        {
            pathIcon.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xE5, 0xA9, 0x3C));
        }

        var icon = new Viewbox
        {
            Width = 17,
            Height = 17,
            VerticalAlignment = VerticalAlignment.Center,
            Child = pathIcon
        };

        var text = new TextBlock
        {
            Text = item.Name,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        row.Children.Add(icon);
        row.Children.Add(text);
        return row;
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
            incomingTransform.X = 0;
            incoming.Opacity = 1;
        };
        sb.Begin();
    }
}
