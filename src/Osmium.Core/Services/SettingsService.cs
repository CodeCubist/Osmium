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

            if (Settings.SettingsSchemaVersion < 2)
            {
                if (Settings.TargetWidth == 1920 && Settings.TargetHeight == 1080)
                {
                    Settings.TargetWidth = 0;
                    Settings.TargetHeight = 0;
                }

                Settings.SettingsSchemaVersion = 2;
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
}