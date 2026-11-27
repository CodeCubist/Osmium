using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Osmium.Core.Services;

public sealed class ServiceBootHostSupervisor : IDisposable
{
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenAdjustSessionId = 0x0100;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityIdentification = 1;
    private const int TokenPrimary = 1;
    private const int TokenSessionId = 12;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const uint CreateNoWindow = 0x08000000;
    private const uint NormalPriorityClass = 0x00000020;
    private const uint WaitTimeout = 0x00000102;

    private CancellationTokenSource? _cts;
    private Thread? _workerThread;
    private IntPtr _hostProcessHandle = IntPtr.Zero;
    private uint _hostSessionId = 0xFFFFFFFF;
    private int _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short CbReserved2;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out Luid lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        ref uint tokenInformation,
        uint tokenInformationLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(
        IntPtr token,
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr processHandle, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _workerThread = new Thread(() => MonitorLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "OsmiumBootHostSupervisor"
        };
        _workerThread.Start();
    }

    private void MonitorLoop(CancellationToken ct)
    {
        IntPtr stopEvt = ServiceAutoStartHelper.CreateSharedEvent(
            ServiceAutoStartHelper.ServiceHostStopEventName,
            manualReset: true,
            initialState: false);
        IntPtr yieldEvt = ServiceAutoStartHelper.CreateSharedEvent(
            ServiceAutoStartHelper.ServiceHostYieldEventName,
            manualReset: true,
            initialState: false);
        IntPtr forceYieldEvt = ServiceAutoStartHelper.CreateSharedEvent(
            ServiceAutoStartHelper.ServiceHostForceYieldEventName,
            manualReset: true,
            initialState: false);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    bool enabled = ServiceAutoStartHelper.IsServiceAutoStartEnabled();
                    if (!enabled)
                    {
                        StopChildHost(stopEvt);
                    }
                    else
                    {
                        bool uiActive = ServiceAutoStartHelper.IsDesktopUiActive();
                        if (uiActive)
                        {
                            ReapIfExited();
                        }
                        else
                        {
                            uint activeSid = WTSGetActiveConsoleSessionId();
                            if (activeSid != 0 && activeSid != 0xFFFFFFFF)
                            {
                                bool alive = IsChildAlive();
                                if (alive && _hostSessionId != activeSid)
                                {
                                    StopChildHost(stopEvt);
                                    alive = false;
                                }

                                if (!alive)
                                {
                                    ServiceAutoStartHelper.SetNativeEventState(stopEvt, false);
                                    ServiceAutoStartHelper.SetNativeEventState(yieldEvt, false);
                                    ServiceAutoStartHelper.SetNativeEventState(forceYieldEvt, false);
                                    LaunchChildHostInSession(activeSid);
                                }
                            }
                        }
                    }
                }
                catch
                {
                }

                ct.WaitHandle.WaitOne(1500);
            }

            StopChildHost(stopEvt);
        }
        finally
        {
            ServiceAutoStartHelper.CloseNativeHandle(stopEvt);
            ServiceAutoStartHelper.CloseNativeHandle(yieldEvt);
            ServiceAutoStartHelper.CloseNativeHandle(forceYieldEvt);
        }
    }

    private bool IsChildAlive()
    {
        if (_hostProcessHandle == IntPtr.Zero) return false;
        if (WaitForSingleObject(_hostProcessHandle, 0) == WaitTimeout)
        {
            return true;
        }

        CloseHandle(_hostProcessHandle);
        _hostProcessHandle = IntPtr.Zero;
        _hostSessionId = 0xFFFFFFFF;
        return false;
    }

    private void ReapIfExited()
    {
        if (_hostProcessHandle != IntPtr.Zero && WaitForSingleObject(_hostProcessHandle, 0) != WaitTimeout)
        {
            CloseHandle(_hostProcessHandle);
            _hostProcessHandle = IntPtr.Zero;
            _hostSessionId = 0xFFFFFFFF;
        }
    }

    private void StopChildHost(IntPtr stopEvt)
    {
        if (_hostProcessHandle == IntPtr.Zero) return;

        ServiceAutoStartHelper.SetNativeEventState(stopEvt, true);
        if (WaitForSingleObject(_hostProcessHandle, 3000) == WaitTimeout)
        {
            TerminateProcess(_hostProcessHandle, 0);
        }

        CloseHandle(_hostProcessHandle);
        _hostProcessHandle = IntPtr.Zero;
        _hostSessionId = 0xFFFFFFFF;
    }

    private void LaunchChildHostInSession(uint sessionId)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;

        string dataDir = ServiceAutoStartHelper.GetServiceProfileDirectory();
        if (!Directory.Exists(dataDir))
        {
            try
            {
                Directory.CreateDirectory(dataDir);
            }
            catch
            {
                return;
            }
        }

        EnablePrivilege("SeTcbPrivilege");
        EnablePrivilege("SeAssignPrimaryTokenPrivilege");
        EnablePrivilege("SeIncreaseQuotaPrivilege");

        if (!OpenProcessToken(
                Process.GetCurrentProcess().Handle,
                TokenDuplicate | TokenAssignPrimary | TokenQuery | TokenAdjustDefault | TokenAdjustSessionId,
                out IntPtr selfToken))
        {
            return;
        }

        IntPtr dupToken = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(
                    selfToken,
                    MaximumAllowed,
                    IntPtr.Zero,
                    SecurityIdentification,
                    TokenPrimary,
                    out dupToken) ||
                dupToken == IntPtr.Zero)
            {
                return;
            }

            uint sidCopy = sessionId;
            if (!SetTokenInformation(dupToken, TokenSessionId, ref sidCopy, sizeof(uint)))
            {
                return;
            }

            string cmdLine = $"\"{exePath}\" --service-host --data-dir \"{dataDir}\"";
            string? workDir = Path.GetDirectoryName(exePath);

            var si = new StartupInfo
            {
                Cb = Marshal.SizeOf<StartupInfo>(),
                Desktop = @"winsta0\default"
            };

            if (CreateProcessAsUserW(
                    dupToken,
                    exePath,
                    cmdLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CreateNoWindow | NormalPriorityClass,
                    IntPtr.Zero,
                    workDir,
                    ref si,
                    out var pi))
            {
                if (pi.Thread != IntPtr.Zero) CloseHandle(pi.Thread);
                _hostProcessHandle = pi.Process;
                _hostSessionId = sessionId;
            }
        }
        finally
        {
            CloseHandle(selfToken);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
        }
    }

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAdjustPrivileges | TokenQuery, out IntPtr token))
        {
            return;
        }

        try
        {
            if (!LookupPrivilegeValueW(null, name, out var luid)) return;
            var tp = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new LuidAndAttributes
                {
                    Luid = luid,
                    Attributes = SePrivilegeEnabled
                }
            };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _cts?.Cancel();
            _workerThread?.Join(4000);
        }
        catch
        {
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }
}