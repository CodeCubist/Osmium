using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Osmium.Core.Helpers;
using Osmium.Core.Models;

namespace Osmium.Core.Services;

public sealed class SettingsService
{
    public const string IdentityFileName = "osmium_identity.dat";
    private const string LegacyIdentityFileName = "identity.json";
    private static readonly byte[] IdentityEntropyBytes = Encoding.UTF8.GetBytes("Osmium.Device.Identity.v1");
    private static readonly byte[] ServiceIdentityEntropyBytes = Encoding.UTF8.GetBytes("Osmium.Device.ServiceIdentity.v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dataDir;
    private readonly string _settingsPath;
    private readonly string _identityPath;
    private readonly string _legacyIdentityPath;
    private readonly bool _isServiceHostMode;
    private readonly object _lock = new();

    public string DataDirectory => _dataDir;
    public bool IsServiceHostMode => _isServiceHostMode;

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

    public SettingsService(string? overrideDataDirectory = null, bool isServiceHostMode = false)
    {
        _isServiceHostMode = isServiceHostMode;
        if (!string.IsNullOrWhiteSpace(overrideDataDirectory))
        {
            _dataDir = overrideDataDirectory.Trim();
        }
        else
        {
            string exeDir = AppContext.BaseDirectory;
            bool portable = File.Exists(Path.Combine(exeDir, "portable.dat"));
            _dataDir = portable
                ? Path.Combine(exeDir, "OsmiumData")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Osmium");
        }

        Directory.CreateDirectory(_dataDir);
        _settingsPath = Path.Combine(_dataDir, "settings.json");
        _identityPath = Path.Combine(
            _dataDir,
            _isServiceHostMode ? ServiceAutoStartHelper.ServiceIdentityFileName : IdentityFileName);
        _legacyIdentityPath = Path.Combine(_dataDir, LegacyIdentityFileName);

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

            if (Settings.SettingsSchemaVersion < 2)
            {
                if (Settings.TargetWidth == 1920 && Settings.TargetHeight == 1080)
                {
                    Settings.TargetWidth = 0;
                    Settings.TargetHeight = 0;
                }

                Settings.SettingsSchemaVersion = 2;
            }

            DeviceIdentity? loadedIdentity = TryLoadEncryptedIdentity(_identityPath, _isServiceHostMode);
            if (loadedIdentity == null && File.Exists(_legacyIdentityPath))
            {
                loadedIdentity = TryLoadLegacyPlaintextIdentity(_legacyIdentityPath);
            }

            Identity = loadedIdentity ?? new DeviceIdentity();

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
            if (!_isServiceHostMode)
            {
                ServiceAutoStartHelper.SyncProfileFileToService(_settingsPath, "settings.json");
            }
        }
    }

    public void SaveIdentity()
    {
        lock (_lock)
        {
            byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Identity, JsonOptions));
            bool encryptedSaved = false;
            try
            {
                if (_isServiceHostMode)
                {
                    byte[]? svcCipher = DpapiStorageHelper.ProtectBytes(
                        plain,
                        ServiceIdentityEntropyBytes,
                        "OsmiumServiceIdentity",
                        localMachineScope: true);
                    if (svcCipher != null)
                    {
                        DpapiStorageHelper.WriteBytesAtomic(_identityPath, svcCipher);
                        encryptedSaved = true;
                    }
                }
                else
                {
                    byte[]? cipher = DpapiStorageHelper.ProtectBytes(
                        plain,
                        IdentityEntropyBytes,
                        "OsmiumDeviceIdentity",
                        localMachineScope: false);
                    if (cipher != null)
                    {
                        DpapiStorageHelper.WriteBytesAtomic(_identityPath, cipher);
                        encryptedSaved = true;
                    }

                    if (ServiceAutoStartHelper.IsServiceAutoStartEnabled())
                    {
                        byte[]? svcCipher = DpapiStorageHelper.ProtectBytes(
                            plain,
                            ServiceIdentityEntropyBytes,
                            "OsmiumServiceIdentity",
                            localMachineScope: true);
                        if (svcCipher != null)
                        {
                            ServiceAutoStartHelper.WriteServiceIdentityBytes(svcCipher);
                        }
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }

            if (encryptedSaved && File.Exists(_legacyIdentityPath))
            {
                DpapiStorageHelper.SecureDeleteFile(_legacyIdentityPath);
            }
        }
    }

    public void StageServiceIdentityForElevation()
    {
        if (_isServiceHostMode) return;
        lock (_lock)
        {
            byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Identity, JsonOptions));
            try
            {
                byte[]? svcCipher = DpapiStorageHelper.ProtectBytes(
                    plain,
                    ServiceIdentityEntropyBytes,
                    "OsmiumServiceIdentity",
                    localMachineScope: true);
                if (svcCipher != null)
                {
                    string stagedPath = Path.Combine(_dataDir, ServiceAutoStartHelper.ServiceIdentityFileName);
                    DpapiStorageHelper.WriteBytesAtomic(stagedPath, svcCipher);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
    }

    private static DeviceIdentity? TryLoadEncryptedIdentity(string path, bool isServiceHostMode)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] cipher = File.ReadAllBytes(path);
            if (cipher.Length == 0) return null;

            byte[]? plain = DpapiStorageHelper.UnprotectBytes(
                cipher,
                isServiceHostMode ? ServiceIdentityEntropyBytes : IdentityEntropyBytes);
            if (plain == null || plain.Length == 0) return null;

            string json;
            try
            {
                json = Encoding.UTF8.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }

            return JsonSerializer.Deserialize<DeviceIdentity>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static DeviceIdentity? TryLoadLegacyPlaintextIdentity(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<DeviceIdentity>(json, JsonOptions);
        }
        catch
        {
            return null;
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
}