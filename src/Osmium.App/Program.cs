using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Osmium.Core.Interop;

namespace Osmium.App;

public static class Program
{
    internal const string SingleInstanceKey = "Osmium";
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;

    internal static bool LaunchedAtStartup
    {
        get;
        private set;
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [STAThread]
    private static void Main(string[] args)
    {
        if (HandleHeadlessServiceArgs(args, out int headlessExitCode))
        {
            Environment.ExitCode = headlessExitCode;
            return;
        }

        WaitForPreviousProcessExit(args);

        LaunchedAtStartup =
            args != null && args.Any(a => string.Equals(a, "--startup", StringComparison.OrdinalIgnoreCase));

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                ReportFatalError("AppDomain.UnhandledException", ex, showDialog: true);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            try
            {
                App.Engine?.Logging.Error($"Unobserved task exception: {e.Exception}");
            }
            catch
            {
                WriteCrashLog("TaskScheduler.UnobservedTaskException", e.Exception);
            }
        };

        try
        {
            Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
            Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY_PID",
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            WinRT.ComWrappersSupport.InitializeComWrappers();

            bool isRestartLaunch = args != null && args.Any(a =>
                string.Equals(a, "restart", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--wait-pid", StringComparison.OrdinalIgnoreCase));

            if (isRestartLaunch)
            {
                const int maxRetries = 50;
                const int retryDelayMs = 100;
                for (int i = 0; i < maxRetries; i++)
                {
                    var instance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
                    if (instance.IsCurrent)
                    {
                        goto startApp;
                    }

                    instance.UnregisterKey();
                    Thread.Sleep(retryDelayMs);
                }
            }

            var mainInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
            if (!mainInstance.IsCurrent)
            {
                var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
                var task = mainInstance.RedirectActivationToAsync(activationArgs).AsTask();
                task.Wait();
                return;
            }

            startApp:
            Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });

            if (App.RestartRequested)
            {
                try
                {
                    AppInstance.GetCurrent().UnregisterKey();
                }
                catch
                {
                }

                try
                {
                    App.Engine?.Dispose();
                }
                catch
                {
                }

                App.LaunchRestartProcessOnce();
                Environment.Exit(0);
            }
        }
        catch (Exception ex)
        {
            ReportFatalError("Program.Main", ex, showDialog: true);
            Environment.ExitCode = ex.HResult != 0 ? ex.HResult : 1;
        }
    }

    private static void WaitForPreviousProcessExit(string[] args)
    {
        if (args == null || args.Length < 2) return;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--wait-pid", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out int prevPid) &&
                prevPid > 0 &&
                prevPid != Environment.ProcessId)
            {
                try
                {
                    using var prevProc = Process.GetProcessById(prevPid);
                    prevProc.WaitForExit(4000);
                }
                catch
                {
                }

                break;
            }
        }
    }

    private static bool HandleHeadlessServiceArgs(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args == null || args.Length == 0) return false;

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--service", StringComparison.OrdinalIgnoreCase))
            {
                exitCode = NativeMethods.Osmium_RunSystemService() != 0 ? 0 : 1;
                return true;
            }

            if (string.Equals(args[i], "--sas-once", StringComparison.OrdinalIgnoreCase))
            {
                exitCode = NativeMethods.Osmium_RunOneShotSasService() != 0 ? 0 : 1;
                return true;
            }

            if (string.Equals(args[i], "--install-drivers", StringComparison.OrdinalIgnoreCase))
            {
                exitCode = NativeMethods.Osmium_InstallVirtualDrivers() != 0 ? 0 : 1;
                return true;
            }

            if (string.Equals(args[i], "--uninstall-drivers", StringComparison.OrdinalIgnoreCase))
            {
                exitCode = NativeMethods.Osmium_UninstallVirtualDrivers() != 0 ? 0 : 1;
                return true;
            }
        }

        return false;
    }

    internal static void ReportFatalError(string stage, Exception ex, bool showDialog)
    {
        string? logPath = WriteCrashLog(stage, ex);

        if (!showDialog) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine(SafeTrFormat("Startup_ErrorTitleFmt", "Osmium failed to start - {0}", stage));
            sb.AppendLine();
            sb.AppendLine($"{ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
            {
                sb.AppendLine($"Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }

            if (!string.IsNullOrEmpty(logPath))
            {
                sb.AppendLine();
                sb.AppendLine(SafeTr("Startup_CrashLogSavedFmt", "Crash log saved to:"));
                sb.AppendLine(logPath);
            }

            MessageBox(IntPtr.Zero, sb.ToString(), SafeTr("Startup_ErrorCaption", "Osmium Startup Error"),
                MbOk | MbIconError);
        }
        catch
        {
        }
    }

    private static string SafeTr(string key, string fallback)
    {
        try
        {
            string s = Services.LocalizationService.GetString(key);
            return string.IsNullOrEmpty(s) || s == key ? fallback : s;
        }
        catch
        {
            return fallback;
        }
    }

    private static string SafeTrFormat(string key, string fallbackFmt, params object[] args)
    {
        string fmt = SafeTr(key, fallbackFmt);
        try
        {
            return string.Format(CultureInfo.CurrentUICulture, fmt, args);
        }
        catch
        {
            return string.Format(CultureInfo.InvariantCulture, fallbackFmt, args);
        }
    }

    private static string? WriteCrashLog(string stage, Exception ex)
    {
        try
        {
            string logDir = App.Engine?.Logging.LogDirectory
                            ?? Path.Combine(
                                File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.dat"))
                                    ? Path.Combine(AppContext.BaseDirectory, "OsmiumData")
                                    : Path.Combine(
                                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                        "Osmium"),
                                "logs");
            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, "startup_crash.log");
            string content =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Stage={stage}\n" +
                $"OS={Environment.OSVersion} | 64BitOS={Environment.Is64BitOperatingSystem} | ProcessArch={RuntimeInformation.ProcessArchitecture}\n" +
                $"Framework={RuntimeInformation.FrameworkDescription}\n" +
                $"BaseDir={AppContext.BaseDirectory}\n" +
                $"Exception={ex}\n" +
                new string('-', 80) + "\n";
            File.AppendAllText(logPath, content, new UTF8Encoding(false));
            return logPath;
        }
        catch
        {
            try
            {
                string fallback = Path.Combine(Path.GetTempPath(), "Osmium_startup_crash.log");
                File.AppendAllText(fallback, $"[{DateTime.Now:O}] {stage}: {ex}\n");
                return fallback;
            }
            catch
            {
                return null;
            }
        }
    }
}