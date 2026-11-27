using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Osmium.Core.Services;

public sealed class LocalPowerProfile
{
    public bool AllowRemotePower
    {
        get;
        init;
    }

    public List<string> MacAddresses
    {
        get;
        init;
    } = [];

    public List<string> BroadcastAddresses
    {
        get;
        init;
    } = [];

    public bool WolReady
    {
        get;
        init;
    }

    public string PrimaryMac => MacAddresses.Count > 0 ? MacAddresses[0] : string.Empty;

    public string AdapterSummary
    {
        get;
        init;
    } = string.Empty;
}

public sealed class WakeOnLanSendReport
{
    public int PacketsSent
    {
        get;
        init;
    }

    public int MacsCount
    {
        get;
        init;
    }
}

public sealed class WolConfigurationResult
{
    public bool Success
    {
        get;
        init;
    }

    public int ConfiguredAdapters
    {
        get;
        init;
    }

    public bool FastStartupDisabled
    {
        get;
        init;
    }
}

public sealed class WolRestoreResult
{
    public bool Success
    {
        get;
        init;
    }

    public bool HadBackup
    {
        get;
        init;
    }

    public int RestoredAdapters
    {
        get;
        init;
    }

    public bool FastStartupRestored
    {
        get;
        init;
    }
}

public static class WakeOnLanService
{
    private const string NetAdapterClassRegPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    private const string PowerSessionManagerRegPath =
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";

    private const string OsmiumMachineRegPath = @"SOFTWARE\Osmium";
    private const string WolBackupRegValueName = "WolBackupJson";

    private sealed class WolRegistryValueSnapshot
    {
        public bool Existed
        {
            get;
            set;
        }

        public int Kind
        {
            get;
            set;
        }

        public string? StringValue
        {
            get;
            set;
        }

        public int? DwordValue
        {
            get;
            set;
        }

        public long? QwordValue
        {
            get;
            set;
        }

        public string? BinaryBase64
        {
            get;
            set;
        }

        public List<string>? MultiStringValue
        {
            get;
            set;
        }
    }

    private sealed class WolAdapterRegistrySnapshot
    {
        public string SubKeyName
        {
            get;
            set;
        } = string.Empty;

        public string DriverDesc
        {
            get;
            set;
        } = string.Empty;

        public WolRegistryValueSnapshot? StarWakeOnMagicPacket
        {
            get;
            set;
        }

        public WolRegistryValueSnapshot? WakeOnMagicPacket
        {
            get;
            set;
        }

        public WolRegistryValueSnapshot? PnPCapabilities
        {
            get;
            set;
        }
    }

    private sealed class WolWakeDeviceSnapshot
    {
        public string DeviceName
        {
            get;
            set;
        } = string.Empty;

        public bool WasOriginallyArmed
        {
            get;
            set;
        }
    }

    private sealed class WolSystemBackupSnapshot
    {
        public int Version
        {
            get;
            set;
        } = 1;

        public long CapturedAtUnix
        {
            get;
            set;
        }

        public List<WolWakeDeviceSnapshot> WakeDevices
        {
            get;
            set;
        } = [];

        public List<WolAdapterRegistrySnapshot> Adapters
        {
            get;
            set;
        } = [];

        public WolRegistryValueSnapshot? HiberbootEnabled
        {
            get;
            set;
        }
    }

    private static readonly string[] VirtualAdapterTokens =
    [
        "virtual", "vmware", "vbox", "virtualbox", "hyper-v", "vethernet",
        "loopback", "tap-", "tun", "wintun", "wireguard", "tailscale",
        "zerotier", "radmin", "hamachi", "bluetooth", "pseudo", "teredo",
        "isatap", "km-test", "npcap", "wsl", "docker", "bridge", "miniport",
        "vpn", "clash", "sing-box", "mihomo", "nekobox", "v2ray", "softether",
        "openvpn", "cisco anyconnect", "fortinet", "check point"
    ];

