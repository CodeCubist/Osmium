using System.Collections.Concurrent;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Osmium.App.Helpers;

public static class WallpaperHelper
{
    private static readonly Dictionary<string, BitmapImage> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BitmapImage> VariantCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, byte[]>
        VariantPngCache = new(StringComparer.OrdinalIgnoreCase);

    public static string? ResolveDefaultWindowsWallpaperPath(bool isDarkTheme, bool preferSecondary = false)
    {
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(winDir)) winDir = @"C:\Windows";

        string[] lightCandidates = preferSecondary
            ?
            [
                Path.Combine(winDir, @"Web\Wallpaper\ThemeA\img20.jpg"),
                Path.Combine(winDir, @"Web\Wallpaper\ThemeB\img24.jpg"),
                Path.Combine(winDir, @"Web\Screen\img100.jpg"),
                Path.Combine(winDir, @"Web\Wallpaper\Windows\img0.jpg"),
                Path.Combine(winDir, @"Web\4K\Wallpaper\Windows\img0_1920x1200.jpg")
            ]
            :
            [
                Path.Combine(winDir, @"Web\Wallpaper\Windows\img0.jpg"),
                Path.Combine(winDir, @"Web\4K\Wallpaper\Windows\img0_1920x1200.jpg"),
                Path.Combine(winDir, @"Web\Wallpaper\ThemeA\img20.jpg"),
                Path.Combine(winDir, @"Web\Screen\img100.jpg")
            ];

        string[] darkCandidates = preferSecondary
            ?
            [
                Path.Combine(winDir, @"Web\Wallpaper\ThemeD\img32.jpg"),
                Path.Combine(winDir, @"Web\Wallpaper\Windows\img19.jpg"),
                Path.Combine(winDir, @"Web\4K\Wallpaper\Windows\img19_1920x1200.jpg"),
                Path.Combine(winDir, @"Web\Wallpaper\Windows\img0.jpg")
            ]
            :
            [
                Path.Combine(winDir, @"Web\Wallpaper\Windows\img19.jpg"),
                Path.Combine(winDir, @"Web\4K\Wallpaper\Windows\img19_1920x1200.jpg"),
                Path.Combine(winDir, @"Web\Wallpaper\Windows\img0.jpg"),
                Path.Combine(winDir, @"Web\Screen\img100.jpg")
            ];

        var candidates = isDarkTheme ? darkCandidates : lightCandidates;
        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        try
        {
            string webWallpaper = Path.Combine(winDir, @"Web\Wallpaper");
            if (Directory.Exists(webWallpaper))
            {
                var found = Directory.EnumerateFiles(webWallpaper, "*.jpg", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(found)) return found;
            }
        }
        catch
        {
        }

