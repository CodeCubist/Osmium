using System.Diagnostics;
using Microsoft.Win32;
using Osmium.Core.Services;

namespace Osmium.App.Helpers;

internal static class StartupHelper
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Osmium";

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            return key?.GetValue(ValueName) is string cmd && !string.IsNullOrWhiteSpace(cmd);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsServiceAutoStartEnabled() => ServiceAutoStartHelper.IsServiceAutoStartEnabled();

    public static async Task<bool> EnableServiceAutoStartAsync(AccountService account, SettingsService settings)
    {
        try
        {
            string dataDir = settings.DataDirectory;
            account.StageServiceSessionForElevation();
            settings.StageServiceIdentityForElevation();
            string? ownerSid = ServiceAutoStartHelper.GetCurrentUserSidString();

            bool ok;
            if (SystemServiceHelper.IsCurrentProcessElevated())
            {
                ok = await Task.Run(() => ServiceAutoStartHelper.EnableServiceAutoStartElevated(dataDir, ownerSid));
            }
            else
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath)) return false;

                string sidArg = string.IsNullOrWhiteSpace(ownerSid) ? string.Empty : $" --owner-sid \"{ownerSid}\"";
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"--enable-boot-service --data-dir \"{dataDir}\"{sidArg}",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                await proc.WaitForExitAsync();
                ok = proc.ExitCode == 0;
            }

            SystemServiceHelper.NotifyStatusChanged();
            return ok && ServiceAutoStartHelper.IsServiceAutoStartEnabled();
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> DisableServiceAutoStartAsync()
    {
        try
        {
            bool ok;
            if (SystemServiceHelper.IsCurrentProcessElevated())
            {
                ok = await Task.Run(ServiceAutoStartHelper.DisableServiceAutoStartElevated);
            }
            else
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath)) return false;

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--disable-boot-service",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                await proc.WaitForExitAsync();
                ok = proc.ExitCode == 0;
            }

            SystemServiceHelper.NotifyStatusChanged();
            return ok && !ServiceAutoStartHelper.IsServiceAutoStartEnabled();
        }
        catch
        {
            return false;
        }
    }

    public static bool SetAutoStartEnabled(bool enable)
    {
        try
        {
            if (enable)
            {
                string command = BuildRunCommand();
                if (string.IsNullOrEmpty(command)) return false;

                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);
                key.SetValue(ValueName, command, RegistryValueKind.String);
                return true;
            }
            else
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    public static void SyncExecutablePathIfEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true);
            if (key?.GetValue(ValueName) is not string existing || string.IsNullOrWhiteSpace(existing))
                return;

            string expected = BuildRunCommand();
            if (!string.IsNullOrEmpty(expected) &&
                !string.Equals(existing, expected, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, expected, RegistryValueKind.String);
            }
        }
        catch
        {
        }
    }

    private static string BuildRunCommand()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath)) return string.Empty;
        return $"\"{exePath}\" --startup";
    }
}