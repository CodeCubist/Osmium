using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Osmium.Core.Models;

namespace Osmium.Core.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dataDir;
    private readonly string _settingsPath;
    private readonly string _identityPath;
    private readonly object _lock = new();

    public string DataDirectory => _dataDir;

    public OsmiumSettings Settings
    {
        get;
        private set;
    } = new();

    public DeviceIdentity Identity
    {
        get;
        private set;
    } = new();

    public event Action<string>? TemporaryAccessCodeChanged;

    public SettingsService()
    {
        string exeDir = AppContext.BaseDirectory;
        bool portable = File.Exists(Path.Combine(exeDir, "portable.dat"));
        _dataDir = portable
            ? Path.Combine(exeDir, "OsmiumData")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Osmium");

        Directory.CreateDirectory(_dataDir);
        _settingsPath = Path.Combine(_dataDir, "settings.json");
        _identityPath = Path.Combine(_dataDir, "identity.json");

        Load();
    }

    public void Load()
    {
        lock (_lock)
        {
            if (File.Exists(_settingsPath))
            {
                try
                {
                    string json = File.ReadAllText(_settingsPath);
                    Settings = JsonSerializer.Deserialize<OsmiumSettings>(json, JsonOptions) ?? new OsmiumSettings();
                }
                catch
                {
                    Settings = new OsmiumSettings();
                }
            }
            else
            {
                Settings = new OsmiumSettings();
            }

            if (File.Exists(_identityPath))
            {
                try
                {
                    string json = File.ReadAllText(_identityPath);
                    Identity = JsonSerializer.Deserialize<DeviceIdentity>(json, JsonOptions) ?? new DeviceIdentity();
                }
                catch
                {
                    Identity = new DeviceIdentity();
                }
            }
            else
            {
                Identity = new DeviceIdentity();
            }

            if (Identity.RefreshPolicy == AccessCodeRefreshPolicy.OnAppStartup)
            {
                Identity.TemporaryAccessCode = DeviceIdentity.GenerateAccessCode();
            }

            Identity.EnsureValid();
            SaveIdentity();
            SaveSettings();
        }
    }

    public void SaveSettings()
    {
        lock (_lock)
        {
            string json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
    }

    public void SaveIdentity()
    {
        lock (_lock)
        {
            string json = JsonSerializer.Serialize(Identity, JsonOptions);
            File.WriteAllText(_identityPath, json);
        }
    }

    public void RefreshTemporaryAccessCode()
    {
        string updated;
        lock (_lock)
        {
            string prev = Identity.TemporaryAccessCode;
            string next;
            int guard = 0;
            do
            {
                next = DeviceIdentity.GenerateAccessCode();
            } while (next == prev && guard++ < 4);

            Identity.TemporaryAccessCode = next;
            SaveIdentity();
            updated = next;
        }

        TemporaryAccessCodeChanged?.Invoke(updated);
    }

    public void RecordRecentSession(string targetDeviceId, string alias)
    {
        lock (_lock)
        {
            string normalized = DeviceIdentity.NormalizeDeviceId(targetDeviceId);
            if (string.IsNullOrEmpty(normalized)) return;

            Settings.RecentSessions.RemoveAll(r => r.DeviceId == normalized);
            Settings.RecentSessions.Insert(0, new RecentSessionRecord
            {
                DeviceId = normalized,
                Alias = string.IsNullOrWhiteSpace(alias) ? $"Osmium-{normalized[^4..]}" : alias,
                LastConnectedAt = DateTimeOffset.UtcNow
            });
            if (Settings.RecentSessions.Count > 12)
            {
                Settings.RecentSessions.RemoveRange(12, Settings.RecentSessions.Count - 12);
            }

            SaveSettings();
        }
    }

    public void RemoveRecentSession(string targetDeviceId)
    {
        lock (_lock)
        {
            string normalized = DeviceIdentity.NormalizeDeviceId(targetDeviceId);
            if (Settings.RecentSessions.RemoveAll(r => r.DeviceId == normalized) > 0)
            {
                SaveSettings();
            }
        }
    }

    public void ClearRecentSessions()
    {
        lock (_lock)
        {
            if (Settings.RecentSessions.Count > 0)
            {
                Settings.RecentSessions.Clear();
                SaveSettings();
            }
        }
    }

    public bool HasAutoUnlockSecret(string targetDeviceId)
    {
        lock (_lock)
        {
            string normalized = DeviceIdentity.NormalizeDeviceId(targetDeviceId);
            return !string.IsNullOrEmpty(normalized)
                   && Settings.AutoUnlockCredentials.TryGetValue(normalized, out var cipher)
                   && !string.IsNullOrWhiteSpace(cipher);
        }
    }

    public string? GetAutoUnlockSecret(string targetDeviceId)
    {
        lock (_lock)
        {
            string normalized = DeviceIdentity.NormalizeDeviceId(targetDeviceId);
            if (string.IsNullOrEmpty(normalized) ||
                !Settings.AutoUnlockCredentials.TryGetValue(normalized, out var cipherBase64) ||
                string.IsNullOrWhiteSpace(cipherBase64))
            {
                return null;
            }

            try
            {
                byte[] cipher = Convert.FromBase64String(cipherBase64);
                byte[]? plain = UnprotectBytes(cipher);
                if (plain == null || plain.Length == 0) return null;
                try
                {
                    return Encoding.UTF8.GetString(plain);
                }
                finally
                {
                    Array.Clear(plain, 0, plain.Length);
                }
            }
            catch
            {
                return null;
            }
        }
    }

    public void SetAutoUnlockSecret(string targetDeviceId, string? secret)
    {
        lock (_lock)
        {
            string normalized = DeviceIdentity.NormalizeDeviceId(targetDeviceId);
            if (string.IsNullOrEmpty(normalized)) return;

            if (string.IsNullOrEmpty(secret))
            {
                if (Settings.AutoUnlockCredentials.Remove(normalized))
                {
                    SaveSettings();
                }

                return;
            }

            byte[] plain = Encoding.UTF8.GetBytes(secret);
            try
            {
                byte[]? cipher = ProtectBytes(plain);
                if (cipher != null)
                {
                    Settings.AutoUnlockCredentials[normalized] = Convert.ToBase64String(cipher);
                    SaveSettings();
                }
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private const uint CryptProtectUiForbidden = 0x01;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        in DataBlob dataIn,
        string? dataDescr,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        in DataBlob dataIn,
        IntPtr dataDescr,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static unsafe byte[]? ProtectBytes(byte[] plain)
    {
        if (plain.Length == 0) return [];
        fixed (byte* p = plain)
        {
            var blobIn = new DataBlob { Size = plain.Length, Data = (IntPtr)p };
            if (!CryptProtectData(in blobIn, "OsmiumUnlockSecret", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var blobOut))
            {
                return null;
            }

            try
            {
                byte[] result = new byte[blobOut.Size];
                Marshal.Copy(blobOut.Data, result, 0, blobOut.Size);
                return result;
            }
            finally
            {
                if (blobOut.Data != IntPtr.Zero) LocalFree(blobOut.Data);
            }
        }
    }

    private static unsafe byte[]? UnprotectBytes(byte[] cipher)
    {
        if (cipher.Length == 0) return [];
        fixed (byte* p = cipher)
        {
            var blobIn = new DataBlob { Size = cipher.Length, Data = (IntPtr)p };
            if (!CryptUnprotectData(in blobIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var blobOut))
            {
                return null;
            }

            try
            {
                byte[] result = new byte[blobOut.Size];
                Marshal.Copy(blobOut.Data, result, 0, blobOut.Size);
                return result;
            }
            finally
            {
                if (blobOut.Data != IntPtr.Zero) LocalFree(blobOut.Data);
            }
        }
    }
}