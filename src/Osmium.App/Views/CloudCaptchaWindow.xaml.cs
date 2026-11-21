using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Osmium.App.Helpers;
using Osmium.App.Services;
using Osmium.Core.Models;
using Osmium.Core.Services;
using Windows.Graphics;
using Windows.UI;

namespace Osmium.App.Views;

public sealed partial class CloudCaptchaWindow : Window
{
    private const string ResultPathHint = "/api/auth/result";

    private readonly Uri _captchaUri;
    private readonly SemaphoreSlim _pollSemaphore = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<Task<bool>> _tryConsumePollAsync;
    private bool _finished;

    private CloudCaptchaWindow(Uri captchaUri, Func<Task<bool>> tryConsumePollAsync)
    {
        _captchaUri = captchaUri;
        _tryConsumePollAsync = tryConsumePollAsync;

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyLocalization();
        ConfigureWindow();
        ApplyTheme();

        Closed += (_, _) =>
        {
            _finished = true;
            _cts.Cancel();
        };

        _ = InitializeAsync();
    }

    private void ApplyLocalization()
    {
        string title = LocalizationService.GetString("Acc_CaptchaWindowTitle");
        Title = title;
        WindowHeaderText.Text = title;
        StatusTextBlock.Text = LocalizationService.GetString("Acc_CaptchaHint");
        OpenExternalBtn.Content = LocalizationService.GetString("Acc_CaptchaOpenBrowser");
    }

    public static Task<AccountApiResult<T>?> ShowChallengeAsync<T>(
        AccountService accountService,
        AccountChallengeInfo challenge)
    {
        Uri captchaUri = accountService.ResolveServiceUri(challenge.CaptchaUrl);
        return ShowExternalChallengeAsync<AccountApiResult<T>>(
            captchaUri,
            async () =>
            {
                var result = await accountService.PollChallengeAsync<T>(challenge.PollUrl, challenge.ChallengeSecret);
                return result.IsPending ? (false, null) : (true, result);
            });
    }

    public static async Task<TResult?> ShowExternalChallengeAsync<TResult>(
        Uri captchaUri,
        Func<Task<(bool Done, TResult? Result)>> pollOnceAsync)
    {
        var tcs = new TaskCompletionSource<TResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CloudCaptchaWindow? window = null;

        async Task<bool> PollOnceWrapperAsync()
        {
            var (done, result) = await pollOnceAsync();
            if (!done) return false;

            tcs.TrySetResult(result);
            if (window != null)
            {
                if (window.DispatcherQueue.HasThreadAccess)
                {
                    try
                    {
                        window.Close();
                    }
                    catch
                    {
                    }
                }
                else
                {
                    window.DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            window.Close();
                        }
                        catch
                        {
                        }
                    });
                }
            }

            return true;
        }

        window = new CloudCaptchaWindow(captchaUri, PollOnceWrapperAsync);
        window.Closed += (_, _) => tcs.TrySetResult(default);
        window.Activate();

        return await tcs.Task;
    }

    private void ConfigureWindow()
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow == null) return;

            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "osmium.ico");
            if (File.Exists(iconPath))
            {
                appWindow.SetIcon(iconPath);
            }

            WindowManagerHelper.ResizeWithDpi(appWindow, this, 480, 560);
            WindowManagerHelper.CenterWindowOnScreen(appWindow, 480, 560);
            try
            {
                SystemBackdrop = new MicaBackdrop();
            }
            catch
            {
            }
        }
        catch
        {
        }
    }

    private void ApplyTheme()
    {
        try
        {
            var pref = App.Engine.SettingsService.Settings.UiTheme;
            RootGrid.RequestedTheme = pref switch
            {
                AppThemePreference.Light => ElementTheme.Light,
                AppThemePreference.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default
            };

            bool isDark = pref == AppThemePreference.Dark ||
                          (pref == AppThemePreference.System &&
                           Application.Current.RequestedTheme == ApplicationTheme.Dark);
            ImmersiveWindowHelper.ApplyImmersiveFrame(this, isDark);
        }
        catch
        {
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            await CaptchaWebView.EnsureCoreWebView2Async();
            CaptchaWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            CaptchaWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            CaptchaWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            CaptchaWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            CaptchaWebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            CaptchaWebView.CoreWebView2.NavigationCompleted += (_, _) => LoadingRing.IsActive = false;
            CaptchaWebView.Source = _captchaUri;
        }
        catch
        {
            LoadingRing.IsActive = false;
            StatusTextBlock.Text = LocalizationService.GetString("Acc_CaptchaLoadError");
        }

        _ = RunPollLoopAsync(_cts.Token);
    }

    private bool IsAllowedNavigationUri(string rawUri)
    {
        if (string.Equals(rawUri, "about:blank", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, _captchaUri.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(uri.Host, _captchaUri.Host, StringComparison.OrdinalIgnoreCase) &&
            uri.Port == _captchaUri.Port)
        {
            return true;
        }

        string host = uri.Host.ToLowerInvariant();
        return host == "geetest.com" || host.EndsWith(".geetest.com", StringComparison.Ordinal) ||
               host == "geevisit.com" || host.EndsWith(".geevisit.com", StringComparison.Ordinal) ||
               host == "gsensebot.com" || host.EndsWith(".gsensebot.com", StringComparison.Ordinal);
    }

    private async void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsAllowedNavigationUri(args.Uri))
        {
            args.Cancel = true;
            return;
        }

        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var targetUri) &&
            string.Equals(targetUri.Host, _captchaUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            if (targetUri.AbsolutePath.EndsWith("/submit", StringComparison.OrdinalIgnoreCase))
            {
                StatusTextBlock.Text = LocalizationService.GetString("Acc_CaptchaSubmitting");
                LoadingRing.IsActive = true;
                return;
            }

            if (targetUri.AbsolutePath.Contains(ResultPathHint, StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
                StatusTextBlock.Text = LocalizationService.GetString("Acc_CaptchaSubmitting");
                LoadingRing.IsActive = true;
                await PollBurstAfterRedirectAsync();
            }
        }
    }

    private async Task PollBurstAfterRedirectAsync()
    {
        try
        {
            for (int i = 0; i < 6 && !_finished && !_cts.IsCancellationRequested; i++)
            {
                if (await TrySinglePollAsync())
                {
                    return;
                }

                await Task.Delay(250, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunPollLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_finished)
            {
                await Task.Delay(1200, ct);
                if (await TrySinglePollAsync())
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> TrySinglePollAsync()
    {
        if (_finished || _cts.IsCancellationRequested) return true;
        try
        {
            await _pollSemaphore.WaitAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return true;
        }

        try
        {
            if (_finished || _cts.IsCancellationRequested) return true;
            bool done = await _tryConsumePollAsync();
            if (done) _finished = true;
            return done;
        }
        catch
        {
            return false;
        }
        finally
        {
            _pollSemaphore.Release();
        }
    }

    private void OnOpenInBrowserClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _captchaUri.ToString(),
                UseShellExecute = true
            });
            StatusTextBlock.Text = LocalizationService.GetString("Acc_CaptchaBrowserOpened");
        }
        catch
        {
        }
    }
}