    public static bool TryParseMacAddress(string? raw, out byte[] macBytes)
    {
        macBytes = [];
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var hex = new StringBuilder(12);
        foreach (char ch in raw.Trim())
        {
            if (ch is ':' or '-' or '.' or ' ') continue;
            if (!Uri.IsHexDigit(ch)) return false;
            hex.Append(char.ToUpperInvariant(ch));
        }

        if (hex.Length != 12) return false;
        string s = hex.ToString();
        if (s == "000000000000" || s == "FFFFFFFFFFFF") return false;

        byte[] bytes = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            if (!byte.TryParse(s.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return false;
            }
        }

        if ((bytes[0] & 1) != 0) return false;

        macBytes = bytes;
        return true;
    }

    public static string? NormalizeMacAddress(string? raw)
    {
        if (!TryParseMacAddress(raw, out byte[] bytes)) return null;
        return string.Join(':', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }

    public static byte[] BuildMagicPacket(byte[] macBytes)
    {
        if (macBytes == null || macBytes.Length != 6)
        {
            throw new ArgumentException("Invalid MAC address length.", nameof(macBytes));
        }

        byte[] packet = new byte[102];
        for (int i = 0; i < 6; i++)
        {
            packet[i] = 0xFF;
        }

        for (int i = 0; i < 16; i++)
        {
            Buffer.BlockCopy(macBytes, 0, packet, 6 + i * 6, 6);
        }

        return packet;
    }

    public static bool IsPrivateOrBroadcastIpv4(IPAddress addr)
    {
        if (addr.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = addr.GetAddressBytes();
        if (b[0] == 255 && b[1] == 255 && b[2] == 255 && b[3] == 255) return true;
        if (b[0] == 10) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
        return false;
    }

    public static LocalPowerProfile CollectLocalPowerProfile(bool allowRemotePower)
    {
        var macs = new List<string>();
        var macSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var broadcasts = new List<string>();
        var bcastSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adapterDescriptions = new List<string>();

        try
        {
            var physicalNics = GetPhysicalNetworkInterfaces();
            foreach (var nic in physicalNics)
            {
                string? mac = FormatPhysicalAddress(nic.GetPhysicalAddress());
                if (!string.IsNullOrEmpty(mac) && macSet.Add(mac))
                {
                    macs.Add(mac);
                    if (!string.IsNullOrWhiteSpace(nic.Description))
                    {
                        adapterDescriptions.Add(nic.Description.Trim());
                    }
                }

                if (nic.OperationalStatus == OperationalStatus.Up)
                {
                    try
                    {
                        var ipProps = nic.GetIPProperties();
                        foreach (var uni in ipProps.UnicastAddresses)
                        {
                            if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            if (IPAddress.IsLoopback(uni.Address)) continue;
                            var mask = uni.IPv4Mask;
                            if (mask == null || mask.Equals(IPAddress.Any)) continue;

                            IPAddress bcast = ComputeSubnetBroadcast(uni.Address, mask);
                            if (IsPrivateOrBroadcastIpv4(bcast))
                            {
                                string bStr = bcast.ToString();
                                if (bcastSet.Add(bStr) && broadcasts.Count < 8)
                                {
                                    broadcasts.Add(bStr);
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                if (macs.Count >= 6) break;
            }
        }
        catch
        {
        }

        bool wolReady = macs.Count > 0 && DetectLocalWolReadiness(adapterDescriptions);
        string summary = adapterDescriptions.Count > 0 ? adapterDescriptions[0] : string.Empty;

        return new LocalPowerProfile
        {
            AllowRemotePower = allowRemotePower,
            MacAddresses = macs,
            BroadcastAddresses = broadcasts,
            WolReady = wolReady,
            AdapterSummary = summary
        };
    }

    public static async Task<WakeOnLanSendReport> SendMagicPacketsAsync(
        IEnumerable<string> macAddresses,
        IEnumerable<string>? targetBroadcasts = null,
        IEnumerable<int>? ports = null,
        int repeatCount = 3,
        CancellationToken ct = default)
    {
        var validMacBytes = new List<byte[]>();
        var seenMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in macAddresses)
        {
            if (TryParseMacAddress(raw, out byte[] bytes))
            {
                string key = string.Join(':', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
                if (seenMacs.Add(key))
                {
                    validMacBytes.Add(bytes);
                    if (validMacBytes.Count >= 6) break;
                }
            }
        }

        if (validMacBytes.Count == 0)
        {
            return new WakeOnLanSendReport { PacketsSent = 0, MacsCount = 0 };
        }

        var validPorts = new List<int>();
        if (ports != null)
        {
            foreach (int p in ports)
            {
                if (p is > 0 and <= 65535 && !validPorts.Contains(p))
                {
                    validPorts.Add(p);
                }
            }
        }

        if (validPorts.Count == 0)
        {
            validPorts.Add(9);
            validPorts.Add(7);
        }

        var extraBroadcastIps = new List<IPAddress>();
        var extraIpSet = new HashSet<string>(StringComparer.Ordinal);
        if (targetBroadcasts != null)
        {
            foreach (string rawIp in targetBroadcasts)
            {
                if (IPAddress.TryParse(rawIp?.Trim(), out var ip) &&
                    IsPrivateOrBroadcastIpv4(ip) &&
                    extraIpSet.Add(ip.ToString()))
                {
                    extraBroadcastIps.Add(ip);
                }
            }
        }

        var localInterfaces = new List<(IPAddress LocalIp, IPAddress BroadcastIp)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                try
                {
                    foreach (var uni in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(uni.Address)) continue;
                        IPAddress bcast = uni.IPv4Mask != null && !uni.IPv4Mask.Equals(IPAddress.Any)
                            ? ComputeSubnetBroadcast(uni.Address, uni.IPv4Mask)
                            : IPAddress.Broadcast;
                        localInterfaces.Add((uni.Address, bcast));
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        int totalSent = 0;
        int bursts = Math.Clamp(repeatCount, 1, 5);

        for (int burst = 0; burst < bursts; burst++)
        {
            ct.ThrowIfCancellationRequested();

            foreach (byte[] mac in validMacBytes)
            {
                byte[] packet = BuildMagicPacket(mac);

                foreach (var (localIp, subnetBcast) in localInterfaces)
                {
                    try
                    {
                        using var boundClient = new UdpClient(new IPEndPoint(localIp, 0));
                        boundClient.EnableBroadcast = true;
                        foreach (int port in validPorts)
                        {
                            try
                            {
                                await boundClient.SendAsync(packet, packet.Length, new IPEndPoint(subnetBcast, port));
                                totalSent++;
                            }
                            catch
                            {
                            }

                            if (!subnetBcast.Equals(IPAddress.Broadcast))
                            {
                                try
                                {
                                    await boundClient.SendAsync(packet, packet.Length,
                                        new IPEndPoint(IPAddress.Broadcast, port));
                                    totalSent++;
                                }
                                catch
                                {
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                try
                {
                    using var fallbackClient = new UdpClient(AddressFamily.InterNetwork);
                    fallbackClient.EnableBroadcast = true;
                    foreach (int port in validPorts)
                    {
                        try
                        {
                            await fallbackClient.SendAsync(packet, packet.Length,
                                new IPEndPoint(IPAddress.Broadcast, port));
                            totalSent++;
                        }
                        catch
                        {
                        }

                        foreach (var bcastIp in extraBroadcastIps)
                        {
                            if (bcastIp.Equals(IPAddress.Broadcast)) continue;
                            try
                            {
                                await fallbackClient.SendAsync(packet, packet.Length, new IPEndPoint(bcastIp, port));
                                totalSent++;
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            if (burst + 1 < bursts)
            {
                await Task.Delay(80, ct);
            }
        }

        return new WakeOnLanSendReport
        {
            PacketsSent = totalSent,
            MacsCount = validMacBytes.Count
        };
    }

    public static bool HasWolBackup()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(OsmiumMachineRegPath, writable: false);
            string? raw = key?.GetValue(WolBackupRegValueName) as string;
            return !string.IsNullOrWhiteSpace(raw);
        }
        catch
        {
            return false;
        }
    }

    public static WolConfigurationResult ConfigureLocalWindowsWol()
    {
        int configured = 0;
        bool fastStartupDisabled = false;
        var physicalDescs = GetPhysicalNetworkInterfaces()
            .Select(n => n.Description?.Trim() ?? string.Empty)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingSnapshot = LoadWolBackupSnapshot();
        var snapshot = existingSnapshot ?? new WolSystemBackupSnapshot
        {
            Version = 1,
            CapturedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        bool snapshotDirty = existingSnapshot == null;

        var originallyArmedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string armedOut = RunCommandCapture("powercfg.exe", "/devicequery wake_armed", 4000);
            foreach (string line in armedOut.Split(['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(line) && !line.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                {
                    originallyArmedSet.Add(line);
                }
            }
        }
        catch
        {
        }

        var devicesToEnable = new List<string>();
        try
        {
            string programmableOutput = RunCommandCapture("powercfg.exe", "/devicequery wake_programmable", 4000);
            var lines = programmableOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (string devName in lines)
            {
                if (string.IsNullOrWhiteSpace(devName) || devName.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool matchesPhysical = physicalDescs.Any(desc =>
                    devName.Contains(desc, StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains(devName, StringComparison.OrdinalIgnoreCase));
                if (!matchesPhysical) continue;

                devicesToEnable.Add(devName);
                if (!snapshot.WakeDevices.Any(w => w.DeviceName.Equals(devName, StringComparison.OrdinalIgnoreCase)))
                {
                    snapshot.WakeDevices.Add(new WolWakeDeviceSnapshot
                    {
                        DeviceName = devName,
                        WasOriginallyArmed = originallyArmedSet.Contains(devName)
                    });
                    snapshotDirty = true;
                }
            }
        }
        catch
        {
        }

        var adapterSubKeysToUpdate = new List<string>();
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(NetAdapterClassRegPath, writable: false);
            if (classKey != null)
            {
                foreach (string subName in classKey.GetSubKeyNames())
                {
                    if (subName.Length != 4 || !subName.All(char.IsDigit)) continue;
                    try
                    {
                        using var adapterKey = classKey.OpenSubKey(subName, writable: false);
                        if (adapterKey == null) continue;

                        string driverDesc = adapterKey.GetValue("DriverDesc")?.ToString()?.Trim() ?? string.Empty;
                        if (string.IsNullOrEmpty(driverDesc)) continue;

                        bool matchesPhysical = physicalDescs.Any(desc =>
                            driverDesc.Equals(desc, StringComparison.OrdinalIgnoreCase) ||
                            driverDesc.Contains(desc, StringComparison.OrdinalIgnoreCase) ||
                            desc.Contains(driverDesc, StringComparison.OrdinalIgnoreCase));
                        if (!matchesPhysical) continue;

                        adapterSubKeysToUpdate.Add(subName);
                        if (!snapshot.Adapters.Any(a =>
                                a.SubKeyName.Equals(subName, StringComparison.OrdinalIgnoreCase)))
                        {
                            snapshot.Adapters.Add(new WolAdapterRegistrySnapshot
                            {
                                SubKeyName = subName,
                                DriverDesc = driverDesc,
                                StarWakeOnMagicPacket = CaptureRegistryValue(adapterKey, "*WakeOnMagicPacket"),
                                WakeOnMagicPacket = CaptureRegistryValue(adapterKey, "WakeOnMagicPacket"),
                                PnPCapabilities = CaptureRegistryValue(adapterKey, "PnPCapabilities")
                            });
                            snapshotDirty = true;
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }

        try
        {
            using var powerReadKey = Registry.LocalMachine.OpenSubKey(PowerSessionManagerRegPath, writable: false);
            if (powerReadKey != null && snapshot.HiberbootEnabled == null)
            {
                snapshot.HiberbootEnabled = CaptureRegistryValue(powerReadKey, "HiberbootEnabled");
                snapshotDirty = true;
            }
        }
        catch
        {
        }

        if (snapshotDirty &&
            (snapshot.WakeDevices.Count > 0 || snapshot.Adapters.Count > 0 || snapshot.HiberbootEnabled != null))
        {
            SaveWolBackupSnapshot(snapshot);
        }

        foreach (string devName in devicesToEnable)
        {
            try
            {
                string safeName = devName.Replace("\"", string.Empty);
                if (RunCommandExitCode("powercfg.exe", $"/deviceenablewake \"{safeName}\"", 4000) == 0)
                {
                    configured++;
                }
            }
            catch
            {
            }
        }

        foreach (string subName in adapterSubKeysToUpdate)
        {
            try
            {
                using var adapterKey = Registry.LocalMachine.OpenSubKey($@"{NetAdapterClassRegPath}\{subName}",
                    writable: true);
                if (adapterKey == null) continue;

                bool modified = false;
                if (adapterKey.GetValue("*WakeOnMagicPacket") != null)
                {
                    adapterKey.SetValue("*WakeOnMagicPacket", "1", RegistryValueKind.String);
                    modified = true;
                }

                if (adapterKey.GetValue("WakeOnMagicPacket") != null)
                {
                    adapterKey.SetValue("WakeOnMagicPacket", "1", RegistryValueKind.String);
                    modified = true;
                }

                if (adapterKey.GetValue("PnPCapabilities") is int pnp)
                {
                    int nextPnp = pnp & ~0x100;
                    if (nextPnp != pnp)
                    {
                        adapterKey.SetValue("PnPCapabilities", nextPnp, RegistryValueKind.DWord);
                        modified = true;
                    }
                }
                else
                {
                    adapterKey.SetValue("PnPCapabilities", 0, RegistryValueKind.DWord);
                    modified = true;
                }

                if (modified) configured++;
            }
            catch
            {
            }
        }

        try
        {
            using var powerKey = Registry.LocalMachine.OpenSubKey(PowerSessionManagerRegPath, writable: true);
            if (powerKey != null)
            {
                powerKey.SetValue("HiberbootEnabled", 0, RegistryValueKind.DWord);
                fastStartupDisabled = true;
            }
        }
        catch
        {
        }

        bool success = configured > 0 || fastStartupDisabled;
        if (!success && existingSnapshot == null)
        {
            DeleteWolBackupSnapshot();
        }

        return new WolConfigurationResult
        {
            Success = success,
            ConfiguredAdapters = configured,
            FastStartupDisabled = fastStartupDisabled
        };
    }

    public static WolRestoreResult RestoreLocalWindowsWol(bool allowDefaultFallback = false)
    {
        var snapshot = LoadWolBackupSnapshot();
        int restoredAdapters = 0;
        bool fastStartupRestored = false;

        if (snapshot != null)
        {
            bool anyError = false;

            foreach (var adapter in snapshot.Adapters)
            {
                if (string.IsNullOrWhiteSpace(adapter.SubKeyName)) continue;
                try
                {
                    using var adapterKey = Registry.LocalMachine.OpenSubKey(
                        $@"{NetAdapterClassRegPath}\{adapter.SubKeyName}", writable: true);
                    if (adapterKey == null) continue;

                    if (adapter.StarWakeOnMagicPacket != null)
                    {
                        RestoreRegistryValue(adapterKey, "*WakeOnMagicPacket", adapter.StarWakeOnMagicPacket);
                    }

                    if (adapter.WakeOnMagicPacket != null)
                    {
                        RestoreRegistryValue(adapterKey, "WakeOnMagicPacket", adapter.WakeOnMagicPacket);
                    }

                    if (adapter.PnPCapabilities != null)
                    {
                        RestoreRegistryValue(adapterKey, "PnPCapabilities", adapter.PnPCapabilities);
                    }

                    restoredAdapters++;
                }
                catch
                {
                    anyError = true;
                }
            }

            foreach (var wakeDev in snapshot.WakeDevices)
            {
                if (string.IsNullOrWhiteSpace(wakeDev.DeviceName)) continue;
                try
                {
                    string safeName = wakeDev.DeviceName.Replace("\"", string.Empty);
                    string flag = wakeDev.WasOriginallyArmed ? "/deviceenablewake" : "/devicedisablewake";
                    if (RunCommandExitCode("powercfg.exe", $"{flag} \"{safeName}\"", 4000) == 0)
                    {
                        restoredAdapters++;
                    }
                }
                catch
                {
                }
            }

            if (snapshot.HiberbootEnabled != null)
            {
                try
                {
                    using var powerKey = Registry.LocalMachine.OpenSubKey(PowerSessionManagerRegPath, writable: true);
                    if (powerKey != null)
                    {
                        RestoreRegistryValue(powerKey, "HiberbootEnabled", snapshot.HiberbootEnabled);
                        fastStartupRestored = true;
                    }
                }
                catch
                {
                    anyError = true;
                }
            }

            if (!anyError)
            {
                DeleteWolBackupSnapshot();
            }

            return new WolRestoreResult
            {
                Success = !anyError,
                HadBackup = true,
                RestoredAdapters = restoredAdapters,
                FastStartupRestored = fastStartupRestored
            };
        }

        if (!allowDefaultFallback)
        {
            return new WolRestoreResult
            {
                Success = true,
                HadBackup = false,
                RestoredAdapters = 0,
                FastStartupRestored = false
            };
        }

        var physicalDescs = GetPhysicalNetworkInterfaces()
            .Select(n => n.Description?.Trim() ?? string.Empty)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            string armedOutput = RunCommandCapture("powercfg.exe", "/devicequery wake_armed", 4000);
            foreach (string devName in armedOutput.Split(['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.IsNullOrWhiteSpace(devName) || devName.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool matchesPhysical = physicalDescs.Any(desc =>
                    devName.Contains(desc, StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains(devName, StringComparison.OrdinalIgnoreCase));
                if (!matchesPhysical) continue;

                string safeName = devName.Replace("\"", string.Empty);
                if (RunCommandExitCode("powercfg.exe", $"/devicedisablewake \"{safeName}\"", 4000) == 0)
                {
                    restoredAdapters++;
                }
            }
        }
        catch
        {
        }

        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(NetAdapterClassRegPath, writable: false);
            if (classKey != null)
            {
                foreach (string subName in classKey.GetSubKeyNames())
                {
                    if (subName.Length != 4 || !subName.All(char.IsDigit)) continue;
                    try
                    {
                        using var adapterKey = Registry.LocalMachine.OpenSubKey(
                            $@"{NetAdapterClassRegPath}\{subName}", writable: true);
                        if (adapterKey == null) continue;

                        string driverDesc = adapterKey.GetValue("DriverDesc")?.ToString()?.Trim() ?? string.Empty;
                        if (string.IsNullOrEmpty(driverDesc)) continue;

                        bool matchesPhysical = physicalDescs.Any(desc =>
                            driverDesc.Equals(desc, StringComparison.OrdinalIgnoreCase) ||
                            driverDesc.Contains(desc, StringComparison.OrdinalIgnoreCase) ||
                            desc.Contains(driverDesc, StringComparison.OrdinalIgnoreCase));
                        if (!matchesPhysical) continue;

                        bool modified = false;
                        if (adapterKey.GetValue("*WakeOnMagicPacket") != null)
                        {
                            adapterKey.SetValue("*WakeOnMagicPacket", "0", RegistryValueKind.String);
                            modified = true;
                        }

                        if (adapterKey.GetValue("WakeOnMagicPacket") != null)
                        {
                            adapterKey.SetValue("WakeOnMagicPacket", "0", RegistryValueKind.String);
                            modified = true;
                        }

                        if (adapterKey.GetValue("PnPCapabilities") is int pnp)
                        {
                            int nextPnp = pnp | 0x100;
                            if (nextPnp != pnp)
                            {
                                adapterKey.SetValue("PnPCapabilities", nextPnp, RegistryValueKind.DWord);
                                modified = true;
                            }
                        }

                        if (modified) restoredAdapters++;
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }

        try
        {
            using var powerKey = Registry.LocalMachine.OpenSubKey(PowerSessionManagerRegPath, writable: true);
            if (powerKey != null)
            {
                powerKey.SetValue("HiberbootEnabled", 1, RegistryValueKind.DWord);
                fastStartupRestored = true;
            }
        }
        catch
        {
        }

        DeleteWolBackupSnapshot();

        return new WolRestoreResult
        {
            Success = restoredAdapters > 0 || fastStartupRestored,
            HadBackup = false,
            RestoredAdapters = restoredAdapters,
            FastStartupRestored = fastStartupRestored
        };
    }

    private static WolRegistryValueSnapshot CaptureRegistryValue(RegistryKey key, string valueName)
    {
        object? raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw == null)
        {
            return new WolRegistryValueSnapshot
            {
                Existed = false
            };
        }

        RegistryValueKind kind = key.GetValueKind(valueName);
        var snap = new WolRegistryValueSnapshot
        {
            Existed = true,
            Kind = (int)kind
        };

        switch (kind)
        {
            case RegistryValueKind.DWord:
                snap.DwordValue = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                break;
            case RegistryValueKind.QWord:
                snap.QwordValue = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                break;
            case RegistryValueKind.Binary:
                if (raw is byte[] bytes)
                {
                    snap.BinaryBase64 = Convert.ToBase64String(bytes);
                }

                break;
            case RegistryValueKind.MultiString:
                if (raw is string[] arr)
                {
                    snap.MultiStringValue = arr.ToList();
                }

                break;
            default:
                snap.StringValue = raw.ToString();
                break;
        }

        return snap;
    }

    private static void RestoreRegistryValue(RegistryKey key, string valueName, WolRegistryValueSnapshot snap)
    {
        if (!snap.Existed)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
            return;
        }

        var kind = (RegistryValueKind)snap.Kind;
        switch (kind)
        {
            case RegistryValueKind.DWord:
                key.SetValue(valueName, snap.DwordValue ?? 0, RegistryValueKind.DWord);
                break;
            case RegistryValueKind.QWord:
                key.SetValue(valueName, snap.QwordValue ?? 0L, RegistryValueKind.QWord);
                break;
            case RegistryValueKind.Binary:
                byte[] bin = string.IsNullOrEmpty(snap.BinaryBase64)
                    ? Array.Empty<byte>()
                    : Convert.FromBase64String(snap.BinaryBase64);
                key.SetValue(valueName, bin, RegistryValueKind.Binary);
                break;
            case RegistryValueKind.MultiString:
                key.SetValue(valueName, snap.MultiStringValue?.ToArray() ?? Array.Empty<string>(),
                    RegistryValueKind.MultiString);
                break;
            case RegistryValueKind.ExpandString:
                key.SetValue(valueName, snap.StringValue ?? string.Empty, RegistryValueKind.ExpandString);
                break;
            default:
                key.SetValue(valueName, snap.StringValue ?? string.Empty, RegistryValueKind.String);
                break;
        }
    }

    private static WolSystemBackupSnapshot? LoadWolBackupSnapshot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(OsmiumMachineRegPath, writable: false);
            string? raw = key?.GetValue(WolBackupRegValueName) as string;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return JsonSerializer.Deserialize<WolSystemBackupSnapshot>(raw);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveWolBackupSnapshot(WolSystemBackupSnapshot snapshot)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(OsmiumMachineRegPath, writable: true);
            if (key == null) return;
            string json = JsonSerializer.Serialize(snapshot);
            key.SetValue(WolBackupRegValueName, json, RegistryValueKind.String);
        }
        catch
        {
        }
    }

    private static void DeleteWolBackupSnapshot()
    {
        try
        {
            bool shouldRemoveKey = false;
            using (var key = Registry.LocalMachine.OpenSubKey(OsmiumMachineRegPath, writable: true))
            {
                if (key == null) return;
                key.DeleteValue(WolBackupRegValueName, throwOnMissingValue: false);
                shouldRemoveKey = key.ValueCount == 0 && key.SubKeyCount == 0;
            }

            if (shouldRemoveKey)
            {
                Registry.LocalMachine.DeleteSubKey(OsmiumMachineRegPath, throwOnMissingSubKey: false);
            }
        }
        catch
        {
        }
    }

    private static List<NetworkInterface> GetPhysicalNetworkInterfaces()
    {
        var all = NetworkInterface.GetAllNetworkInterfaces();
        var result = new List<(int Priority, NetworkInterface Nic)>();

        foreach (var nic in all)
        {
            if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx
                or NetworkInterfaceType.Ethernet3Megabit
                or NetworkInterfaceType.Wireless80211))
            {
                continue;
            }

            string combined = $"{nic.Name} {nic.Description}".ToLowerInvariant();
            if (VirtualAdapterTokens.Any(tok => combined.Contains(tok, StringComparison.Ordinal)))
            {
                continue;
            }

            if (FormatPhysicalAddress(nic.GetPhysicalAddress()) == null)
            {
                continue;
            }

            bool isWifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            bool isUp = nic.OperationalStatus == OperationalStatus.Up;
            int priority = (isUp ? 0 : 10) + (isWifi ? 2 : 0);
            result.Add((priority, nic));
        }

        return result.OrderBy(x => x.Priority).Select(x => x.Nic).ToList();
    }

    private static string? FormatPhysicalAddress(PhysicalAddress? addr)
    {
        if (addr == null) return null;
        byte[] bytes = addr.GetAddressBytes();
        if (bytes.Length != 6) return null;
        if (bytes.All(b => b == 0) || bytes.All(b => b == 0xFF)) return null;
        if ((bytes[0] & 1) != 0) return null;
        return string.Join(':', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static IPAddress ComputeSubnetBroadcast(IPAddress address, IPAddress mask)
    {
        byte[] ipBytes = address.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        byte[] bcast = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            bcast[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
        }

        return new IPAddress(bcast);
    }

    private static bool DetectLocalWolReadiness(List<string> physicalDescriptions)
    {
        if (physicalDescriptions.Count == 0) return false;

        try
        {
            string armedOut = RunCommandCapture("powercfg.exe", "/devicequery wake_armed", 2500);
            if (!string.IsNullOrWhiteSpace(armedOut))
            {
                var lines = armedOut.Split(['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (string line in lines)
                {
                    if (line.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;
                    if (physicalDescriptions.Any(desc =>
                            line.Contains(desc, StringComparison.OrdinalIgnoreCase) ||
                            desc.Contains(line, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
        }

        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(NetAdapterClassRegPath, writable: false);
            if (classKey == null) return false;

            foreach (string subName in classKey.GetSubKeyNames())
            {
                if (subName.Length != 4 || !subName.All(char.IsDigit)) continue;
                try
                {
                    using var adapterKey = classKey.OpenSubKey(subName, writable: false);
                    if (adapterKey == null) continue;

                    string desc = adapterKey.GetValue("DriverDesc")?.ToString()?.Trim() ?? string.Empty;
                    if (string.IsNullOrEmpty(desc)) continue;

                    bool matched = physicalDescriptions.Any(d =>
                        d.Equals(desc, StringComparison.OrdinalIgnoreCase) ||
                        d.Contains(desc, StringComparison.OrdinalIgnoreCase) ||
                        desc.Contains(d, StringComparison.OrdinalIgnoreCase));
                    if (!matched) continue;

                    string? wmp = adapterKey.GetValue("*WakeOnMagicPacket")?.ToString()
                                  ?? adapterKey.GetValue("WakeOnMagicPacket")?.ToString();
                    int pnp = adapterKey.GetValue("PnPCapabilities") is int p ? p : 0;
                    bool pnpAllowsWake = (pnp & 0x100) == 0;
                    if (string.Equals(wmp, "1", StringComparison.Ordinal) && pnpAllowsWake)
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static string RunCommandCapture(string fileName, string arguments, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return string.Empty;
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(timeoutMs);
        return output;
    }

    private static int RunCommandExitCode(string fileName, string arguments, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return -1;
        proc.WaitForExit(timeoutMs);
        return proc.HasExited ? proc.ExitCode : -1;
    }
}