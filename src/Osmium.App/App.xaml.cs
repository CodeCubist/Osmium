using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Osmium.App.Services;
using Osmium.App.Views;
using Osmium.Core.Models;
using Osmium.Core.Network;
using Osmium.Core.Services;

namespace Osmium.App;

public enum LanguageRestartAction
{
    None,
    RestartingNow,
    DeferredUntilSessionEnds
}

public partial class App : Application
{
    public static App? CurrentApp
    {
        get;
        private set;
    }

    public static EngineService Engine
    {
        get;
        private set;
    } = null!;

    public static MainWindow? MainWindowInstance
    {
        get;
        private set;
    }

    public static bool RestartRequested => Volatile.Read(ref _restartRequested) != 0;
    public static bool IsPendingLanguageRestart => Volatile.Read(ref _pendingLanguageRestart) != 0;

    private static int _restartRequested;
    private static int _restartProcessLaunched;
    private static int _pendingLanguageRestart;

    private RemoteSessionWindow? _activeRemoteWindow;
    private int _executingRestart;

    public bool IsRemoteControlInProgress =>
        (Engine?.Coordinator.IsAnyRemoteSessionActive ?? false) || _activeRemoteWindow != null;

    public App()
    {
        CurrentApp = this;
        InitializeComponent();
        UnhandledException += OnAppUnhandledException;
        Engine = new EngineService();
        try
        {
            Engine.Initialize();
        }
        catch (Exception ex)
        {
            Engine.Logging.Error($"Engine initialization exception: {ex}");
        }

        LocalizationService.Initialize(Engine.SettingsService.Settings.Language);
        Engine.Coordinator.Localizer = (key, args) => LocalizationService.Format(key, args);
        Engine.AccountService.Localizer = (key, args) => LocalizationService.Format(key, args);

        if (Engine.SettingsService.Settings.UiTheme == AppThemePreference.Light)
            RequestedTheme = ApplicationTheme.Light;
        else if (Engine.SettingsService.Settings.UiTheme == AppThemePreference.Dark)
            RequestedTheme = ApplicationTheme.Dark;

        try
        {
            var appInstance = AppInstance.GetCurrent();
            appInstance.Activated += App_Activated;
        }
        catch
        {
        }
    }

    private void App_Activated(object? sender, AppActivationArguments e)
    {
        MainWindowInstance?.DispatcherQueue?.TryEnqueue(() =>
        {
            MainWindowInstance?.RestoreFromTray();
        });
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Helpers.StartupHelper.SyncExecutablePathIfEnabled();
        MainWindowInstance = new MainWindow();
        Engine.Coordinator.LaunchRemoteSessionAsync = LaunchRemoteSessionWindowAsync;
        Engine.Coordinator.SolveRemoteCaptchaAsync = SolveRemoteCaptchaOnUiAsync;
        Engine.Coordinator.RemoteSessionStateChanged += OnRemoteSessionStateChanged;
        _ = Engine.AccountService.InitializeOnStartupAsync();
        MainWindowInstance.Activated += (_, _) => TryShowUpdateAnnouncement();
        MainWindowInstance.Closed += (_, _) =>
        {
            Engine.Coordinator.RemoteSessionStateChanged -= OnRemoteSessionStateChanged;
            StopUpdateAnnouncements();
            try
            {
                _activeRemoteWindow?.Close();
            }
            catch
            {
            }

            Engine.Dispose();
        };

        var s = Engine.SettingsService.Settings;
        if (Program.LaunchedAtStartup && (s.CloseToTray || s.HideToTrayOnMinimize))
        {
            MainWindowInstance.HideToTray();
        }
        else
        {
            MainWindowInstance.Activate();
        }

        _ = CheckForUpdateAsync();
    }

