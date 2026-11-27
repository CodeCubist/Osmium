using System.Collections.Concurrent;
using System.Text;
using Osmium.Core.Models;

namespace Osmium.Core.Services;

public sealed class DeviceWallpaperStore
{
    public const int MaxWallpaperBytes = 4 * 1024 * 1024;
    private const string FolderName = "device_wallpapers";

    private readonly string _directory;
    private readonly LoggingService? _log;
    private readonly object _ioLock = new();
    private readonly ConcurrentDictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _loaded;

    public event Action<string>? WallpaperChanged;

    public DeviceWallpaperStore(string dataDirectory, LoggingService? log = null)
    {
        _directory = Path.Combine(dataDirectory, FolderName);
        _log = log;
    }

    public string DirectoryPath => _directory;

    public string? GetWallpaperPath(string deviceId)
    {
        string normalized = NormalizeKey(deviceId);
        if (string.IsNullOrEmpty(normalized)) return null;

        EnsureLoaded();

        if (_paths.TryGetValue(normalized, out string? path))
        {
            if (File.Exists(path))
            {
                return path;
            }

            _paths.TryRemove(normalized, out _);
        }

        return null;
    }

    public string? SaveWallpaper(string deviceId, byte[] imageBytes)
    {
        string normalized = NormalizeKey(deviceId);
        if (string.IsNullOrEmpty(normalized) ||
            imageBytes == null ||
            imageBytes.Length == 0 ||
            imageBytes.Length > MaxWallpaperBytes)
        {
            return null;
        }

        string? ext = DetectImageExtension(imageBytes);
        if (ext == null)
        {
            return null;
        }

        EnsureLoaded();

        string targetPath;
        lock (_ioLock)
        {
            try
            {
                Directory.CreateDirectory(_directory);

                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string fileName = $"{normalized}_{ts}.{ext}";
                targetPath = Path.Combine(_directory, fileName);
                string tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";

                File.WriteAllBytes(tempPath, imageBytes);
                File.Move(tempPath, targetPath, overwrite: true);

                DeleteOtherDeviceFilesLocked(normalized, targetPath);
                _paths[normalized] = targetPath;
            }
            catch (Exception ex)
            {
                _log?.Warn($"Failed to save remote desktop wallpaper for {normalized}: {ex.Message}");
                return null;
            }
        }

        try
        {
            WallpaperChanged?.Invoke(normalized);
        }
        catch
        {
        }

        return targetPath;
    }

    public void RemoveWallpaper(string deviceId)
    {
        string normalized = NormalizeKey(deviceId);
        if (string.IsNullOrEmpty(normalized)) return;

        EnsureLoaded();

        bool removed;
        lock (_ioLock)
        {
            removed = _paths.TryRemove(normalized, out _);
            removed |= DeleteOtherDeviceFilesLocked(normalized, keepPath: null);
        }

        if (removed)
        {
            try
            {
                WallpaperChanged?.Invoke(normalized);
            }
            catch
            {
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_ioLock)
        {
            if (_loaded) return;
            try
            {
                if (Directory.Exists(_directory))
                {
                    var latestByDevice =
                        new Dictionary<string, (string Path, DateTime WriteUtc)>(StringComparer.OrdinalIgnoreCase);

                    foreach (string file in Directory.EnumerateFiles(_directory))
                    {
                        string fileName = Path.GetFileName(file);
                        if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        {
                            TryDeleteFile(file);
                            continue;
                        }

                        string? devKey = ExtractDeviceKeyFromFileName(fileName);
                        if (string.IsNullOrEmpty(devKey)) continue;

                        string ext = Path.GetExtension(fileName);
                        if (!IsSupportedExtension(ext)) continue;

                        DateTime writeUtc;
                        try
                        {
                            var info = new FileInfo(file);
                            if (!info.Exists || info.Length <= 0 || info.Length > MaxWallpaperBytes)
                            {
                                continue;
                            }

                            writeUtc = info.LastWriteTimeUtc;
                        }
                        catch
                        {
                            continue;
                        }

                        if (!latestByDevice.TryGetValue(devKey, out var existing))
                        {
                            latestByDevice[devKey] = (file, writeUtc);
                        }
                        else if (writeUtc > existing.WriteUtc ||
                                 (writeUtc == existing.WriteUtc &&
                                  string.CompareOrdinal(fileName, Path.GetFileName(existing.Path)) > 0))
                        {
                            TryDeleteFile(existing.Path);
                            latestByDevice[devKey] = (file, writeUtc);
                        }
                        else
                        {
                            TryDeleteFile(file);
                        }
                    }

                    foreach (var kv in latestByDevice)
                    {
                        _paths[kv.Key] = kv.Value.Path;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"Failed to scan cached device wallpapers: {ex.Message}");
            }
            finally
            {
                _loaded = true;
            }
        }
    }

    private bool DeleteOtherDeviceFilesLocked(string normalizedDeviceId, string? keepPath)
    {
        bool deletedAny = false;
        if (!Directory.Exists(_directory)) return false;

        string prefix = normalizedDeviceId + "_";
        try
        {
            foreach (string file in Directory.EnumerateFiles(_directory))
            {
                if (!string.IsNullOrEmpty(keepPath) &&
                    string.Equals(file, keepPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string name = Path.GetFileName(file);
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryDeleteFile(file))
                    {
                        deletedAny = true;
                    }
                }
            }
        }
        catch
        {
        }

        return deletedAny;
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static string NormalizeKey(string deviceId)
    {
        string normalized = DeviceIdentity.NormalizeDeviceId(deviceId);
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

        var sb = new StringBuilder(normalized.Length);
        foreach (char c in normalized)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string? ExtractDeviceKeyFromFileName(string fileName)
    {
        int sep = fileName.IndexOf('_');
        if (sep <= 0) return null;
        string raw = fileName[..sep];
        string normalized = NormalizeKey(raw);
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static bool IsSupportedExtension(string ext)
        => string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(ext, ".webp", StringComparison.OrdinalIgnoreCase);

    private static string? DetectImageExtension(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "jpg";
        }

        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return "png";
        }

        if (bytes.Length >= 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "webp";
        }

        return null;
    }
}