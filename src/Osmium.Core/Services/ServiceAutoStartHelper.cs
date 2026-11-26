using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Osmium.Core.Helpers;
using Osmium.Core.Interop;

namespace Osmium.Core.Services;

public static partial class ServiceAutoStartHelper
{
    public const string RegistryKeyPath = @"SOFTWARE\Osmium";
    public const string RegistryEnabledValue = "ServiceAutoStart";
    public const string RegistryDataDirValue = "ServiceDataDir";
    public const string ServiceSessionFileName = "osmium_account_session.svc.dat";
    public const string ServiceIdentityFileName = "osmium_identity.svc.dat";

    public const string DesktopUiActiveMutexName = @"Global\OsmiumDesktopUiActive";
    public const string ServiceHostYieldEventName = @"Global\OsmiumServiceHostYield";
    public const string ServiceHostForceYieldEventName = @"Global\OsmiumServiceHostForceYield";
    public const string ServiceHostStopEventName = @"Global\OsmiumServiceHostStop";
    public const string ServiceHostStreamingEventName = @"Global\OsmiumServiceHostStreaming";

    private const string SharedSyncSddl = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;AU)";
    private const uint TokenQuery = 0x0008;
    private const int TokenUserClass = 1;
    private const uint SddlRevision1 = 1;
    private const uint EventModifyState = 0x0002;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0x00000000;

    [GeneratedRegex(@"^S-1-5-[0-9\-]{5,120}$", RegexOptions.Compiled)]
    private static partial Regex ValidSidRegex();

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateMutexW(ref SecurityAttributes lpMutexAttributes, bool bInitialOwner, string lpName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenMutexW(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(ref SecurityAttributes lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenEventW(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ResetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string GetDefaultServiceProfileDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Osmium",
            "ServiceProfile");
    }

    public static string GetServiceProfileDirectory()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, writable: false);
            string? configured = key?.GetValue(RegistryDataDirValue) as string;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }
        }
        catch
        {
        }

        return GetDefaultServiceProfileDirectory();
    }

    public static bool IsServiceAutoStartEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, writable: false);
            if (key?.GetValue(RegistryEnabledValue) is int val)
            {
                return val != 0;
            }
        }
        catch
        {
        }

        return false;
    }

    public static string? GetCurrentUserSidString()
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenQuery, out IntPtr token))
        {
            return null;
        }

        try
        {
            GetTokenInformation(token, TokenUserClass, IntPtr.Zero, 0, out int len);
            if (len <= 0) return null;

            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetTokenInformation(token, TokenUserClass, buf, len, out _))
                {
                    return null;
                }

                var user = Marshal.PtrToStructure<SidAndAttributes>(buf);
                if (user.Sid == IntPtr.Zero) return null;

                if (!ConvertSidToStringSidW(user.Sid, out IntPtr strPtr) || strPtr == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return Marshal.PtrToStringUni(strPtr);
                }
                finally
                {
                    LocalFree(strPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    public static bool EnableServiceAutoStartElevated(string? sourceDataDir, string? ownerSid)
    {
        try
        {
            string profileDir = GetDefaultServiceProfileDirectory();
            Directory.CreateDirectory(profileDir);

            ApplyRestrictedDirectoryAcl(profileDir, ownerSid);

            if (!string.IsNullOrWhiteSpace(sourceDataDir) && Directory.Exists(sourceDataDir))
            {
                CopyFileAtomicSafe(Path.Combine(sourceDataDir, "settings.json"), Path.Combine(profileDir, "settings.json"));
                string wallpaper = WindowsWallpaperHelper.PrivacyWallpaperFileName;
                CopyFileAtomicSafe(Path.Combine(sourceDataDir, wallpaper), Path.Combine(profileDir, wallpaper));

                string stagedSvcIdentity = Path.Combine(sourceDataDir, ServiceIdentityFileName);
                if (File.Exists(stagedSvcIdentity))
                {
                    CopyFileAtomicSafe(stagedSvcIdentity, Path.Combine(profileDir, ServiceIdentityFileName));
                    DpapiStorageHelper.SecureDeleteFile(stagedSvcIdentity);
                }

                DpapiStorageHelper.SecureDeleteFile(Path.Combine(profileDir, "identity.json"));

                string stagedSvcSession = Path.Combine(sourceDataDir, ServiceSessionFileName);
                if (File.Exists(stagedSvcSession))
                {
                    CopyFileAtomicSafe(stagedSvcSession, Path.Combine(profileDir, ServiceSessionFileName));
                    DpapiStorageHelper.SecureDeleteFile(stagedSvcSession);
                }
            }

            using (var key = Registry.LocalMachine.CreateSubKey(RegistryKeyPath, writable: true))
            {
                if (key == null) return false;
                key.SetValue(RegistryDataDirValue, profileDir, RegistryValueKind.String);
                key.SetValue(RegistryEnabledValue, 1, RegistryValueKind.DWord);
            }

            bool svcRunning = NativeMethods.Osmium_IsSystemServiceRunning() != 0;
            if (!svcRunning)
            {
                svcRunning = NativeMethods.Osmium_InstallSystemService() != 0;
            }

            return svcRunning;
        }
        catch
        {
            return false;
        }
    }

    public static bool DisableServiceAutoStartElevated()
    {
        try
        {
            using (var key = Registry.LocalMachine.CreateSubKey(RegistryKeyPath, writable: true))
            {
                key?.SetValue(RegistryEnabledValue, 0, RegistryValueKind.DWord);
            }

            SignalEventByName(ServiceHostStopEventName);

            string profileDir = GetServiceProfileDirectory();
            DpapiStorageHelper.SecureDeleteFile(Path.Combine(profileDir, ServiceSessionFileName));
            DpapiStorageHelper.SecureDeleteFile(Path.Combine(profileDir, ServiceIdentityFileName));
            DpapiStorageHelper.SecureDeleteFile(Path.Combine(profileDir, "identity.json"));

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void SyncProfileFileToService(string sourceFilePath, string targetFileName)
    {
        if (!IsServiceAutoStartEnabled()) return;
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath)) return;

        try
        {
            string profileDir = GetServiceProfileDirectory();
            if (!Directory.Exists(profileDir)) return;
            CopyFileAtomicSafe(sourceFilePath, Path.Combine(profileDir, targetFileName));
        }
        catch
        {
        }
    }

    public static void WriteServiceIdentityBytes(byte[] protectedBytes)
    {
        if (!IsServiceAutoStartEnabled()) return;
        try
        {
            string profileDir = GetServiceProfileDirectory();
            if (!Directory.Exists(profileDir)) return;
            string target = Path.Combine(profileDir, ServiceIdentityFileName);
            DpapiStorageHelper.WriteBytesAtomic(target, protectedBytes);
            DpapiStorageHelper.SecureDeleteFile(Path.Combine(profileDir, "identity.json"));
        }
        catch
        {
        }
    }

    public static void RemoveServiceIdentityFile()
    {
        try
        {
            string profileDir = GetServiceProfileDirectory();
            DpapiStorageHelper.SecureDeleteFile(Path.Combine(profileDir, ServiceIdentityFileName));
            DpapiStorageHelper.SecureDeleteFile(Path.Combine(profileDir, "identity.json"));
        }
        catch
        {
        }
    }

    public static void WriteServiceSessionBytes(byte[] protectedBytes)
    {
        if (!IsServiceAutoStartEnabled()) return;
        try
        {
            string profileDir = GetServiceProfileDirectory();
            if (!Directory.Exists(profileDir)) return;
            string target = Path.Combine(profileDir, ServiceSessionFileName);
            string tmp = target + ".tmp";
            File.WriteAllBytes(tmp, protectedBytes);
            File.Move(tmp, target, overwrite: true);
        }
        catch
        {
        }
    }

    public static void RemoveServiceSessionFile()
    {
        try
        {
            string profileDir = GetServiceProfileDirectory();
            string target = Path.Combine(profileDir, ServiceSessionFileName);
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        catch
        {
        }
    }

    public static IntPtr CreateSharedEvent(string name, bool manualReset, bool initialState)
    {
        IntPtr sd = IntPtr.Zero;
        try
        {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(SharedSyncSddl, SddlRevision1, out sd, out _);
            var sa = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = sd,
                InheritHandle = false
            };
            return CreateEventW(ref sa, manualReset, initialState, name);
        }
        finally
        {
            if (sd != IntPtr.Zero) LocalFree(sd);
        }
    }

    public static IDisposable? AcquireDesktopUiActiveMutex()
    {
        IntPtr sd = IntPtr.Zero;
        try
        {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(SharedSyncSddl, SddlRevision1, out sd, out _);
            var sa = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = sd,
                InheritHandle = false
            };
            IntPtr hMutex = CreateMutexW(ref sa, true, DesktopUiActiveMutexName);
            return hMutex != IntPtr.Zero ? new NativeHandleGuard(hMutex) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (sd != IntPtr.Zero) LocalFree(sd);
        }
    }

    public static bool IsDesktopUiActive()
    {
        IntPtr h = OpenMutexW(Synchronize, false, DesktopUiActiveMutexName);
        if (h == IntPtr.Zero) return false;
        CloseHandle(h);
        return true;
    }

    public static bool IsServiceHostStreaming()
    {
        IntPtr h = OpenEventW(Synchronize, false, ServiceHostStreamingEventName);
        if (h == IntPtr.Zero) return false;
        try
        {
            return WaitForSingleObject(h, 0) == WaitObject0;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static void SignalServiceHostYield(bool force)
    {
        SignalEventByName(force ? ServiceHostForceYieldEventName : ServiceHostYieldEventName);
    }

    public static void SignalEventByName(string name)
    {
        IntPtr h = OpenEventW(EventModifyState, false, name);
        if (h == IntPtr.Zero) return;
        try
        {
            SetEvent(h);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static void SetNativeEventState(IntPtr handle, bool signaled)
    {
        if (handle == IntPtr.Zero) return;
        if (signaled) SetEvent(handle);
        else ResetEvent(handle);
    }

    public static void CloseNativeHandle(IntPtr handle)
    {
        if (handle != IntPtr.Zero) CloseHandle(handle);
    }

    public static bool IsNativeEventSignaled(IntPtr handle)
    {
        return handle != IntPtr.Zero && WaitForSingleObject(handle, 0) == WaitObject0;
    }

    private static void CopyFileAtomicSafe(string src, string dst)
    {
        if (!File.Exists(src)) return;
        string tmp = dst + ".tmp";
        File.Copy(src, tmp, overwrite: true);
        File.Move(tmp, dst, overwrite: true);
    }

    private static void ApplyRestrictedDirectoryAcl(string dirPath, string? ownerSid)
    {
        string sidArg = string.Empty;
        if (!string.IsNullOrWhiteSpace(ownerSid) && ValidSidRegex().IsMatch(ownerSid.Trim()))
        {
            sidArg = $" /grant:r \"*{ownerSid.Trim()}:(OI)(CI)M\"";
        }

        string icaclsExe = Path.Combine(Environment.SystemDirectory, "icacls.exe");
        var psi = new ProcessStartInfo
        {
            FileName = File.Exists(icaclsExe) ? icaclsExe : "icacls.exe",
            Arguments = $"\"{dirPath}\" /inheritance:r /grant:r \"*S-1-5-18:(OI)(CI)F\" /grant:r \"*S-1-5-32-544:(OI)(CI)F\"{sidArg}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit(5000);
    }

    private sealed class NativeHandleGuard(IntPtr handle) : IDisposable
    {
        private IntPtr _handle = handle;

        public void Dispose()
        {
            IntPtr h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (h != IntPtr.Zero)
            {
                CloseHandle(h);
            }
        }
    }
}
