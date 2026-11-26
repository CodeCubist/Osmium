using System.Runtime.InteropServices;

namespace Osmium.Core.Helpers;

public static class DpapiStorageHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private const uint CryptProtectUiForbidden = 0x01;
    private const uint CryptProtectLocalMachine = 0x04;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        in DataBlob dataIn,
        string? dataDescr,
        in DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        in DataBlob dataIn,
        IntPtr dataDescr,
        in DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static unsafe byte[]? ProtectBytes(byte[] plain, byte[] entropy, string description, bool localMachineScope)
    {
        if (plain.Length == 0) return [];
        uint flags = CryptProtectUiForbidden | (localMachineScope ? CryptProtectLocalMachine : 0u);
        fixed (byte* pPlain = plain)
        fixed (byte* pEntropy = entropy)
        {
            var blobIn = new DataBlob { Size = plain.Length, Data = (IntPtr)pPlain };
            var blobEnt = new DataBlob { Size = entropy.Length, Data = (IntPtr)pEntropy };
            if (!CryptProtectData(in blobIn, description, in blobEnt, IntPtr.Zero, IntPtr.Zero, flags, out var blobOut))
            {
                return null;
            }

            try
            {
                byte[] res = new byte[blobOut.Size];
                Marshal.Copy(blobOut.Data, res, 0, blobOut.Size);
                return res;
            }
            finally
            {
                if (blobOut.Data != IntPtr.Zero) LocalFree(blobOut.Data);
            }
        }
    }

    public static unsafe byte[]? UnprotectBytes(byte[] cipher, byte[] entropy)
    {
        if (cipher.Length == 0) return [];
        fixed (byte* pCipher = cipher)
        fixed (byte* pEntropy = entropy)
        {
            var blobIn = new DataBlob { Size = cipher.Length, Data = (IntPtr)pCipher };
            var blobEnt = new DataBlob { Size = entropy.Length, Data = (IntPtr)pEntropy };
            if (!CryptUnprotectData(in blobIn, IntPtr.Zero, in blobEnt, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var blobOut))
            {
                return null;
            }

            try
            {
                byte[] res = new byte[blobOut.Size];
                Marshal.Copy(blobOut.Data, res, 0, blobOut.Size);
                return res;
            }
            finally
            {
                if (blobOut.Data != IntPtr.Zero) LocalFree(blobOut.Data);
            }
        }
    }

    public static void WriteBytesAtomic(string targetPath, byte[] bytes)
    {
        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmp = targetPath + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, targetPath, overwrite: true);
    }

    public static void SecureDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 0 && info.Length <= 1024 * 1024)
            {
                byte[] zeros = new byte[info.Length];
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                fs.Write(zeros, 0, zeros.Length);
                fs.Flush(true);
            }
        }
        catch
        {
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
