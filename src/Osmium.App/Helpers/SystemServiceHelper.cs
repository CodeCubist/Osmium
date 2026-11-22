using System.Diagnostics;
using Osmium.Core.Interop;

namespace Osmium.App.Helpers;

public static class SystemServiceHelper
{
    public static event Action? StatusChanged;

    public static bool IsServiceReady()
    {
        try
        {
            return NativeMethods.Osmium_IsSystemServiceRunning() != 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsCurrentProcessElevated()
    {
        try
        {
            NativeMethods.Osmium_GetDriverStatus(out _, out int isElevated, out _);
            return isElevated != 0;
        }
        catch
        {
            return false;
        }
    }

    public static Task<bool> InstallAsync() => ExecuteOpAsync(install: true);

    public static Task<bool> UninstallAsync() => ExecuteOpAsync(install: false);

    private static async Task<bool> ExecuteOpAsync(bool install)
    {
        try
        {
            if (IsCurrentProcessElevated())
            {
                int res = await Task.Run(() => install
                    ? NativeMethods.Osmium_InstallSystemService()
                    : NativeMethods.Osmium_UninstallSystemService());
                return res != 0;
            }

            if (!string.IsNullOrEmpty(Environment.ProcessPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    Arguments = install ? "--install-service" : "--uninstall-service",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var proc = Process.Start(psi);
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
        finally
        {
            StatusChanged?.Invoke();
        }

        return false;
    }
}
