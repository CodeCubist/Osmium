using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Win32;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Osmium.Core.Services;

public static class WindowsWallpaperHelper
{
    private const uint SpiGetDeskWallpaper = 0x0073;
    private const long MaxSourceFileBytes = 64L * 1024 * 1024;
    private const int DefaultMaxBytes = 384 * 1024;

    private static readonly object CacheLock = new();
    private static string? _cachedPath;
    private static long _cachedWriteTicks;
    private static long _cachedFileLength;
    private static byte[]? _cachedJpeg;

    public static string? ResolveCurrentDesktopWallpaperPath()
    {
        string? spiPath = QuerySystemParametersWallpaperPath();
        if (!string.IsNullOrWhiteSpace(spiPath))
        {
            if (File.Exists(spiPath))
            {
                return spiPath;
            }

            string? transcoded = GetTranscodedWallpaperPath();
            if (!string.IsNullOrEmpty(transcoded) && File.Exists(transcoded))
            {
                return transcoded;
            }
        }

        string? regPath = QueryRegistryWallpaperPath();
        if (!string.IsNullOrWhiteSpace(regPath))
        {
            if (File.Exists(regPath))
            {
                return regPath;
            }

            string? transcoded = GetTranscodedWallpaperPath();
            if (!string.IsNullOrEmpty(transcoded) && File.Exists(transcoded))
            {
                return transcoded;
            }
        }

        return null;
    }

    public static async Task<byte[]?> TryGetCompressedWallpaperJpegAsync(
        int maxWidth = 960,
        int maxHeight = 540,
        int maxBytes = DefaultMaxBytes,
        CancellationToken ct = default)
    {
        try
        {
            string? path = ResolveCurrentDesktopWallpaperPath();
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxSourceFileBytes)
            {
                return null;
            }

            long writeTicks = info.LastWriteTimeUtc.Ticks;
            long fileLen = info.Length;

            lock (CacheLock)
            {
                if (_cachedJpeg != null &&
                    string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase) &&
                    _cachedWriteTicks == writeTicks &&
                    _cachedFileLength == fileLen &&
                    _cachedJpeg.Length <= maxBytes)
                {
                    return _cachedJpeg;
                }
            }

            byte[] rawBytes;
            await using (var fs = new FileStream(
                             path,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length <= 0 || fs.Length > MaxSourceFileBytes)
                {
                    return null;
                }

                rawBytes = new byte[fs.Length];
                int offset = 0;
                while (offset < rawBytes.Length)
                {
                    int read = await fs.ReadAsync(rawBytes.AsMemory(offset, rawBytes.Length - offset), ct);
                    if (read <= 0) break;
                    offset += read;
                }

                if (offset <= 0) return null;
                if (offset != rawBytes.Length)
                {
                    Array.Resize(ref rawBytes, offset);
                }
            }

            ct.ThrowIfCancellationRequested();

            byte[]? jpeg = await EncodeScaledJpegAsync(rawBytes, maxWidth, maxHeight, quality: 0.82);
            if (jpeg != null && jpeg.Length > maxBytes)
            {
                jpeg = await EncodeScaledJpegAsync(
                    rawBytes,
                    Math.Min(maxWidth, 720),
                    Math.Min(maxHeight, 405),
                    quality: 0.68);
            }

            if (jpeg == null || jpeg.Length == 0 || jpeg.Length > maxBytes)
            {
                return null;
            }

            lock (CacheLock)
            {
                _cachedPath = path;
                _cachedWriteTicks = writeTicks;
                _cachedFileLength = fileLen;
                _cachedJpeg = jpeg;
            }

            return jpeg;
        }
        catch
        {
            return null;
        }
    }

    public const string PrivacyWallpaperFileName = "privacy-wallpaper.jpg";

    public static async Task<byte[]?> ReadPrivacyWallpaperAsync(string sourcePath)
    {
        await using var source = File.OpenRead(sourcePath);
        if (source.Length <= 0 || source.Length > MaxSourceFileBytes) return null;

        var bytes = new byte[source.Length];
        await source.ReadExactlyAsync(bytes);
        return await EncodeScaledJpegAsync(bytes, 4096, 4096, quality: 0.92);
    }

    private static async Task<byte[]?> EncodeScaledJpegAsync(
        byte[] sourceBytes,
        int maxWidth,
        int maxHeight,
        double quality)
    {
        using var inStream = new InMemoryRandomAccessStream();
        await inStream.WriteAsync(sourceBytes.AsBuffer());
        inStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(inStream);
        uint rawW = decoder.PixelWidth;
        uint rawH = decoder.PixelHeight;
        uint orientedW = decoder.OrientedPixelWidth;
        uint orientedH = decoder.OrientedPixelHeight;
        if (rawW == 0 || rawH == 0 || orientedW == 0 || orientedH == 0)
        {
            return null;
        }

        bool swapped = orientedW != rawW && orientedW == rawH;
        double scale = Math.Min(1.0, Math.Min((double)maxWidth / orientedW, (double)maxHeight / orientedH));
        uint outW = Math.Max(1u, (uint)Math.Round(orientedW * scale));
        uint outH = Math.Max(1u, (uint)Math.Round(orientedH * scale));

        var transform = new BitmapTransform
        {
            ScaledWidth = swapped ? outH : outW,
            ScaledHeight = swapped ? outW : outH,
            InterpolationMode = BitmapInterpolationMode.Fant
        };

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        byte[] pixels = pixelData.DetachPixelData();
        if (pixels.Length < (long)outW * outH * 4)
        {
            return null;
        }

        using var outStream = new InMemoryRandomAccessStream();
        var props = new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue((float)quality, Windows.Foundation.PropertyType.Single) }
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream, props);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            outW,
            outH,
            96.0,
            96.0,
            pixels);
        await encoder.FlushAsync();

        outStream.Seek(0);
        uint size = (uint)outStream.Size;
        if (size == 0)
        {
            return null;
        }

        byte[] jpegBytes = new byte[size];
        using (var reader = new DataReader(outStream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync(size);
            reader.ReadBytes(jpegBytes);
        }

        return jpegBytes;
    }

    private static string? QuerySystemParametersWallpaperPath()
    {
        try
        {
            char[] buffer = new char[1024];
            if (!SystemParametersInfoW(SpiGetDeskWallpaper, (uint)buffer.Length, buffer, 0))
            {
                return null;
            }

            int nullIdx = Array.IndexOf(buffer, '\0');
            string path = nullIdx >= 0 ? new string(buffer, 0, nullIdx) : new string(buffer);
            path = path.Trim();
            return path.Length > 0 ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? QueryRegistryWallpaperPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            string? val = key?.GetValue("WallPaper") as string;
            val = val?.Trim();
            return string.IsNullOrEmpty(val) ? null : val;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetTranscodedWallpaperPath()
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData)) return null;
            return Path.Combine(appData, @"Microsoft\Windows\Themes\TranscodedWallpaper");
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(
        uint uiAction,
        uint uiParam,
        [Out] char[] pvParam,
        uint fWinIni);
}