        return null;
    }

    public static BitmapImage? GetWallpaperImage(bool isDarkTheme, bool preferSecondary = false, int decodeWidth = 1080)
    {
        string? path = ResolveDefaultWindowsWallpaperPath(isDarkTheme, preferSecondary);
        if (string.IsNullOrEmpty(path)) return null;

        string key = $"{path}|{decodeWidth}";
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            var bmp = new BitmapImage
            {
                DecodePixelWidth = decodeWidth,
                UriSource = new Uri(path, UriKind.Absolute)
            };
            Cache[key] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static Task<BitmapImage?> GetGrayscaleWallpaperAsync(
        bool isDarkTheme,
        bool preferSecondary = false,
        int decodeWidth = 1280)
        => GetHeroWallpaperVariantAsync(isDarkTheme, grayscale: true, blur: false, preferSecondary, decodeWidth);

    public static async Task<BitmapImage?> GetHeroWallpaperVariantAsync(
        bool isDarkTheme,
        bool grayscale,
        bool blur,
        bool preferSecondary = false,
        int decodeWidth = 1280)
    {
        string? path = ResolveDefaultWindowsWallpaperPath(isDarkTheme, preferSecondary);
        if (string.IsNullOrEmpty(path)) return null;

        int effectiveWidth = blur ? Math.Min(decodeWidth, 640) : decodeWidth;
        int blurRadius = blur ? 14 : 0;
        string key = $"{path}|{effectiveWidth}|{(grayscale ? "bw" : "c")}|{(blur ? $"b{blurRadius}" : "s")}";

        if (VariantCache.TryGetValue(key, out var cachedImage))
        {
            return cachedImage;
        }

        if (!grayscale && !blur)
        {
            try
            {
                byte[] rawBytes = await File.ReadAllBytesAsync(path);
                if (rawBytes.Length > 0)
                {
                    using var stream = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(stream))
                    {
                        writer.WriteBytes(rawBytes);
                        await writer.StoreAsync();
                        await writer.FlushAsync();
                        writer.DetachStream();
                    }

                    stream.Seek(0);

                    var bmp = new BitmapImage
                    {
                        DecodePixelWidth = effectiveWidth
                    };
                    await bmp.SetSourceAsync(stream);
                    VariantCache[key] = bmp;
                    return bmp;
                }
            }
            catch
            {
            }

            return GetWallpaperImage(isDarkTheme, preferSecondary, decodeWidth);
        }

        try
        {
            if (!VariantPngCache.TryGetValue(key, out byte[]? pngBytes))
            {
                pngBytes = await Task.Run(() => BuildVariantPngAsync(path, effectiveWidth, grayscale, blurRadius));
                if (pngBytes == null || pngBytes.Length == 0)
                {
                    return null;
                }

                VariantPngCache[key] = pngBytes;
            }

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(pngBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);

            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            VariantCache[key] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]?> BuildVariantPngAsync(string path, int decodeWidth, bool grayscale,
        int blurRadius)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var inputStream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(inputStream);

        uint srcW = decoder.OrientedPixelWidth;
        uint srcH = decoder.OrientedPixelHeight;
        if (srcW == 0 || srcH == 0) return null;

        int targetW = Math.Clamp(decodeWidth, 64, (int)srcW);
        int targetH = Math.Max(1, (int)Math.Round(srcH * (targetW / (double)srcW)));

        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)targetW,
            ScaledHeight = (uint)targetH,
            InterpolationMode = BitmapInterpolationMode.Linear
        };

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        byte[] pixels = pixelData.DetachPixelData();
        int count = targetW * targetH;
        if (pixels.Length < count * 4)
        {
            return null;
        }

        if (grayscale)
        {
            ApplyGrayscale(pixels, count);
        }

        if (blurRadius > 0)
        {
            ApplySeparableBoxBlur(pixels, targetW, targetH, blurRadius, passes: 3);
        }

        using var outStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)targetW,
            (uint)targetH,
            96.0,
            96.0,
            pixels);
        await encoder.FlushAsync();

        outStream.Seek(0);
        uint size = (uint)outStream.Size;
        if (size == 0) return null;

        byte[] pngBytes = new byte[size];
        using (var reader = new DataReader(outStream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync(size);
            reader.ReadBytes(pngBytes);
        }

        return pngBytes;
    }

    private static void ApplyGrayscale(byte[] pixels, int pixelCount)
    {
        for (int i = 0; i < pixelCount; i++)
        {
            int idx = i * 4;
            byte b = pixels[idx];
            byte g = pixels[idx + 1];
            byte r = pixels[idx + 2];
            byte lum = (byte)Math.Clamp((int)(0.299 * r + 0.587 * g + 0.114 * b), 0, 255);
            pixels[idx] = lum;
            pixels[idx + 1] = lum;
            pixels[idx + 2] = lum;
            pixels[idx + 3] = 255;
        }
    }

    private static void ApplySeparableBoxBlur(byte[] pixels, int width, int height, int radius, int passes)
    {
        if (width <= 1 || height <= 1 || radius <= 0) return;

        var temp = new byte[pixels.Length];
        int diam = radius * 2 + 1;

        for (int pass = 0; pass < passes; pass++)
        {
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                int sumB = 0, sumG = 0, sumR = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int sx = Math.Clamp(k, 0, width - 1);
                    int idx = (row + sx) * 4;
                    sumB += pixels[idx];
                    sumG += pixels[idx + 1];
                    sumR += pixels[idx + 2];
                }

                for (int x = 0; x < width; x++)
                {
                    int dstIdx = (row + x) * 4;
                    temp[dstIdx] = (byte)(sumB / diam);
                    temp[dstIdx + 1] = (byte)(sumG / diam);
                    temp[dstIdx + 2] = (byte)(sumR / diam);
                    temp[dstIdx + 3] = 255;

                    int addIdx = (row + Math.Min(x + radius + 1, width - 1)) * 4;
                    int remIdx = (row + Math.Max(x - radius, 0)) * 4;
                    sumB += pixels[addIdx] - pixels[remIdx];
                    sumG += pixels[addIdx + 1] - pixels[remIdx + 1];
                    sumR += pixels[addIdx + 2] - pixels[remIdx + 2];
                }
            }

            for (int x = 0; x < width; x++)
            {
                int sumB = 0, sumG = 0, sumR = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int sy = Math.Clamp(k, 0, height - 1);
                    int idx = (sy * width + x) * 4;
                    sumB += temp[idx];
                    sumG += temp[idx + 1];
                    sumR += temp[idx + 2];
                }

                for (int y = 0; y < height; y++)
                {
                    int dstIdx = (y * width + x) * 4;
                    pixels[dstIdx] = (byte)(sumB / diam);
                    pixels[dstIdx + 1] = (byte)(sumG / diam);
                    pixels[dstIdx + 2] = (byte)(sumR / diam);
                    pixels[dstIdx + 3] = 255;

                    int addIdx = (Math.Min(y + radius + 1, height - 1) * width + x) * 4;
                    int remIdx = (Math.Max(y - radius, 0) * width + x) * 4;
                    sumB += temp[addIdx] - temp[remIdx];
                    sumG += temp[addIdx + 1] - temp[remIdx + 1];
                    sumR += temp[addIdx + 2] - temp[remIdx + 2];
                }
            }
        }
    }
}