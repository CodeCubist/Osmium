using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Osmium.App.Helpers;
using Osmium.App.Services;
using Osmium.Core.Models;
using Osmium.Core.Network;
using Osmium.Core.Services;

namespace Osmium.App.Views;

public sealed partial class SettingsPage : Page
{
    private bool _selectingPrivacyWallpaper;

    private void RefreshPrivacyWallpaperButtons()
    {
        bool custom = App.Engine.SettingsService.Settings.HostUsePrivacyWallpaper;
        bool canEdit = !_selectingPrivacyWallpaper && !App.Engine.Coordinator.IsHostStreaming;
        HostPrivacyWallpaperBtn.Content = LocalizationService.GetString(custom
            ? "Host_PrivacyWallpaperChange" : "Host_PrivacyWallpaperChoose");
        HostPrivacyWallpaperBtn.IsEnabled = canEdit;
        HostResetPrivacyWallpaperBtn.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        HostResetPrivacyWallpaperBtn.IsEnabled = canEdit;
    }

    private async void HostPrivacyWallpaperBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectingPrivacyWallpaper || App.Engine.Coordinator.IsHostStreaming || App.MainWindowInstance is not { } window)
            return;

        var settings = App.Engine.SettingsService;
        _selectingPrivacyWallpaper = true;
        RefreshPrivacyWallpaperButtons();
        string? stagingPath = null;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window.AppWindow.Id);
            foreach (string ext in new[] { ".jpg", ".jpeg", ".png", ".bmp" }) picker.FileTypeFilter.Add(ext);
            var picked = await picker.PickSingleFileAsync();
            if (picked == null || App.Engine.Coordinator.IsHostStreaming) return;

            var jpeg = await WindowsWallpaperHelper.ReadPrivacyWallpaperAsync(picked.Path);
            if (jpeg == null) throw new InvalidDataException("Invalid privacy wallpaper image");
            if (App.Engine.Coordinator.IsHostStreaming) return;

            string name = WindowsWallpaperHelper.PrivacyWallpaperFileName;
            string target = Path.Combine(settings.DataDirectory, name);
            stagingPath = Path.Combine(settings.DataDirectory, $"{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(stagingPath, jpeg);
            if (App.Engine.Coordinator.IsHostStreaming) return;
            File.Move(stagingPath, target, overwrite: true);
            stagingPath = null;
            ServiceAutoStartHelper.SyncProfileFileToService(target, name);
            settings.Settings.HostUsePrivacyWallpaper = true;
            settings.SaveSettings();
        }
        catch (Exception ex)
        {
            App.Engine.Logging.Warn($"Could not set privacy screen wallpaper: {ex.Message}");
            if (XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = ActualTheme,
                    Title = LocalizationService.GetString("Host_PrivacyWallpaper"),
                    Content = LocalizationService.GetString("Host_PrivacyWallpaperError"),
                    CloseButtonText = LocalizationService.GetString("Dialog_Ok")
                };
                await dialog.ShowAsync();
            }
        }
        finally
        {
            if (stagingPath != null)
            {
                try { File.Delete(stagingPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _selectingPrivacyWallpaper = false;
            RefreshPrivacyWallpaperButtons();
        }
    }

    private void HostResetPrivacyWallpaperBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectingPrivacyWallpaper || App.Engine.Coordinator.IsHostStreaming) return;
        var settings = App.Engine.SettingsService;
        settings.Settings.HostUsePrivacyWallpaper = false;
        settings.SaveSettings();
        RefreshPrivacyWallpaperButtons();
    }

    private void NetworkNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        var s = App.Engine.SettingsService.Settings;
        s.StunPortOverride = double.IsNaN(StunPortBox.Value) ? 0 : Math.Clamp((int)StunPortBox.Value, 0, 65535);
        s.PacketSize = double.IsNaN(PacketSizeBox.Value) ? 1240 : Math.Clamp((int)PacketSizeBox.Value, 512, 1460);
        App.Engine.SettingsService.SaveSettings();
    }

    private void ClientResCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ClientResCombo.SelectedItem is ComboBoxItem item && item.Tag?.ToString() is string resStr)
        {
            var parts = resStr.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
            {
                var s = App.Engine.SettingsService.Settings;
                s.TargetWidth = w;
                s.TargetHeight = h;
                MaybeAutoAdjustBitrate();
                App.Engine.SettingsService.SaveSettings();
            }
        }
    }

    private void ClientFpsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ClientFpsCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int fps))
        {
            App.Engine.SettingsService.Settings.Fps = fps;
            MaybeAutoAdjustBitrate();
            App.Engine.SettingsService.SaveSettings();
        }
    }

    private async void CustomRes_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Engine.SettingsService.Settings;
        int initW = s.TargetWidth > 0 ? s.TargetWidth : 1920;
        int initH = s.TargetHeight > 0 ? s.TargetHeight : 1080;
        var wBox = new NumberBox
        {
            Header = LocalizationService.GetString("Set_CustomResWidth"), Value = initW, Minimum = 256,
            Maximum = 8192
        };
        var hBox = new NumberBox
        {
            Header = LocalizationService.GetString("Set_CustomResHeight"), Value = initH, Minimum = 144,
            Maximum = 4320
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(wBox);
        panel.Children.Add(hBox);

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString("Settings_CustomResLabel"),
            Content = panel,
            PrimaryButtonText = LocalizationService.GetString("Dialog_Ok"),
            CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            int w = double.IsNaN(wBox.Value) ? initW : Math.Clamp((int)wBox.Value, 256, 8192);
            int h = double.IsNaN(hBox.Value) ? initH : Math.Clamp((int)hBox.Value, 144, 4320);
            s.TargetWidth = w & ~1;
            s.TargetHeight = h & ~1;

            _loading = true;
            PopulateResolutionCombo(s.TargetWidth, s.TargetHeight);
            _loading = false;
            MaybeAutoAdjustBitrate();
            App.Engine.SettingsService.SaveSettings();
        }
    }

    private async void CustomFps_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Engine.SettingsService.Settings;
        var fpsBox = new NumberBox
        {
            Header = $"FPS ({OsmiumSettings.MinStreamFps}–{OsmiumSettings.MaxStreamFps})",
            Value = s.Fps,
            Minimum = OsmiumSettings.MinStreamFps,
            Maximum = OsmiumSettings.MaxStreamFps
        };

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = LocalizationService.GetString("Settings_CustomFpsLabel"),
            Content = fpsBox,
            PrimaryButtonText = LocalizationService.GetString("Dialog_Ok"),
            CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            int fps = double.IsNaN(fpsBox.Value) ? s.Fps : Math.Clamp((int)fpsBox.Value,
                OsmiumSettings.MinStreamFps, OsmiumSettings.MaxStreamFps);
            s.Fps = fps;
            _loading = true;
            PopulateFpsCombo(fps);
            _loading = false;
            MaybeAutoAdjustBitrate();
            App.Engine.SettingsService.SaveSettings();
        }
    }

    private void ResetBitrate_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Engine.SettingsService.Settings;
        s.AutoAdjustBitrate = true;
        s.BitrateKbps = OsmiumSettings.GetDefaultBitrateKbps(s.TargetWidth, s.TargetHeight, s.Fps, s.EnableYuv444);
        _loading = true;
        AutoAdjustBitrateSwitch.IsOn = true;
        UpdateBitrateSliderBoundsAndValue();
        _loading = false;
        App.Engine.SettingsService.SaveSettings();
    }

    private void ClientBitrateSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ClientBitrateLabel == null || _loading) return;
        var s = App.Engine.SettingsService.Settings;
        s.BitrateKbps = (int)e.NewValue;
        s.AutoAdjustBitrate = false;
        _loading = true;
        AutoAdjustBitrateSwitch.IsOn = false;
        UpdateBitrateSliderBoundsAndValue();
        _loading = false;
        App.Engine.SettingsService.SaveSettings();
    }

    private void UnlockBitrateSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = App.Engine.SettingsService.Settings;
        s.UnlockBitrate = UnlockBitrateSwitch.IsOn;
        _loading = true;
        UpdateBitrateSliderBoundsAndValue();
        _loading = false;
        App.Engine.SettingsService.SaveSettings();
    }

    private void ClientCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var s = App.Engine.SettingsService.Settings;

        if (WindowModeCombo.SelectedItem is ComboBoxItem wmItem && int.TryParse(wmItem.Tag?.ToString(), out int wm))
            s.WindowMode = (StreamWindowMode)wm;
        if (ClientCodecCombo.SelectedItem is ComboBoxItem codecItem &&
            int.TryParse(codecItem.Tag?.ToString(), out int c))
            s.VideoCodec = (VideoCodecPreference)c;
        if (ClientDecoderCombo.SelectedItem is ComboBoxItem decItem && int.TryParse(decItem.Tag?.ToString(), out int d))
            s.VideoDecoderMode = (HardwarePreference)d;
        if (AudioChannelsCombo.SelectedItem is ComboBoxItem audItem && int.TryParse(audItem.Tag?.ToString(), out int a))
            s.AudioChannels = (AudioChannelConfig)a;
        if (CaptureSysKeysCombo.SelectedItem is ComboBoxItem skItem && int.TryParse(skItem.Tag?.ToString(), out int sk))
            s.CaptureSysKeys = (CaptureSysKeysMode)sk;
        if (UiDisplayModeCombo.SelectedItem is ComboBoxItem uiItem && int.TryParse(uiItem.Tag?.ToString(), out int ui))
            s.UiDisplayMode = (UiWindowDisplayMode)ui;
        if (SharedClipboardMaxSizeCombo.SelectedItem is ComboBoxItem clipSizeItem &&
            int.TryParse(clipSizeItem.Tag?.ToString(), out int clipMb))
            s.SharedClipboardMaxSizeMb = Math.Clamp(clipMb, 1, 64);
        if (FileTransferDirectionCombo.SelectedItem is ComboBoxItem dirItem &&
            int.TryParse(dirItem.Tag?.ToString(), out int dirVal))
            s.FileTransferDirection = (FileTransferDirection)Math.Clamp(dirVal, 0, 2);
        if (FileTransferMaxSizeCombo.SelectedItem is ComboBoxItem fileSizeItem &&
            int.TryParse(fileSizeItem.Tag?.ToString(), out int fileMb))
            s.FileTransferMaxSizeMb = Math.Clamp(fileMb, 16, 4096);

        App.Engine.SettingsService.SaveSettings();
    }

    private async void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (LanguageCombo.SelectedItem is not ComboBoxItem item || item.Tag?.ToString() is not string lang)
            return;

        if (string.Equals(App.Engine.SettingsService.Settings.Language, lang, StringComparison.OrdinalIgnoreCase))
            return;

        App.Engine.SettingsService.Settings.Language = lang;
        App.Engine.SettingsService.SaveSettings();

        var action = App.CurrentApp?.RequestLanguageRestart(lang) ?? LanguageRestartAction.None;
        switch (action)
        {
            case LanguageRestartAction.None:
                LangRestartBar.IsOpen = false;
                break;

            case LanguageRestartAction.RestartingNow:
                LanguageCombo.IsEnabled = false;
                LangRestartBar.Severity = InfoBarSeverity.Informational;
                LangRestartBar.Message = LocalizationService.GetString("Settings_LangRestartNotice");
                LangRestartBar.IsOpen = true;
                break;

            case LanguageRestartAction.DeferredUntilSessionEnds:
                LangRestartBar.Severity = InfoBarSeverity.Informational;
                LangRestartBar.Message = LocalizationService.GetString("Settings_LangRestartPendingMsg");
                LangRestartBar.IsOpen = true;
                if (XamlRoot != null)
                {
                    try
                    {
                        var dlg = new ContentDialog
                        {
                            XamlRoot = XamlRoot,
                            RequestedTheme = ActualTheme,
                            Title = LocalizationService.GetString("Settings_LangRestartPendingTitle"),
                            Content = new TextBlock
                            {
                                Text = LocalizationService.GetString("Settings_LangRestartPendingMsg"),
                                FontSize = 13,
                                TextWrapping = TextWrapping.Wrap,
                                MinWidth = 300,
                                MaxWidth = 420
                            },
                            CloseButtonText = LocalizationService.GetString("Assist_OkBtn"),
                            DefaultButton = ContentDialogButton.Close
                        };
                        await dlg.ShowAsync();
                    }
                    catch
                    {
                    }
                }

                break;
        }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ThemeCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int themeVal))
        {
            var pref = (AppThemePreference)themeVal;
            App.Engine.SettingsService.Settings.UiTheme = pref;
            App.Engine.SettingsService.SaveSettings();
            App.MainWindowInstance?.ApplyTheme(pref);
        }
    }

    private void SettingSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = App.Engine.SettingsService.Settings;

        bool yuvChanged = s.EnableYuv444 != EnableYuv444Switch.IsOn;

        s.AutoAdjustBitrate = AutoAdjustBitrateSwitch.IsOn;
        s.EnableVsync = VsyncSwitch.IsOn;
        FramePacingSwitch.IsEnabled = s.EnableVsync;
        s.EnableFramePacing = FramePacingSwitch.IsOn;
        s.EnableHdr = EnableHdrSwitch.IsOn;
        s.EnableYuv444 = EnableYuv444Switch.IsOn;
        s.ShowPerformanceOverlay = PerfOverlaySwitch.IsOn;
        s.ShowConnectionWarnings = ConnWarningsSwitch.IsOn;
        s.ShowConfigurationWarnings = ConfWarningsSwitch.IsOn;

        s.MuteHostSpeakers = MuteHostSpeakersSwitch.IsOn;
        s.MuteOnFocusLoss = MuteOnFocusLossSwitch.IsOn;

        s.AbsoluteMouseMode = AbsMouseSwitch.IsOn;
        s.AbsoluteTouchMode = AbsTouchSwitch.IsOn;
        s.SwapMouseButtons = SwapMouseSwitch.IsOn;
        s.ReverseScrollDirection = ReverseScrollSwitch.IsOn;
        s.MultiController = MultiControllerSwitch.IsOn;
        s.SwapFaceButtons = SwapFaceButtonsSwitch.IsOn;
        s.GamepadMouse = GamepadMouseSwitch.IsOn;
        s.BackgroundGamepad = BackgroundGamepadSwitch.IsOn;
        s.KeepAwake = KeepAwakeSwitch.IsOn;
        bool trayChanged = s.CloseToTray != CloseToTraySwitch.IsOn ||
                           s.HideToTrayOnMinimize != HideToTrayOnMinimizeSwitch.IsOn;
        s.CloseToTray = CloseToTraySwitch.IsOn;
        s.HideToTrayOnMinimize = HideToTrayOnMinimizeSwitch.IsOn;
        s.EnableSharedClipboard = SharedClipboardDefaultSwitch.IsOn;
        s.SharedClipboardSyncImages = SharedClipboardImagesSwitch.IsOn;
        s.EnableFileTransfer = FileTransferDefaultSwitch.IsOn;

        if (yuvChanged)
        {
            MaybeAutoAdjustBitrate();
        }

        App.Engine.SettingsService.SaveSettings();
        if (trayChanged)
        {
            App.MainWindowInstance?.SyncTrayIconVisibility();
        }
    }

    private void UpdateServiceAutoStartRowVisibility()
    {
        if (RowServiceAutoStart == null || ServiceAutoStartDivider == null) return;
        bool visible = LaunchOnStartupSwitch.IsOn || ServiceAutoStartSwitch.IsOn;
        Visibility v = visible ? Visibility.Visible : Visibility.Collapsed;
        RowServiceAutoStart.Visibility = v;
        ServiceAutoStartDivider.Visibility = v;
    }

    private async void LaunchOnStartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool desired = LaunchOnStartupSwitch.IsOn;
        if (!StartupHelper.SetAutoStartEnabled(desired))
        {
            bool prev = _loading;
            _loading = true;
            LaunchOnStartupSwitch.IsOn = StartupHelper.IsAutoStartEnabled();
            UpdateServiceAutoStartRowVisibility();
            _loading = prev;
            return;
        }

        if (!desired && StartupHelper.IsServiceAutoStartEnabled())
        {
            ServiceAutoStartSwitch.IsEnabled = false;
            try
            {
                await StartupHelper.DisableServiceAutoStartAsync();
            }
            finally
            {
                bool prev = _loading;
                _loading = true;
                ServiceAutoStartSwitch.IsOn = StartupHelper.IsServiceAutoStartEnabled();
                _loading = prev;
                ServiceAutoStartSwitch.IsEnabled = !App.Engine.Coordinator.IsHostStreaming;
            }
        }

        UpdateServiceAutoStartRowVisibility();
    }

    private async void ServiceAutoStartSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || App.Engine.Coordinator.IsHostStreaming) return;
        bool desired = ServiceAutoStartSwitch.IsOn;

        if (desired && XamlRoot != null)
        {
            var confirmDlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = LocalizationService.GetString("Set_ServiceAutoStartConfirmTitle"),
                Content = new TextBlock
                {
                    Text = LocalizationService.GetString("Set_ServiceAutoStartConfirmBody"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420
                },
                PrimaryButtonText = LocalizationService.GetString("Set_ServiceAutoStartEnableBtn"),
                CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            if (await confirmDlg.ShowAsync() != ContentDialogResult.Primary)
            {
                bool prev = _loading;
                _loading = true;
                ServiceAutoStartSwitch.IsOn = false;
                UpdateServiceAutoStartRowVisibility();
                _loading = prev;
                return;
            }
        }

        ServiceAutoStartSwitch.IsEnabled = false;
        bool ok;
        try
        {
            if (desired)
            {
                ok = await StartupHelper.EnableServiceAutoStartAsync(
                    App.Engine.AccountService,
                    App.Engine.SettingsService);
            }
            else
            {
                ok = await StartupHelper.DisableServiceAutoStartAsync();
            }
        }
        finally
        {
            bool prev = _loading;
            _loading = true;
            ServiceAutoStartSwitch.IsOn = StartupHelper.IsServiceAutoStartEnabled();
            UpdateServiceAutoStartRowVisibility();
            _loading = prev;
            ServiceAutoStartSwitch.IsEnabled = !App.Engine.Coordinator.IsHostStreaming;
        }

        if (!ok && XamlRoot != null)
        {
            var errDlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = LocalizationService.GetString("Set_ServiceAutoStartFailedTitle"),
                Content = new TextBlock
                {
                    Text = LocalizationService.GetString("Set_ServiceAutoStartFailedBody"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420
                },
                CloseButtonText = LocalizationService.GetString("Assist_OkBtn"),
                DefaultButton = ContentDialogButton.Close
            };
            await errDlg.ShowAsync();
        }
    }

    private async void ClearTransferCacheBtn_Click(object sender, RoutedEventArgs e)
    {
        ClearTransferCacheBtn.IsEnabled = false;
        try
        {
            long freedBytes = await Task.Run(WindowsClipboardHelper.ClearAllStagingCache);
            double freedMb = freedBytes / (1024.0 * 1024.0);
            LangRestartBar.Severity = InfoBarSeverity.Success;
            LangRestartBar.Message = freedBytes > 0
                ? LocalizationService.Format("Set_FileTransferCacheClearedFmt", $"{freedMb:F1}")
                : LocalizationService.GetString("Set_FileTransferCacheEmpty");
            LangRestartBar.IsOpen = true;
        }
        finally
        {
            ClearTransferCacheBtn.IsEnabled = true;
        }
    }

    private void RefreshHostSystemServiceRowStatus()
    {
        if (RowHostSystemService == null) return;
        bool ready = SystemServiceHelper.IsServiceReady();
        RowHostSystemService.Description = ready
            ? LocalizationService.GetString("Host_SystemServiceReady")
            : LocalizationService.GetString("Host_SystemServiceMissing");
        InstallSystemServiceBtn.Content = ready
            ? LocalizationService.GetString("Host_SystemServiceReinstallBtn")
            : LocalizationService.GetString("Host_SystemServiceInstallBtn");
        UninstallSystemServiceBtn.Content = LocalizationService.GetString("Host_SystemServiceUninstallBtn");
        bool locked = App.Engine.Coordinator.IsHostStreaming;
        InstallSystemServiceBtn.IsEnabled = !locked;
        UninstallSystemServiceBtn.IsEnabled = ready && !locked;
    }

    private async Task ExecuteHostSystemServiceOpAsync(bool install)
    {
        InstallSystemServiceBtn.IsEnabled = false;
        UninstallSystemServiceBtn.IsEnabled = false;
        try
        {
            if (install)
                await SystemServiceHelper.InstallAsync();
            else
                await SystemServiceHelper.UninstallAsync();
        }
        finally
        {
            RefreshHostSystemServiceRowStatus();
        }
    }

    private async void InstallSystemServiceBtn_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Coordinator.IsHostStreaming) return;
        await ExecuteHostSystemServiceOpAsync(install: true);
    }

    private async void UninstallSystemServiceBtn_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Coordinator.IsHostStreaming) return;
        if (XamlRoot != null)
        {
            var dlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = LocalizationService.GetString("Host_SystemServiceUninstallTitle"),
                Content = LocalizationService.GetString("Host_SystemServiceUninstallConfirm"),
                PrimaryButtonText = LocalizationService.GetString("Dialog_Ok"),
                CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        await ExecuteHostSystemServiceOpAsync(install: false);
    }

    private void RefreshHostDriverRowStatus()
    {
        try
        {
            Osmium.Core.Interop.NativeMethods.Osmium_GetDriverStatus(
                out int hasUsb, out int _, out int _);
            bool ready = hasUsb != 0;
            RowHostDrivers.Description = ready
                ? LocalizationService.GetString("Host_DriversReady")
                : LocalizationService.GetString("Host_DriversMissing");
            InstallDriversBtn.Content = ready
                ? LocalizationService.GetString("Host_DriversReinstallBtn")
                : LocalizationService.GetString("Host_DriversInstallBtn");
            UninstallDriversBtn.Content = LocalizationService.GetString("Host_DriversUninstallBtn");
            bool locked = App.Engine.Coordinator.IsHostStreaming;
            InstallDriversBtn.IsEnabled = !locked;
            UninstallDriversBtn.IsEnabled = ready && !locked;
        }
        catch
        {
        }
    }

    private async void EnableEncryptionSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = App.Engine.SettingsService.Settings;
        if (App.Engine.Coordinator.IsHostStreaming)
        {
            bool prev = _loading;
            _loading = true;
            EnableEncryptionSwitch.IsOn = s.EnableEncryption;
            _loading = prev;
            return;
        }

        if (EnableEncryptionSwitch.IsOn)
        {
            s.EnableEncryption = true;
            App.Engine.SettingsService.SaveSettings();
            return;
        }

        bool confirmed = false;
        if (XamlRoot != null)
        {
            var warningText = new TextBlock
            {
                Text = LocalizationService.GetString("Set_DisableEncryptionWarnBody"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE5, 0x2E, 0x2E)),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 14
            };
            var dlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = LocalizationService.GetString("Set_DisableEncryptionWarnTitle"),
                Content = warningText,
                PrimaryButtonText = LocalizationService.GetString("Set_DisableEncryptionConfirm"),
                CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            confirmed = await dlg.ShowAsync() == ContentDialogResult.Primary;
        }

        if (confirmed && !App.Engine.Coordinator.IsHostStreaming)
        {
            s.EnableEncryption = false;
            App.Engine.SettingsService.SaveSettings();
        }
        else
        {
            bool prev = _loading;
            _loading = true;
            EnableEncryptionSwitch.IsOn = true;
            _loading = prev;
        }
    }

    private async Task ExecuteHostDriverOpAsync(bool install)
    {
        InstallDriversBtn.IsEnabled = false;
        UninstallDriversBtn.IsEnabled = false;
        try
        {
            Osmium.Core.Interop.NativeMethods.Osmium_GetDriverStatus(
                out _, out int isElevated, out _);

            if (isElevated != 0)
            {
                await Task.Run(() => install
                    ? Osmium.Core.Interop.NativeMethods.Osmium_InstallVirtualDrivers()
                    : Osmium.Core.Interop.NativeMethods.Osmium_UninstallVirtualDrivers());
            }
            else if (!string.IsNullOrEmpty(Environment.ProcessPath))
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    Arguments = install ? "--install-drivers" : "--uninstall-drivers",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                }
            }
        }
        catch
        {
        }
        finally
        {
            RefreshHostDriverRowStatus();
            InstallDriversBtn.IsEnabled = !App.Engine.Coordinator.IsHostStreaming;
        }
    }

    private async void InstallDriversBtn_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Coordinator.IsHostStreaming) return;
        await ExecuteHostDriverOpAsync(install: true);
    }

    private async void UninstallDriversBtn_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Coordinator.IsHostStreaming) return;
        if (XamlRoot != null)
        {
            var dlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = LocalizationService.GetString("Host_DriversUninstallTitle"),
                Content = LocalizationService.GetString("Host_DriversUninstallConfirm"),
                PrimaryButtonText = LocalizationService.GetString("Dialog_Ok"),
                CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        await ExecuteHostDriverOpAsync(install: false);
    }

    private void BtnRefreshRemotePowerStatus_Click(object sender, RoutedEventArgs e)
        => RefreshHostRemotePowerRowStatus();

    private void RefreshHostRemotePowerRowStatus()
    {
        if (RowHostAllowRemotePower == null) return;
        _ = Task.Run(() => (Profile: App.Engine.GetLocalPowerProfile(), HasBackup: WakeOnLanService.HasWolBackup()))
            .ContinueWith(t =>
            {
                if (t.IsFaulted) return;
                var (profile, hasBackup) = t.Result;
                if (profile == null) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (RowHostAllowRemotePower == null) return;
                    bool locked = App.Engine.Coordinator.IsHostStreaming;
                    bool powerProfileBlocked = App.Engine.AccountService.IsLoggedIn &&
                                               !App.Engine.AccountService.IsProfileComplete;
                    HostAllowRemotePowerSwitch.IsEnabled = !locked && !powerProfileBlocked;
                    ConfigureWolBtn.IsEnabled = !locked && !powerProfileBlocked;
                    RestoreWolBtn.IsEnabled = !locked && (hasBackup || profile.WolReady);
                    if (BtnRefreshRemotePowerStatus != null)
                    {
                        BtnRefreshRemotePowerStatus.IsEnabled = !locked;
                    }

                    if (RemotePowerProfileWarningBar != null)
                    {
                        RemotePowerProfileWarningBar.Title = LocalizationService.GetString("Acc_ProfileIncompleteTitle");
                        RemotePowerProfileWarningBar.Message = LocalizationService.GetString("Power_ProfileWarningDesc");
                        RemotePowerProfileWarningBar.IsOpen = powerProfileBlocked;
                        RemotePowerProfileWarningBar.Visibility = powerProfileBlocked ? Visibility.Visible : Visibility.Collapsed;
                    }

                    bool hasMac = !string.IsNullOrEmpty(profile.PrimaryMac);
                    if (RemotePowerMacText != null)
                    {
                        RemotePowerMacText.Text = hasMac ? profile.PrimaryMac : "-";
                    }

                    if (RowRemotePowerNicInfo != null)
                    {
                        RowRemotePowerNicInfo.Description = !string.IsNullOrWhiteSpace(profile.AdapterSummary)
                            ? profile.AdapterSummary
                            : LocalizationService.GetString("Host_WolNoNicFound");
                    }

                    if (RowHostWolConfig != null)
                    {
                        if (!hasMac)
                        {
                            RowHostWolConfig.Description = LocalizationService.GetString("Host_WolNoNicFound");
                        }
                        else
                        {
                            string stateText = LocalizationService.GetString(profile.WolReady
                                ? "Host_WolStatusReady"
                                : "Host_WolStatusNeedsConfig");
                            RowHostWolConfig.Description = $"{stateText} · MAC: {profile.PrimaryMac}";
                        }
                    }
                });
            });
    }

    private async Task<bool> ExecuteRestoreWolElevatedAsync(bool allowDefaultFallback)
    {
        try
        {
            if (SystemServiceHelper.IsCurrentProcessElevated())
            {
                var res = await Task.Run(() => WakeOnLanService.RestoreLocalWindowsWol(allowDefaultFallback));
                return res.Success;
            }

            if (!string.IsNullOrEmpty(Environment.ProcessPath))
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    Arguments = allowDefaultFallback ? "--restore-wol --force-default" : "--restore-wol",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                    return proc.ExitCode == 0;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private async void HostAllowRemotePowerSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || App.Engine.Coordinator.IsHostStreaming) return;
        if (App.Engine.AccountService.IsLoggedIn && !App.Engine.AccountService.IsProfileComplete)
        {
            bool prev = _loading;
            _loading = true;
            HostAllowRemotePowerSwitch.IsOn = App.Engine.SettingsService.Settings.HostAllowRemotePower;
            _loading = prev;
            return;
        }

        var s = App.Engine.SettingsService.Settings;
        s.HostAllowRemotePower = HostAllowRemotePowerSwitch.IsOn;
        App.Engine.SettingsService.SaveSettings();
        await App.Engine.SyncSignalingStatusAsync();
        RefreshHostRemotePowerRowStatus();

        if (!s.HostAllowRemotePower && WakeOnLanService.HasWolBackup() && XamlRoot != null)
        {
            var promptDlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = LocalizationService.GetString("Host_RestoreWolTitle"),
                Content = new TextBlock
                {
                    Text = LocalizationService.GetString("Host_DisablePowerRestorePrompt"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420
                },
                PrimaryButtonText = LocalizationService.GetString("Host_RestoreWolBtn"),
                CloseButtonText = LocalizationService.GetString("Host_DisablePowerKeepWolBtn"),
                DefaultButton = ContentDialogButton.Primary
            };
            if (await promptDlg.ShowAsync() == ContentDialogResult.Primary)
            {
                ConfigureWolBtn.IsEnabled = false;
                RestoreWolBtn.IsEnabled = false;
                bool ok = false;
                try
                {
                    ok = await ExecuteRestoreWolElevatedAsync(allowDefaultFallback: false);
                }
                finally
                {
                    RefreshHostRemotePowerRowStatus();
                    await App.Engine.SyncSignalingStatusAsync();
                }

                if (XamlRoot != null)
                {
                    var resultDlg = new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        RequestedTheme = ActualTheme,
                        Title = LocalizationService.GetString("Host_RestoreWolTitle"),
                        Content = new TextBlock
                        {
                            Text = LocalizationService.GetString(ok ? "Host_RestoreWolSuccess" : "Host_RestoreWolFailed"),
                            FontSize = 13,
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 420
                        },
                        CloseButtonText = LocalizationService.GetString("Assist_OkBtn"),
                        DefaultButton = ContentDialogButton.Close
                    };
                    await resultDlg.ShowAsync();
                }
            }
        }
    }

    private async void RestoreWolBtn_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Coordinator.IsHostStreaming) return;
        if (XamlRoot != null)
        {
            var confirmDlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = LocalizationService.GetString("Host_RestoreWolTitle"),
                Content = new TextBlock
                {
                    Text = LocalizationService.GetString("Host_RestoreWolConfirm"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420
                },
                PrimaryButtonText = LocalizationService.GetString("Host_RestoreWolBtn"),
                CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirmDlg.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        ConfigureWolBtn.IsEnabled = false;
        RestoreWolBtn.IsEnabled = false;
        HostAllowRemotePowerSwitch.IsEnabled = false;
        bool ok = false;
        try
        {
            ok = await ExecuteRestoreWolElevatedAsync(allowDefaultFallback: true);
            if (ok)
            {
                var s = App.Engine.SettingsService.Settings;
                s.HostAllowRemotePower = false;
                bool prev = _loading;
                _loading = true;
                HostAllowRemotePowerSwitch.IsOn = false;
                _loading = prev;
                App.Engine.SettingsService.SaveSettings();
            }
        }
        finally
        {
            bool locked = App.Engine.Coordinator.IsHostStreaming;
            bool powerProfileBlocked = App.Engine.AccountService.IsLoggedIn &&
                                       !App.Engine.AccountService.IsProfileComplete;
            HostAllowRemotePowerSwitch.IsEnabled = !locked && !powerProfileBlocked;
            ConfigureWolBtn.IsEnabled = !locked && !powerProfileBlocked;
            RefreshHostRemotePowerRowStatus();
            await App.Engine.SyncSignalingStatusAsync();
        }

        if (XamlRoot != null)
        {
            var resultDlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = LocalizationService.GetString("Host_RestoreWolTitle"),
                Content = new TextBlock
                {
                    Text = LocalizationService.GetString(ok ? "Host_RestoreWolSuccess" : "Host_RestoreWolFailed"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420
                },
                CloseButtonText = LocalizationService.GetString("Assist_OkBtn"),
                DefaultButton = ContentDialogButton.Close
            };
            await resultDlg.ShowAsync();
        }
    }

    private async void ConfigureWolBtn_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.Coordinator.IsHostStreaming) return;
        if (App.Engine.AccountService.IsLoggedIn && !App.Engine.AccountService.IsProfileComplete) return;
        if (XamlRoot != null)
        {
            var confirmDlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = LocalizationService.GetString("Host_ConfigureWolTitle"),
                Content = new TextBlock
                {
                    Text = LocalizationService.GetString("Host_ConfigureWolConfirm"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420
                },
                PrimaryButtonText = LocalizationService.GetString("Host_ConfigureWolBtn"),
                CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            if (await confirmDlg.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        ConfigureWolBtn.IsEnabled = false;
        RestoreWolBtn.IsEnabled = false;
        bool ok = false;
        try
        {
            if (SystemServiceHelper.IsCurrentProcessElevated())
            {
                var res = await Task.Run(WakeOnLanService.ConfigureLocalWindowsWol);
                ok = res.Success;
            }
            else if (!string.IsNullOrEmpty(Environment.ProcessPath))
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    Arguments = "--configure-wol",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                    ok = proc.ExitCode == 0;
                }
            }
        }
        catch
        {
            ok = false;
        }
        finally
        {
            bool powerProfileBlocked = App.Engine.AccountService.IsLoggedIn &&
                                       !App.Engine.AccountService.IsProfileComplete;
            ConfigureWolBtn.IsEnabled = !App.Engine.Coordinator.IsHostStreaming && !powerProfileBlocked;
            RefreshHostRemotePowerRowStatus();
            await App.Engine.SyncSignalingStatusAsync();
        }

        if (XamlRoot != null)
        {
            var resultDlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = LocalizationService.GetString("Host_ConfigureWolTitle"),
                Content = new TextBlock
                {
                    Text = LocalizationService.GetString(ok ? "Host_ConfigureWolSuccess" : "Host_ConfigureWolFailed"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420
                },
                CloseButtonText = LocalizationService.GetString("Assist_OkBtn"),
                DefaultButton = ContentDialogButton.Close
            };
            await resultDlg.ShowAsync();
        }
    }

    private void ShowSidebarDevicesSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Engine.SettingsService.Settings.ShowSameAccountDevicesInSidebar = ShowSidebarDevicesSwitch.IsOn;
        App.Engine.SettingsService.SaveSettings();
        App.MainWindowInstance?.SyncSameAccountPeerNavItems();
        RenderDeviceManagementPanel();
    }

    private void BtnDevicesGoLogin_Click(object sender, RoutedEventArgs e)
        => App.MainWindowInstance?.NavigateToAccount();

    private async void BtnRefreshSettingsDevices_Click(object sender, RoutedEventArgs e)
        => await RefreshSettingsDevicesAsync();

    private async Task RefreshSettingsDevicesAsync()
    {
        if (!App.Engine.AccountService.IsLoggedIn)
        {
            RenderDeviceManagementPanel();
            return;
        }

        BtnRefreshSettingsDevices.IsEnabled = false;
        try
        {
            var res = await App.Engine.AccountService.RefreshDevicesAsync();
            if (!res.Ok)
            {
                DevicesStatusBar.Severity = InfoBarSeverity.Error;
                DevicesStatusBar.Message = App.Engine.AccountService.FormatFriendlyError(res.Code, res.HttpStatusCode);
                DevicesStatusBar.IsOpen = true;
            }

            RenderDeviceManagementPanel();
        }
        finally
        {
            BtnRefreshSettingsDevices.IsEnabled = App.Engine.AccountService.IsLoggedIn;
        }
    }

    private void RenderDeviceManagementPanel()
    {
        if (SettingsDevicesListPanel == null) return;

        var acc = App.Engine.AccountService;
        var settings = App.Engine.SettingsService.Settings;
        bool isLoggedIn = acc.IsLoggedIn;

        RowSettingsDevicesHeader.Visibility = isLoggedIn ? Visibility.Visible : Visibility.Collapsed;
        RowDevicesLoginPrompt.Visibility = isLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        SettingsDevicesListPanel.Children.Clear();

        if (!isLoggedIn)
        {
            SettingsDevicesCountText.Text = string.Empty;
            return;
        }

        var devices = acc.SameAccountDevices;
        SettingsDevicesCountText.Text = $"{devices.Count} / 1000";
        BtnRefreshSettingsDevices.IsEnabled = true;

        if (devices.Count == 0)
        {
            SettingsDevicesListPanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.GetString("Set_DevicesEmpty"),
                FontSize = 13,
                Margin = new Thickness(4, 6, 0, 0),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            return;
        }

        string localId = DeviceIdentity.NormalizeDeviceId(App.Engine.SettingsService.Identity.DeviceId);
        var hiddenIds = settings.HiddenSidebarDeviceIds ?? new HashSet<string>(StringComparer.Ordinal);
        bool hostControlled = App.Engine.Coordinator.IsHostStreaming;
        bool globalSidebarEnabled = settings.ShowSameAccountDevicesInSidebar;
        var compactToggleStyle = Application.Current.Resources["OsmiumCompactToggleStyle"] as Style;
        var secondaryBrush = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush;
        var criticalBrush = Application.Current.Resources["SystemFillColorCriticalBrush"] as Brush;
        bool profileComplete = acc.IsProfileComplete;

        foreach (var dev in devices)
        {
            string normId = DeviceIdentity.NormalizeDeviceId(dev.DeviceId);
            bool isCurrentDevice = string.Equals(normId, localId, StringComparison.Ordinal);
            string displayName = string.IsNullOrWhiteSpace(dev.DeviceName)
                ? dev.FormattedDeviceId
                : dev.DeviceName;

            string title = isCurrentDevice
                ? $"{displayName} - {dev.FormattedDeviceId} - {LocalizationService.GetString("Set_DeviceThisTag")}"
                : $"{displayName} - {dev.FormattedDeviceId}";

            string statusText = dev.IsBeingControlled
                ? LocalizationService.GetString("Status_Streaming")
                : LocalizationService.GetString(dev.IsOnline ? "Status_Online" : "Status_Offline");
            string ipText = string.IsNullOrWhiteSpace(dev.LastIp) ? "-" : dev.LastIp;

            var card = new Controls.SettingsRowCard
            {
                Title = title,
                Description = $"{statusText} - {dev.OsVersion} - {ipText}",
                Glyph = "\uF35A"
            };

            var rightStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (!isCurrentDevice)
            {
                if (dev.IsComputerDevice && dev.AllowRemotePower)
                {
                    var capturedPowerDev = dev;
                    if (!dev.IsOnline)
                    {
                        bool hasMacs = dev.WolMacs.Count > 0;
                        var wakeBtn = new Button
                        {
                            Content = LocalizationService.GetString("Power_ActionWake"),
                            CornerRadius = new CornerRadius(6),
                            IsEnabled = !hostControlled && profileComplete && hasMacs
                        };
                        if (!profileComplete)
                        {
                            ToolTipService.SetToolTip(wakeBtn, LocalizationService.GetString("Power_ProfileIncompleteHint"));
                        }
                        else if (!hasMacs)
                        {
                            ToolTipService.SetToolTip(wakeBtn, LocalizationService.GetString("Power_TargetNoMacHint"));
                        }

                        wakeBtn.Click += async (_, _) =>
                        {
                            if (App.Engine.Coordinator.IsHostStreaming || XamlRoot == null) return;
                            wakeBtn.IsEnabled = false;
                            try
                            {
                                await RemotePowerDialogHelper.ExecuteAsync(
                                    XamlRoot, ActualTheme, capturedPowerDev, "wake", requireConfirm: false);
                            }
                            finally
                            {
                                RenderDeviceManagementPanel();
                            }
                        };
                        rightStack.Children.Add(wakeBtn);
                    }
                    else
                    {
                        var restartBtn = new Button
                        {
                            Content = LocalizationService.GetString("Power_ActionRestart"),
                            CornerRadius = new CornerRadius(6),
                            IsEnabled = !hostControlled && profileComplete
                        };
                        var shutdownBtn = new Button
                        {
                            Content = LocalizationService.GetString("Power_ActionShutdown"),
                            CornerRadius = new CornerRadius(6),
                            Foreground = criticalBrush,
                            IsEnabled = !hostControlled && profileComplete
                        };
                        if (!profileComplete)
                        {
                            string tip = LocalizationService.GetString("Power_ProfileIncompleteHint");
                            ToolTipService.SetToolTip(restartBtn, tip);
                            ToolTipService.SetToolTip(shutdownBtn, tip);
                        }

                        restartBtn.Click += async (_, _) =>
                        {
                            if (App.Engine.Coordinator.IsHostStreaming || XamlRoot == null) return;
                            restartBtn.IsEnabled = false;
                            shutdownBtn.IsEnabled = false;
                            try
                            {
                                await RemotePowerDialogHelper.ExecuteAsync(
                                    XamlRoot, ActualTheme, capturedPowerDev, "restart", requireConfirm: true);
                            }
                            finally
                            {
                                RenderDeviceManagementPanel();
                            }
                        };
                        shutdownBtn.Click += async (_, _) =>
                        {
                            if (App.Engine.Coordinator.IsHostStreaming || XamlRoot == null) return;
                            restartBtn.IsEnabled = false;
                            shutdownBtn.IsEnabled = false;
                            try
                            {
                                await RemotePowerDialogHelper.ExecuteAsync(
                                    XamlRoot, ActualTheme, capturedPowerDev, "shutdown", requireConfirm: true);
                            }
                            finally
                            {
                                RenderDeviceManagementPanel();
                            }
                        };
                        rightStack.Children.Add(restartBtn);
                        rightStack.Children.Add(shutdownBtn);
                    }
                }

                var sidebarToggleGroup = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center
                };
                sidebarToggleGroup.Children.Add(new TextBlock
                {
                    Text = LocalizationService.GetString("Set_DeviceSidebarToggleLabel"),
                    FontSize = 12,
                    Foreground = secondaryBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var sidebarSwitch = new ToggleSwitch
                {
                    Style = compactToggleStyle,
                    IsOn = !hiddenIds.Contains(normId),
                    IsEnabled = globalSidebarEnabled
                };
                string capturedNormId = normId;
                sidebarSwitch.Toggled += (_, _) =>
                {
                    var s = App.Engine.SettingsService.Settings;
                    s.HiddenSidebarDeviceIds ??= new HashSet<string>(StringComparer.Ordinal);
                    if (sidebarSwitch.IsOn)
                    {
                        s.HiddenSidebarDeviceIds.Remove(capturedNormId);
                    }
                    else
                    {
                        s.HiddenSidebarDeviceIds.Add(capturedNormId);
                    }

                    App.Engine.SettingsService.SaveSettings();
                    App.MainWindowInstance?.SyncSameAccountPeerNavItems();
                };
                sidebarToggleGroup.Children.Add(sidebarSwitch);
                rightStack.Children.Add(sidebarToggleGroup);
            }

            var renameBtn = new Button
            {
                Content = LocalizationService.GetString("Set_DeviceRenameBtn"),
                CornerRadius = new CornerRadius(6),
                IsEnabled = !hostControlled
            };
            var capturedDevForRename = dev;
            renameBtn.Click += async (_, _) =>
            {
                if (App.Engine.Coordinator.IsHostStreaming || XamlRoot == null) return;
                var (updated, cleared) =
                    await DeviceRenameDialogHelper.ShowAsync(XamlRoot, ActualTheme, capturedDevForRename);
                if (updated)
                {
                    App.MainWindowInstance?.SyncSameAccountPeerNavItems();
                    DevicesStatusBar.Severity = InfoBarSeverity.Success;
                    DevicesStatusBar.Message = LocalizationService.GetString(
                        cleared ? "Set_DeviceRenameCleared" : "Set_DeviceRenameSuccess");
                    DevicesStatusBar.IsOpen = true;
                    RenderDeviceManagementPanel();
                }
            };
            rightStack.Children.Add(renameBtn);

            var signOutBtn = new Button
            {
                Content = LocalizationService.GetString("Set_DeviceSignOutBtn"),
                CornerRadius = new CornerRadius(6),
                IsEnabled = !hostControlled
            };
            string targetId = dev.DeviceId;
            string capturedLabel = $"{displayName} - {dev.FormattedDeviceId}";
            bool capturedIsSelf = isCurrentDevice;
            string capturedNormForSignOut = normId;
            signOutBtn.Click += async (_, _) =>
            {
                if (App.Engine.Coordinator.IsHostStreaming) return;
                if (XamlRoot != null)
                {
                    string body = LocalizationService.Format(
                        capturedIsSelf ? "Set_DeviceSignOutSelfBodyFmt" : "Set_DeviceSignOutBodyFmt",
                        capturedLabel);
                    var confirmDlg = new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        RequestedTheme = ActualTheme,
                        Title = LocalizationService.GetString("Set_DeviceSignOutTitle"),
                        Content = new TextBlock
                        {
                            Text = body,
                            FontSize = 13,
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 380
                        },
                        PrimaryButtonText = LocalizationService.GetString("Set_DeviceSignOutBtn"),
                        CloseButtonText = LocalizationService.GetString("Dialog_Cancel"),
                        DefaultButton = ContentDialogButton.Close
                    };
                    if (await confirmDlg.ShowAsync() != ContentDialogResult.Primary)
                    {
                        return;
                    }
                }

                signOutBtn.IsEnabled = false;
                var res = await App.Engine.AccountService.RemoveDeviceAsync(targetId);
                if (res.Ok)
                {
                    var s = App.Engine.SettingsService.Settings;
                    if (s.HiddenSidebarDeviceIds != null && s.HiddenSidebarDeviceIds.Remove(capturedNormForSignOut))
                    {
                        App.Engine.SettingsService.SaveSettings();
                    }

                    App.MainWindowInstance?.SyncSameAccountPeerNavItems();
                    DevicesStatusBar.Severity = InfoBarSeverity.Success;
                    DevicesStatusBar.Message = LocalizationService.GetString("Set_DeviceSignOutSuccess");
                    DevicesStatusBar.IsOpen = true;
                    RenderDeviceManagementPanel();
                }
                else
                {
                    DevicesStatusBar.Severity = InfoBarSeverity.Error;
                    DevicesStatusBar.Message =
                        App.Engine.AccountService.FormatFriendlyError(res.Code, res.HttpStatusCode);
                    DevicesStatusBar.IsOpen = true;
                    signOutBtn.IsEnabled = !App.Engine.Coordinator.IsHostStreaming;
                }
            };
            rightStack.Children.Add(signOutBtn);

            card.ActionContent = rightStack;
            SettingsDevicesListPanel.Children.Add(card);
        }
    }
}