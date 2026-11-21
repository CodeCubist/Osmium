using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Osmium.Core.Services;

public static class WindowsClipboardHelper
{
    private const uint CfDib = 8;
    private const uint CfUnicodeText = 13;
    private const uint CfHDrop = 15;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;
    private const uint DropEffectCopy = 1;

    private static readonly uint CfPreferredDropEffect = RegisterClipboardFormatW("Preferred DropEffect");

    public static string StagingRootDir { get; } = Path.Combine(Path.GetTempPath(), "OsmiumFileTransfer");

    public static uint GetSequenceNumber() => GetClipboardSequenceNumber();

    public static bool HasDropFiles() => IsClipboardFormatAvailable(CfHDrop);

    public static bool HasUnicodeText() => IsClipboardFormatAvailable(CfUnicodeText);

    public static bool HasDibImage() => IsClipboardFormatAvailable(CfDib);

    public static bool IsInsideStagingDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string fullRoot = Path.GetFullPath(StagingRootDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void CleanupOldStagingDirs(TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(StagingRootDir)) return;
            DateTime cutoff = DateTime.UtcNow - maxAge;
            foreach (string roleDir in Directory.EnumerateDirectories(StagingRootDir))
            {
                foreach (string batchDir in Directory.EnumerateDirectories(roleDir))
                {
                    try
                    {
                        var info = new DirectoryInfo(batchDir);
                        if (info.LastWriteTimeUtc < cutoff)
                        {
                            info.Delete(recursive: true);
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
    }

    public static long ClearAllStagingCache()
    {
        long freedBytes = 0;
        try
        {
            if (!Directory.Exists(StagingRootDir)) return 0;
            foreach (string file in Directory.EnumerateFiles(StagingRootDir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    freedBytes += new FileInfo(file).Length;
                }
                catch
                {
                }
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(StagingRootDir))
            {
                try
                {
                    if (Directory.Exists(entry))
                        Directory.Delete(entry, recursive: true);
                    else if (File.Exists(entry))
                        File.Delete(entry);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return freedBytes;
    }

    public static bool TryReadText(out string text)
    {
        text = string.Empty;
        if (!IsClipboardFormatAvailable(CfUnicodeText)) return false;
        if (!TryOpenClipboardWithRetry()) return false;
        try
        {
            IntPtr hData = GetClipboardData(CfUnicodeText);
            if (hData == IntPtr.Zero) return false;
            IntPtr ptr = GlobalLock(hData);
            if (ptr == IntPtr.Zero) return false;
            try
            {
                text = Marshal.PtrToStringUni(ptr) ?? string.Empty;
                return true;
            }
            finally
            {
                GlobalUnlock(hData);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static bool TrySetText(string text, Action<uint>? onBeforeClose = null)
    {
        text ??= string.Empty;
        if (!TryOpenClipboardWithRetry()) return false;
        try
        {
            if (!EmptyClipboard()) return false;

            byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
            IntPtr hGlobal = GlobalAlloc(GmemMoveable | GmemZeroInit, (UIntPtr)bytes.Length);
            if (hGlobal == IntPtr.Zero) return false;

            IntPtr ptr = GlobalLock(hGlobal);
            if (ptr == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }

            try
            {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(CfUnicodeText, hGlobal) == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }

            onBeforeClose?.Invoke(GetClipboardSequenceNumber());
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static bool TryReadDib(int maxRawBytes, out byte[] dibBytes)
    {
        dibBytes = [];
        if (!IsClipboardFormatAvailable(CfDib)) return false;
        if (!TryOpenClipboardWithRetry()) return false;
        try
        {
            IntPtr hData = GetClipboardData(CfDib);
            if (hData == IntPtr.Zero) return false;

            nuint size = GlobalSize(hData);
            if (size == 0 || size > (nuint)Math.Max(1024, maxRawBytes)) return false;

            IntPtr ptr = GlobalLock(hData);
            if (ptr == IntPtr.Zero) return false;
            try
            {
                dibBytes = new byte[(int)size];
                Marshal.Copy(ptr, dibBytes, 0, dibBytes.Length);
                return true;
            }
            finally
            {
                GlobalUnlock(hData);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static bool TrySetDib(byte[] dibBytes, Action<uint>? onBeforeClose = null)
    {
        if (dibBytes == null || dibBytes.Length < 40) return false;
        if (!TryOpenClipboardWithRetry()) return false;
        try
        {
            if (!EmptyClipboard()) return false;

            IntPtr hGlobal = GlobalAlloc(GmemMoveable | GmemZeroInit, (UIntPtr)dibBytes.Length);
            if (hGlobal == IntPtr.Zero) return false;

            IntPtr ptr = GlobalLock(hGlobal);
            if (ptr == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }

            try
            {
                Marshal.Copy(dibBytes, 0, ptr, dibBytes.Length);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(CfDib, hGlobal) == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }

            onBeforeClose?.Invoke(GetClipboardSequenceNumber());
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static bool TryReadDropFiles(out List<string> paths)
    {
        paths = [];
        if (!IsClipboardFormatAvailable(CfHDrop)) return false;
        if (!TryOpenClipboardWithRetry()) return false;
        try
        {
            IntPtr hDrop = GetClipboardData(CfHDrop);
            if (hDrop == IntPtr.Zero) return false;

            uint count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
            if (count == 0 || count > 4096) return false;

            var result = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                uint charLen = DragQueryFileW(hDrop, i, null, 0);
                if (charLen == 0 || charLen > 32767) continue;
                var sb = new StringBuilder((int)charLen + 2);
                if (DragQueryFileW(hDrop, i, sb, (uint)sb.Capacity) > 0)
                {
                    string p = sb.ToString();
                    if (File.Exists(p) || Directory.Exists(p))
                    {
                        result.Add(p);
                    }
                }
            }

            if (result.Count == 0) return false;
            paths = result;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static bool TrySetDropFiles(IReadOnlyList<string> paths, Action<uint>? onBeforeClose = null)
    {
        if (paths == null || paths.Count == 0) return false;

        var valid = new List<string>(paths.Count);
        foreach (string p in paths)
        {
            if (!string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
            {
                valid.Add(Path.GetFullPath(p));
            }
        }

        if (valid.Count == 0) return false;

        var sb = new StringBuilder();
        foreach (string p in valid)
        {
            sb.Append(p);
            sb.Append('\0');
        }
        sb.Append('\0');

        byte[] pathBytes = Encoding.Unicode.GetBytes(sb.ToString());
        int dropFilesSize = Marshal.SizeOf<DropFilesStruct>();
        int totalBytes = dropFilesSize + pathBytes.Length;

        if (!TryOpenClipboardWithRetry()) return false;
        try
        {
            if (!EmptyClipboard()) return false;

            IntPtr hDrop = GlobalAlloc(GmemMoveable | GmemZeroInit, (UIntPtr)totalBytes);
            if (hDrop == IntPtr.Zero) return false;

            IntPtr dropPtr = GlobalLock(hDrop);
            if (dropPtr == IntPtr.Zero)
            {
                GlobalFree(hDrop);
                return false;
            }

            try
            {
                var df = new DropFilesStruct
                {
                    PFiles = (uint)dropFilesSize,
                    PtX = 0,
                    PtY = 0,
                    FNc = 0,
                    FWide = 1
                };
                Marshal.StructureToPtr(df, dropPtr, fDeleteOld: false);
                Marshal.Copy(pathBytes, 0, IntPtr.Add(dropPtr, dropFilesSize), pathBytes.Length);
            }
            finally
            {
                GlobalUnlock(hDrop);
            }

            if (SetClipboardData(CfHDrop, hDrop) == IntPtr.Zero)
            {
                GlobalFree(hDrop);
                return false;
            }

            if (CfPreferredDropEffect != 0)
            {
                IntPtr hEffect = GlobalAlloc(GmemMoveable | GmemZeroInit, (UIntPtr)4);
                if (hEffect != IntPtr.Zero)
                {
                    IntPtr effPtr = GlobalLock(hEffect);
                    if (effPtr != IntPtr.Zero)
                    {
                        try
                        {
                            Marshal.WriteInt32(effPtr, unchecked((int)DropEffectCopy));
                        }
                        finally
                        {
                            GlobalUnlock(hEffect);
                        }

                        if (SetClipboardData(CfPreferredDropEffect, hEffect) == IntPtr.Zero)
                        {
                            GlobalFree(hEffect);
                        }
                    }
                    else
                    {
                        GlobalFree(hEffect);
                    }
                }
            }

            onBeforeClose?.Invoke(GetClipboardSequenceNumber());
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static byte[] CompressDib(byte[] dibBytes)
    {
        using var ms = new MemoryStream(Math.Max(1024, dibBytes.Length / 4));
        using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            ds.Write(dibBytes, 0, dibBytes.Length);
        }

        return ms.ToArray();
    }

    public static byte[]? DecompressDib(byte[] compressed, int maxOutputBytes)
    {
        if (compressed == null || compressed.Length == 0) return null;
        try
        {
            using var input = new MemoryStream(compressed);
            using var ds = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] buffer = new byte[16384];
            int total = 0;
            int read;
            while ((read = ds.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxOutputBytes) return null;
                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryOpenClipboardWithRetry()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(15);
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DropFilesStruct
    {
        public uint PFiles;
        public int PtX;
        public int PtY;
        public int FNc;
        public int FWide;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(IntPtr hMem);
}
