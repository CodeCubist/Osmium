using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Osmium.Core.Network;

namespace Osmium.Core.Services;

public sealed class RemotePowerService
{
    private readonly SettingsService _settings;
    private readonly AccountService _account;
    private readonly P2PCoordinator _coordinator;
    private readonly LoggingService _log;
    private readonly ConcurrentDictionary<string, long> _processedCommands = new(StringComparer.Ordinal);

    public RemotePowerService(
        SettingsService settings,
        AccountService account,
        P2PCoordinator coordinator,
        LoggingService log)
    {
        _settings = settings;
        _account = account;
        _coordinator = coordinator;
        _log = log;
    }

    public void HandlePowerCommand(PowerCommandEventArgs cmd)
    {
        if (cmd == null) return;

        if (!_settings.Settings.HostAllowRemotePower)
        {
            _log.Warn($"Rejected remote power command ({cmd.Action}): HostAllowRemotePower is disabled.");
            return;
        }

        if (!_account.IsLoggedIn || string.IsNullOrWhiteSpace(_account.CurrentUserId))
        {
            _log.Warn($"Rejected remote power command ({cmd.Action}): local account is not signed in.");
            return;
        }

        if (!_account.IsProfileComplete)
        {
            _log.Warn($"Rejected remote power command ({cmd.Action}): local account profile is incomplete.");
            return;
        }

        if (string.IsNullOrWhiteSpace(cmd.RequesterUserId) ||
            !string.Equals(cmd.RequesterUserId, _account.CurrentUserId, StringComparison.OrdinalIgnoreCase))
        {
            _log.Warn($"Rejected remote power command ({cmd.Action}): requester account mismatch.");
            return;
        }

        if (!TryMarkCommandProcessed(cmd.CommandId))
        {
            return;
        }

        string action = cmd.Action.Trim().ToLowerInvariant();
        if (action is not ("shutdown" or "restart"))
        {
            return;
        }

        int delaySec = Math.Clamp(cmd.DelaySeconds, 1, 30);
        _log.Info(
            $"Accepted remote power command: action={action}, delay={delaySec}s, from={cmd.RequesterDeviceName} ({cmd.RequesterDeviceId})");

        _ = Task.Run(() => ExecuteLocalPowerAction(action, delaySec));
    }

    public void HandleWolRelay(PowerWolRelayEventArgs relay)
    {
        if (relay == null) return;

        if (!_account.IsLoggedIn || string.IsNullOrWhiteSpace(_account.CurrentUserId))
        {
            _log.Warn("Ignored WoL relay request: local account is not signed in.");
            return;
        }

        if (!_account.IsProfileComplete)
        {
            _log.Warn("Ignored WoL relay request: local account profile is incomplete.");
            return;
        }

        if (string.IsNullOrWhiteSpace(relay.RequesterUserId) ||
            !string.Equals(relay.RequesterUserId, _account.CurrentUserId, StringComparison.OrdinalIgnoreCase))
        {
            _log.Warn("Ignored WoL relay request: requester account mismatch.");
            return;
        }

        if (relay.Macs.Count == 0 || !TryMarkCommandProcessed(relay.CommandId))
        {
            return;
        }

        _log.Info(
            $"Relaying Wake-on-LAN for {relay.TargetDeviceName} ({relay.TargetDeviceId}), MACs={string.Join(',', relay.Macs)}");

        _ = Task.Run(async () =>
        {
            try
            {
                var report = await WakeOnLanService.SendMagicPacketsAsync(
                    relay.Macs,
                    relay.Broadcasts,
                    relay.Ports,
                    repeatCount: 3);
                _log.Info(
                    $"WoL relay completed for {relay.TargetDeviceId}: packets={report.PacketsSent}, macs={report.MacsCount}");
            }
            catch (Exception ex)
            {
                _log.Warn($"WoL relay failed for {relay.TargetDeviceId}: {ex.Message}");
            }
        });
    }

    private void ExecuteLocalPowerAction(string action, int delaySeconds)
    {
        try
        {
            if (_coordinator.IsHostStreaming)
            {
                _coordinator.StopHostSession();
                Thread.Sleep(250);
            }
        }
        catch
        {
        }

        bool isReboot = string.Equals(action, "restart", StringComparison.OrdinalIgnoreCase);

        try
        {
            string sysDir = Environment.SystemDirectory;
            string shutdownExe = Path.Combine(sysDir, "shutdown.exe");
            string args = isReboot
                ? $"/r /t {delaySeconds} /f"
                : $"/s /t {delaySeconds} /f";

            var psi = new ProcessStartInfo
            {
                FileName = File.Exists(shutdownExe) ? shutdownExe : "shutdown.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit(3000);
                if (proc.HasExited && proc.ExitCode == 0)
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"shutdown.exe invocation failed, falling back to Win32 API: {ex.Message}");
        }

        try
        {
            EnableShutdownPrivilege();
            InitiateSystemShutdownExW(
                null,
                null,
                (uint)delaySeconds,
                true,
                isReboot,
                ShtdnReasonMajorApplication | ShtdnReasonMinorMaintenance | ShtdnReasonFlagPlanned);
        }
        catch (Exception ex)
        {
            _log.Error($"Win32 InitiateSystemShutdownExW failed: {ex.Message}");
        }
    }

    private bool TryMarkCommandProcessed(string? commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId)) return true;
        long now = Environment.TickCount64;
        if (_processedCommands.Count > 256)
        {
            foreach (var kv in _processedCommands)
            {
                if (now - kv.Value > 120_000)
                {
                    _processedCommands.TryRemove(kv.Key, out _);
                }
            }
        }

        return _processedCommands.TryAdd(commandId, now);
    }

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const uint ShtdnReasonMajorApplication = 0x00040000;
    private const uint ShtdnReasonMinorMaintenance = 0x00000001;
    private const uint ShtdnReasonFlagPlanned = 0x80000000;

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

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out Luid lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool InitiateSystemShutdownExW(
        string? lpMachineName,
        string? lpMessage,
        uint dwTimeout,
        bool bForceAppsClosed,
        bool bRebootAfterShutdown,
        uint dwReason);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static void EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAdjustPrivileges | TokenQuery, out IntPtr token))
        {
            return;
        }

        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) return;
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
}