    public LanguageRestartAction RequestLanguageRestart(string targetLanguage)
    {
        string resolved = LocalizationService.ResolveLanguage(targetLanguage);
        if (string.Equals(resolved, LocalizationService.ActiveLanguage, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Exchange(ref _pendingLanguageRestart, 0);
            return LanguageRestartAction.None;
        }

        if (IsRemoteControlInProgress)
        {
            Interlocked.Exchange(ref _pendingLanguageRestart, 1);
            Engine?.Logging.Info($"Deferred language restart to '{resolved}' until active remote control session ends");
            return LanguageRestartAction.DeferredUntilSessionEnds;
        }

        Interlocked.Exchange(ref _pendingLanguageRestart, 0);
        Engine?.Logging.Info($"Scheduling clean application restart to apply language '{resolved}'");
        ScheduleRestartNow(delayMs: 140);
        return LanguageRestartAction.RestartingNow;
    }

    private void OnRemoteSessionStateChanged()
    {
        TryExecutePendingRestartIfIdle();
        OnUpdateSessionStateChanged();
    }

    private void TryExecutePendingRestartIfIdle()
    {
        if (Volatile.Read(ref _pendingLanguageRestart) == 0) return;
        if (IsRemoteControlInProgress) return;

        if (Interlocked.CompareExchange(ref _pendingLanguageRestart, 0, 1) == 1)
        {
            Engine?.Logging.Info("Remote control session ended; executing pending language restart");
            ScheduleRestartNow(delayMs: 260);
        }
    }

    private void ScheduleRestartNow(int delayMs)
    {
        if (Interlocked.Exchange(ref _executingRestart, 1) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs);
                }

                var queue = MainWindowInstance?.DispatcherQueue;
                if (queue != null)
                {
                    if (!queue.TryEnqueue(ExecuteRestartOnUiThread))
                    {
                        ExecuteRestartFallback();
                    }
                }
                else
                {
                    ExecuteRestartFallback();
                }
            }
            catch
            {
                ExecuteRestartFallback();
            }
        });
    }

    private void ExecuteRestartOnUiThread()
    {
        if (IsRemoteControlInProgress)
        {
            Interlocked.Exchange(ref _pendingLanguageRestart, 1);
            Interlocked.Exchange(ref _executingRestart, 0);
            return;
        }

        Interlocked.Exchange(ref _restartRequested, 1);

        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            ExecuteRestartFallback();
        });

        try
        {
            AppInstance.GetCurrent().UnregisterKey();
        }
        catch
        {
        }

        try
        {
            _activeRemoteWindow?.Close();
        }
        catch
        {
        }

        try
        {
            MainWindowInstance?.ForceExit();
        }
        catch
        {
        }

        try
        {
            Engine?.Dispose();
        }
        catch
        {
        }

        try
        {
            Exit();
        }
        catch
        {
        }
    }

    private static void ExecuteRestartFallback()
    {
        Interlocked.Exchange(ref _restartRequested, 1);
        try
        {
            AppInstance.GetCurrent().UnregisterKey();
        }
        catch
        {
        }

        try
        {
            Engine?.Dispose();
        }
        catch
        {
        }

        LaunchRestartProcessOnce();
        Environment.Exit(0);
    }

    internal static void LaunchRestartProcessOnce()
    {
        if (Interlocked.Exchange(ref _restartProcessLaunched, 1) != 0) return;

        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            try
            {
                exePath = Process.GetCurrentProcess().MainModule?.FileName;
            }
            catch
            {
            }
        }

        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;

        string args = $"restart --wait-pid {Environment.ProcessId}";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = false
                });
            }
            catch
            {
            }
        }
    }

    private Task<bool> SolveRemoteCaptchaOnUiAsync(AccountChallengeInfo challenge)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = MainWindowInstance?.DispatcherQueue;
        if (queue == null)
        {
            tcs.TrySetResult(false);
            return tcs.Task;
        }

        queue.TryEnqueue(async () =>
        {
            try
            {
                var res = await CloudCaptchaWindow.ShowChallengeAsync<object>(Engine.AccountService, challenge);
                tcs.TrySetResult(res is { Ok: true });
            }
            catch
            {
                tcs.TrySetResult(false);
            }
        });

        return tcs.Task;
    }

    private Task LaunchRemoteSessionWindowAsync(RemoteSessionLaunchArgs launchArgs)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = MainWindowInstance?.DispatcherQueue;
        if (queue == null)
        {
            tcs.TrySetException(new InvalidOperationException("Main UI DispatcherQueue unavailable"));
            return tcs.Task;
        }

        queue.TryEnqueue(() =>
        {
            try
            {
                if (_activeRemoteWindow != null)
                {
                    try
                    {
                        _activeRemoteWindow.Close();
                    }
                    catch
                    {
                    }

                    _activeRemoteWindow = null;
                }

                var win = new RemoteSessionWindow(launchArgs);
                _activeRemoteWindow = win;
                win.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_activeRemoteWindow, win))
                    {
                        _activeRemoteWindow = null;
                    }

                    tcs.TrySetResult(null);
                    TryExecutePendingRestartIfIdle();
                    TryShowUpdateAnnouncement();
                };
                win.Activate();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    private void OnAppUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Program.ReportFatalError("WinUI.UnhandledException", e.Exception, showDialog: true,
            startup: MainWindowInstance == null);
        Environment.Exit(-1);
    }
}
