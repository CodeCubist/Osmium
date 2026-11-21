using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Osmium.App.Helpers;
using Osmium.App.Services;
using Osmium.App.Views;
using Osmium.Core.Models;
using Osmium.Core.Network;

namespace Osmium.App;

public sealed partial class MainWindow : Window
{
    private void MainWindow_AppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceExit || !App.Engine.SettingsService.Settings.CloseToTray) return;
        args.Cancel = true;
        HideToTray();
    }

    private void MainWindow_AppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_forceExit || _isHiddenInTray || !App.Engine.SettingsService.Settings.HideToTrayOnMinimize) return;
        if (sender.Presenter is OverlappedPresenter op && op.State == OverlappedPresenterState.Minimized)
        {
            HideToTray();
        }
    }

    private Task<bool> ConfirmIncomingConnectionOnUiAsync(IncomingConnectEventArgs args)
    {
        var tcs = new TaskCompletionSource<bool>();
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_isHiddenInTray) RestoreFromTray();
                for (int i = 0; i < 15 && Content.XamlRoot == null; i++) await Task.Delay(30);

                string body = LocalizationService.Format(
                    "Dialog_IncomingBody",
                    args.ClientName,
                    DeviceIdentity.FormatDeviceId(args.ClientDeviceId),
                    args.ObservedClientIp);

                var dlg = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    RequestedTheme = RootShellGrid.RequestedTheme,
                    Title = LocalizationService.GetString("Dialog_IncomingTitle"),
                    Content = body,
                    PrimaryButtonText = LocalizationService.GetString("Dialog_Allow"),
                    CloseButtonText = LocalizationService.GetString("Dialog_Deny"),
                    DefaultButton = ContentDialogButton.Primary
                };
                var res = await dlg.ShowAsync();
                tcs.TrySetResult(res == ContentDialogResult.Primary);
            }
            catch
            {
                tcs.TrySetResult(false);
            }
        });
        return tcs.Task;
    }

    private Task<bool> ConfirmDriverActionOnUiAsync(bool isInstall, string peerName)
    {
        var tcs = new TaskCompletionSource<bool>();
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_isHiddenInTray) RestoreFromTray();
                for (int i = 0; i < 15 && Content.XamlRoot == null; i++) await Task.Delay(30);

                string actionWord =
                    LocalizationService.GetString(isInstall
                        ? "Dialog_DriverActionInstall"
                        : "Dialog_DriverActionUninstall");
                string body = LocalizationService.Format(
                    "Dialog_DriverConfirmBodyFmt",
                    string.IsNullOrWhiteSpace(peerName)
                        ? LocalizationService.GetString("Dialog_DefaultPeerName")
                        : peerName,
                    actionWord);

                var dlg = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    RequestedTheme = RootShellGrid.RequestedTheme,
                    Title = LocalizationService.GetString("Dialog_DriverConfirmTitle"),
                    Content = body,
                    PrimaryButtonText = LocalizationService.GetString("Dialog_Allow"),
                    CloseButtonText = LocalizationService.GetString("Dialog_Deny"),
                    DefaultButton = ContentDialogButton.Close
                };
                var res = await dlg.ShowAsync();
                tcs.TrySetResult(res == ContentDialogResult.Primary);
            }
            catch
            {
                tcs.TrySetResult(false);
            }
        });
        return tcs.Task;
    }

    private void ContentViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            ContentViewport.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            };
        }
    }

    private static int GetNavPageIndex(string tag) => tag switch
    {
        "DeviceOverview" => 0,
        var t when t.StartsWith("PeerDevice:", StringComparison.Ordinal) => 1,
        "RemoteConnect" => 2,
        "GamepadMapper" => 3,
        "Account" => 4,
        "Settings" => 5,
        _ => -1
    };

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag) return;

        Type? pageType = tag switch
        {
            "DeviceOverview" => typeof(RemoteHomePage),
            var t when t.StartsWith("PeerDevice:", StringComparison.Ordinal) => typeof(PeerDevicePage),
            "RemoteConnect" => typeof(RemoteConnectPage),
            "GamepadMapper" => typeof(GamepadMapperPage),
            "Account" => typeof(AccountPage),
            "Settings" => typeof(SettingsPage),
            _ => null
        };
        if (pageType == null) return;

        object? parameter = null;
        if (tag == "Settings")
        {
            parameter = _pendingSettingsTab;
            _pendingSettingsTab = null;
        }
        else if (tag == "Account")
        {
            parameter = _pendingAccountTab;
            _pendingAccountTab = null;
        }
        else if (tag.StartsWith("PeerDevice:", StringComparison.Ordinal))
        {
            parameter = tag["PeerDevice:".Length..];
        }

        int targetIndex = GetNavPageIndex(tag);
        int previousIndex = _currentNavIndex;
        _currentNavIndex = targetIndex;

        if (previousIndex < 0 || targetIndex == previousIndex)
        {
            ContentFrame.Opacity = 1.0;
            ContentFrameTranslate.Y = 0;
            ContentFrame.Navigate(pageType, parameter, new SuppressNavigationTransitionInfo());
            return;
        }

        _navTransitionStoryboard?.Stop();

        int direction = targetIndex > previousIndex ? 1 : -1;
        double height = ContentViewport?.ActualHeight ?? 0;
        double slideOffset = Math.Min(96.0, Math.Max(height * 0.14, 56.0)) * direction;

        ContentFrame.Opacity = 0;
        ContentFrameTranslate.Y = slideOffset;
        ContentFrame.Navigate(pageType, parameter, new SuppressNavigationTransitionInfo());

        var storyboard = new Storyboard();
        var slideDuration = new Duration(TimeSpan.FromMilliseconds(280));
        var fadeDuration = new Duration(TimeSpan.FromMilliseconds(200));
        var slideEasing = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
        var fadeEasing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var slideAnim = new DoubleAnimation
        {
            To = 0,
            Duration = slideDuration,
            EasingFunction = slideEasing,
            EnableDependentAnimation = false
        };
        Storyboard.SetTarget(slideAnim, ContentFrameTranslate);
        Storyboard.SetTargetProperty(slideAnim, "Y");

        var fadeAnim = new DoubleAnimation
        {
            To = 1.0,
            Duration = fadeDuration,
            EasingFunction = fadeEasing
        };
        Storyboard.SetTarget(fadeAnim, ContentFrame);
        Storyboard.SetTargetProperty(fadeAnim, "Opacity");

        storyboard.Children.Add(slideAnim);
        storyboard.Children.Add(fadeAnim);

        _navTransitionStoryboard = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_navTransitionStoryboard, storyboard)) return;
            ContentFrameTranslate.Y = 0;
            ContentFrame.Opacity = 1.0;
            _navTransitionStoryboard = null;
        };
        storyboard.Begin();
    }

    public void NavigateToLocalHome()
    {
        if (!Equals(NavView.SelectedItem, NavOverviewItem))
        {
            NavView.SelectedItem = NavOverviewItem;
        }
        else if (ContentFrame.Content is not RemoteHomePage)
        {
            ContentFrame.Navigate(typeof(RemoteHomePage), null, new SuppressNavigationTransitionInfo());
        }
    }

    public void NavigateToHostControl()
    {
        if (App.Engine.Coordinator.IsHostStreaming) return;
        NavigateToSettings("CaptureSecurity");
    }

    public void NavigateToRemoteConnect()
    {
        NavView.SelectedItem = NavRemoteConnectItem;
    }

    public void NavigateToGamepadMapper()
    {
        NavView.SelectedItem = NavGamepadItem;
    }

    public void NavigateToAccount(string? initialTab = null)
    {
        _pendingAccountTab = initialTab;
        if (!Equals(NavView.SelectedItem, NavAccountItem))
        {
            NavView.SelectedItem = NavAccountItem;
        }
        else if (!string.IsNullOrEmpty(initialTab) && ContentFrame.Content is AccountPage ap)
        {
            _pendingAccountTab = null;
            ap.SelectTab(initialTab);
        }
    }

    public void NavigateToSettings(string? initialTab = null)
    {
        if (App.Engine.Coordinator.IsHostStreaming &&
            string.Equals(initialTab, "CaptureSecurity", StringComparison.OrdinalIgnoreCase))
        {
            initialTab = "General";
        }

        _pendingSettingsTab = initialTab;
        if (!Equals(NavView.SelectedItem, NavSettingsItem))
        {
            NavView.SelectedItem = NavSettingsItem;
        }
        else if (!string.IsNullOrEmpty(initialTab) && ContentFrame.Content is SettingsPage sp)
        {
            _pendingSettingsTab = null;
            sp.SelectTab(initialTab);
        }
    }

    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy,
        uint uFlags);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}