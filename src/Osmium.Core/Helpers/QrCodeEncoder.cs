using System.Text;

namespace Osmium.Core.Helpers;

/// <summary>
///     Self-contained ISO/IEC 18004 QR Code encoder (Byte mode, Error Correction Level M, Versions 1-20).
/// </summary>
public static class QrCodeEncoder
{
    private const int EcFormatBitsM = 0; // Level M format indicator bits: 00

    private static readonly int[] EcCodewordsPerBlockM =
    [
        0, // 1-indexed
        10, 16, 26, 18, 24, 16, 18, 22, 22, 26,
        30, 22, 22, 24, 24, 28, 28, 26, 26, 26
    ];

    private static readonly int[] NumBlocksM =
    [
        0,
        1, 1, 1, 2, 2, 4, 4, 4, 5, 5,
        5, 8, 9, 9, 10, 10, 11, 13, 14, 16
    ];

    public static bool[,] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] dataBytes = Encoding.UTF8.GetBytes(text);

        int version = SelectVersion(dataBytes.Length);
        int size = version * 4 + 17;

        byte[] dataCodewords = BuildDataCodewords(dataBytes, version);
        byte[] allCodewords = AddErrorCorrectionAndInterleave(dataCodewords, version);

        bool[,] isFunction = new bool[size, size];
        bool[,] baseModules = new bool[size, size];
        DrawFunctionPatterns(baseModules, isFunction, version);
        DrawCodewords(baseModules, isFunction, allCodewords);

        int bestMask = 0;
        long bestPenalty = long.MaxValue;
        bool[,]? bestMatrix = null;

        for (int mask = 0; mask < 8; mask++)
        {
            var candidate = (bool[,])baseModules.Clone();
            ApplyMask(candidate, isFunction, mask);
            DrawFormatBits(candidate, mask);
            if (version >= 7)
            {
                DrawVersionBits(candidate, version);
            }

            long penalty = ComputePenaltyScore(candidate);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestMask = mask;
                bestMatrix = candidate;
            }
        }

        _ = bestMask;
        return bestMatrix!;
    }

    private static int SelectVersion(int byteCount)
    {
        for (int v = 1; v <= 20; v++)
        {
            int dataCapBits = GetNumDataCodewords(v) * 8;
            int ccBits = v <= 9 ? 8 : 16;
            int neededBits = 4 + ccBits + byteCount * 8;
            if (neededBits <= dataCapBits)
            {
                return v;
            }
        }

        throw new ArgumentException("QR payload exceeds supported Version 20-M capacity.", nameof(byteCount));
    }

    private static int GetNumRawDataModules(int version)
    {
        int result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            int numAlign = version / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (version >= 7)
            {
                result -= 36;
            }
        }

        return result;
    }

    private static int GetNumDataCodewords(int version)
    {
        int totalCodewords = GetNumRawDataModules(version) / 8;
        return totalCodewords - EcCodewordsPerBlockM[version] * NumBlocksM[version];
    }

    private static byte[] BuildDataCodewords(byte[] dataBytes, int version)
    {
        int dataCapCodewords = GetNumDataCodewords(version);
        int dataCapBits = dataCapCodewords * 8;
        var bits = new List<bool>(dataCapBits);

        // Mode indicator: Byte mode = 0100
        AppendBits(bits, 0b0100, 4);

        // Character count indicator: 8 bits for v1..9, 16 bits for v10..20
        int ccBits = version <= 9 ? 8 : 16;
        AppendBits(bits, dataBytes.Length, ccBits);

        foreach (byte b in dataBytes)
        {
            AppendBits(bits, b, 8);
        }

        // Terminator (up to 4 zero bits)
        int terminatorLen = Math.Min(4, dataCapBits - bits.Count);
        AppendBits(bits, 0, terminatorLen);

        // Pad to byte boundary
        int remBits = (8 - (bits.Count % 8)) % 8;
        AppendBits(bits, 0, remBits);

        // Pad codewords alternating 0xEC and 0x11
        int padIdx = 0;
        while (bits.Count < dataCapBits)
        {
            AppendBits(bits, (padIdx++ & 1) == 0 ? 0xEC : 0x11, 8);
        }

        byte[] result = new byte[dataCapCodewords];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                result[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            }
        }

        return result;
    }

    private static void AppendBits(List<bool> bits, int val, int length)
    {
        for (int i = length - 1; i >= 0; i--)
        {
            bits.Add(((val >> i) & 1) != 0);
        }
    }

    private static byte[] AddErrorCorrectionAndInterleave(byte[] data, int version)
    {
        int numBlocks = NumBlocksM[version];
        int ecLen = EcCodewordsPerBlockM[version];
        int totalCodewords = GetNumRawDataModules(version) / 8;
        int numShortBlocks = numBlocks - (totalCodewords % numBlocks);
        int shortBlockLen = totalCodewords / numBlocks;

        byte[] rsDivisor = ComputeReedSolomonDivisor(ecLen);
        var blocks = new byte[numBlocks][];
        var eccs = new byte[numBlocks][];

        int offset = 0;
        for (int i = 0; i < numBlocks; i++)
        {
            int dataLen = shortBlockLen - ecLen + (i < numShortBlocks ? 0 : 1);
            byte[] blockData = new byte[dataLen];
            Array.Copy(data, offset, blockData, 0, dataLen);
            offset += dataLen;

            blocks[i] = blockData;
            eccs[i] = ComputeReedSolomonRemainder(blockData, rsDivisor);
        }

        var result = new List<byte>(totalCodewords);
        int maxDataLen = shortBlockLen - ecLen + (numShortBlocks < numBlocks ? 1 : 0);
        for (int i = 0; i < maxDataLen; i++)
        {
            for (int b = 0; b < numBlocks; b++)
            {
                if (i < blocks[b].Length)
                {
                    result.Add(blocks[b][i]);
                }
            }
        }

        for (int i = 0; i < ecLen; i++)
        {
            for (int b = 0; b < numBlocks; b++)
            {
                result.Add(eccs[b][i]);
            }
        }

        return result.ToArray();
    }

    private static byte[] ComputeReedSolomonDivisor(int degree)
    {
        byte[] result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = GfMultiply(result[j], (byte)root);
                if (j + 1 < degree)
                {
                    result[j] ^= result[j + 1];
                }
            }

            root = GfMultiply((byte)root, 0x02);
        }

        return result;
    }

    private static byte[] ComputeReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        byte[] result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++)
            {
                result[i] ^= GfMultiply(divisor[i], factor);
            }
        }

        return result;
    }

    private static byte GfMultiply(byte x, byte y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }

        return (byte)z;
    }

    private static void DrawFunctionPatterns(bool[,] modules, bool[,] isFunction, int version)
    {
        int size = modules.GetLength(0);

        // Timing patterns
        for (int i = 0; i < size; i++)
        {
            SetFunctionModule(modules, isFunction, 6, i, (i & 1) == 0);
            SetFunctionModule(modules, isFunction, i, 6, (i & 1) == 0);
        }

        // Three finder patterns + separators
        DrawFinderPattern(modules, isFunction, 3, 3);
        DrawFinderPattern(modules, isFunction, size - 4, 3);
        DrawFinderPattern(modules, isFunction, 3, size - 4);

        // Alignment patterns
        int[] alignPos = GetAlignmentPatternPositions(version);
        int numAlign = alignPos.Length;
        for (int i = 0; i < numAlign; i++)
        {
            for (int j = 0; j < numAlign; j++)
            {
                if ((i == 0 && j == 0) ||
                    (i == 0 && j == numAlign - 1) ||
                    (i == numAlign - 1 && j == 0))
                {
                    continue;
                }

                DrawAlignmentPattern(modules, isFunction, alignPos[i], alignPos[j]);
            }
        }

        // Reserve format info areas + dark module
        ReserveFormatBits(modules, isFunction);
        if (version >= 7)
        {
            ReserveVersionBits(modules, isFunction);
        }
    }

    private static void SetFunctionModule(bool[,] modules, bool[,] isFunction, int x, int y, bool dark)
    {
        modules[y, x] = dark;
        isFunction[y, x] = true;
    }

    private static void DrawFinderPattern(bool[,] modules, bool[,] isFunction, int cx, int cy)
    {
        int size = modules.GetLength(0);
        for (int dy = -4; dy <= 4; dy++)
        {
            for (int dx = -4; dx <= 4; dx++)
            {
                int x = cx + dx;
                int y = cy + dy;
                if (x < 0 || x >= size || y < 0 || y >= size) continue;
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                bool dark = dist != 2 && dist != 4;
                SetFunctionModule(modules, isFunction, x, y, dark);
            }
        }
    }

    private static void DrawAlignmentPattern(bool[,] modules, bool[,] isFunction, int cx, int cy)
    {
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunctionModule(modules, isFunction, cx + dx, cy + dy, dist != 1);
            }
        }
    }

    private static int[] GetAlignmentPatternPositions(int version)
    {
        if (version == 1) return [];
        int numAlign = version / 7 + 2;
        int step = (version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
        int[] result = new int[numAlign];
        result[0] = 6;
        for (int i = numAlign - 1, pos = version * 4 + 10; i >= 1; i--, pos -= step)
        {
            result[i] = pos;
        }

        return result;
    }

    private static void ReserveFormatBits(bool[,] modules, bool[,] isFunction)
    {
        int size = modules.GetLength(0);
        for (int i = 0; i <= 5; i++) SetFunctionModule(modules, isFunction, 8, i, false);
        SetFunctionModule(modules, isFunction, 8, 7, false);
        SetFunctionModule(modules, isFunction, 8, 8, false);
        SetFunctionModule(modules, isFunction, 7, 8, false);
        for (int i = 0; i <= 5; i++) SetFunctionModule(modules, isFunction, 5 - i, 8, false);

        for (int i = 0; i < 8; i++) SetFunctionModule(modules, isFunction, size - 1 - i, 8, false);
        for (int i = 0; i < 7; i++) SetFunctionModule(modules, isFunction, 8, size - 7 + i, false);

        // Always-dark module at (8, size - 8)
        SetFunctionModule(modules, isFunction, 8, size - 8, true);
    }

    private static void ReserveVersionBits(bool[,] modules, bool[,] isFunction)
    {
        int size = modules.GetLength(0);
        for (int i = 0; i < 18; i++)
        {
            int a = size - 11 + (i % 3);
            int b = i / 3;
            SetFunctionModule(modules, isFunction, a, b, false);
            SetFunctionModule(modules, isFunction, b, a, false);
        }
    }

    private static void DrawFormatBits(bool[,] modules, int mask)
    {
        int size = modules.GetLength(0);
        int data = (EcFormatBitsM << 3) | mask;
        int rem = data;
        for (int i = 0; i < 10; i++)
        {
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        }

        int bits = ((data << 10) | rem) ^ 0x5412;

        // First copy around top-left finder
        for (int i = 0; i <= 5; i++) modules[i, 8] = ((bits >> i) & 1) != 0;
        modules[7, 8] = ((bits >> 6) & 1) != 0;
        modules[8, 8] = ((bits >> 7) & 1) != 0;
        modules[8, 7] = ((bits >> 8) & 1) != 0;
        for (int i = 9; i < 15; i++) modules[8, 14 - i] = ((bits >> i) & 1) != 0;

        // Second copy along top-right and bottom-left finders
        for (int i = 0; i < 8; i++) modules[8, size - 1 - i] = ((bits >> i) & 1) != 0;
        for (int i = 8; i < 15; i++) modules[size - 15 + i, 8] = ((bits >> i) & 1) != 0;
        modules[size - 8, 8] = true;
    }

    private static void DrawVersionBits(bool[,] modules, int version)
    {
        int size = modules.GetLength(0);
        int rem = version;
        for (int i = 0; i < 12; i++)
        {
            rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        }

        int bits = (version << 12) | rem;
        for (int i = 0; i < 18; i++)
        {
            bool bit = ((bits >> i) & 1) != 0;
            int a = size - 11 + (i % 3);
            int b = i / 3;
            modules[b, a] = bit;
            modules[a, b] = bit;
        }
    }

    private static void DrawCodewords(bool[,] modules, bool[,] isFunction, byte[] codewords)
    {
        int size = modules.GetLength(0);
        int bitIdx = 0;
        int totalBits = codewords.Length * 8;

        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            bool upward = ((right + 1) & 2) == 0;
            for (int vert = 0; vert < size; vert++)
            {
                int y = upward ? size - 1 - vert : vert;
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    if (isFunction[y, x]) continue;
                    bool dark = false;
                    if (bitIdx < totalBits)
                    {
                        dark = ((codewords[bitIdx >> 3] >> (7 - (bitIdx & 7))) & 1) != 0;
                        bitIdx++;
                    }

                    modules[y, x] = dark;
                }
            }
        }
    }

    private static void ApplyMask(bool[,] modules, bool[,] isFunction, int mask)
    {
        int size = modules.GetLength(0);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (isFunction[y, x]) continue;
                bool invert = mask switch
                {
                    0 => ((x + y) & 1) == 0,
                    1 => (y & 1) == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (((y / 2) + (x / 3)) & 1) == 0,
                    5 => (x * y) % 2 + (x * y) % 3 == 0,
                    6 => (((x * y) % 2 + (x * y) % 3) & 1) == 0,
                    7 => (((x + y) % 2 + (x * y) % 3) & 1) == 0,
                    _ => false
                };

                if (invert)
                {
                    modules[y, x] = !modules[y, x];
                }
            }
        }
    }

    private static long ComputePenaltyScore(bool[,] modules)
    {
        int size = modules.GetLength(0);
        long penalty = 0;

        // Rule 1 & Rule 3: horizontal runs and finder-like patterns
        for (int y = 0; y < size; y++)
        {
            bool runColor = false;
            int runLen = 0;
            int[] runHistory = new int[7];
            for (int x = 0; x < size; x++)
            {
                if (modules[y, x] == runColor)
                {
                    runLen++;
                    if (runLen == 5) penalty += 3;
                    else if (runLen > 5) penalty++;
                }
                else
                {
                    AddFinderHistory(runLen, runHistory);
                    if (!runColor && FinderPenaltyMatch(runHistory)) penalty += 40;
                    runColor = modules[y, x];
                    runLen = 1;
                }
            }

            AddFinderHistory(runLen, runHistory);
            if (!runColor && FinderPenaltyMatch(runHistory)) penalty += 40;
        }

        // Rule 1 & Rule 3: vertical runs and finder-like patterns
        for (int x = 0; x < size; x++)
        {
            bool runColor = false;
            int runLen = 0;
            int[] runHistory = new int[7];
            for (int y = 0; y < size; y++)
            {
                if (modules[y, x] == runColor)
                {
                    runLen++;
                    if (runLen == 5) penalty += 3;
                    else if (runLen > 5) penalty++;
                }
                else
                {
                    AddFinderHistory(runLen, runHistory);
                    if (!runColor && FinderPenaltyMatch(runHistory)) penalty += 40;
                    runColor = modules[y, x];
                    runLen = 1;
                }
            }

            AddFinderHistory(runLen, runHistory);
            if (!runColor && FinderPenaltyMatch(runHistory)) penalty += 40;
        }

        // Rule 2: 2x2 blocks of same color
        for (int y = 0; y < size - 1; y++)
        {
            for (int x = 0; x < size - 1; x++)
            {
                bool c = modules[y, x];
                if (c == modules[y, x + 1] && c == modules[y + 1, x] && c == modules[y + 1, x + 1])
                {
                    penalty += 3;
                }
            }
        }

        // Rule 4: balance of dark modules
        int dark = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (modules[y, x]) dark++;
            }
        }

        int total = size * size;
        int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        if (k > 0) penalty += k * 10L;

        return penalty;
    }

    private static void AddFinderHistory(int len, int[] history)
    {
        Array.Copy(history, 0, history, 1, history.Length - 1);
        history[0] = len;
    }

    private static bool FinderPenaltyMatch(int[] h)
    {
        int n = h[1];
        if (n <= 0) return false;
        bool core = h[2] == n && h[3] == 3 * n && h[4] == n && h[5] == n;
        return core && (h[0] >= 4 * n || h[6] >= 4 * n);
    }
}