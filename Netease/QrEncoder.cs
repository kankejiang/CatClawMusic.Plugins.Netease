using System.Text;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 自包含 QR 码编码器（字节模式，版本 1-10，纠错等级 L/M/Q/H，自动选最小版本 + 最优掩码）。
/// <para>
/// 为什么自实现而不用第三方库：插件以单个 <c>.ccp</c>（就是改名的 DLL）分发，宿主用
/// <c>Assembly.Load(byte[])</c> 加载，且工程设了 <c>CopyLocalLockFileAssemblies=false</c>，
/// 引用二维码库需要额外的「嵌入资源 + ModuleInitializer 里挂 AssemblyResolve」一整套机制；
/// 而扫码登录只需要「字节模式 + 一个 URL」这点能力，自实现（纯托管、无依赖）更省事也更好审。
/// </para>
/// <para>依据 ISO/IEC 18004。实现范围刻意收敛在版本 1-10（扫码内容为 72 字节的 URL，实际落在版本 5）。</para>
/// </summary>
internal static class QrEncoder
{
    /// <summary>纠错等级</summary>
    internal enum Ecc { L, M, Q, H }

    /// <summary>编码结果</summary>
    internal sealed class QrCode
    {
        public int Version { get; init; }
        public int Size { get; init; }
        public int Mask { get; init; }
        /// <summary>模块矩阵 [row, col]，true = 深色</summary>
        public bool[,] Dark { get; init; } = new bool[0, 0];
    }

    // ── 版本参数表（版本 1-10）──
    // 每项：纠错码字数/块, 组1块数, 组1数据码字/块, 组2块数, 组2数据码字/块
    // 顺序按 Ecc 枚举：L, M, Q, H
    private static readonly int[][][] EcTable =
    {
        // v1（总码字 26）
        new[] { new[]{7,1,19,0,0}, new[]{10,1,16,0,0}, new[]{13,1,13,0,0}, new[]{17,1,9,0,0} },
        // v2（44）
        new[] { new[]{10,1,34,0,0}, new[]{16,1,28,0,0}, new[]{22,1,22,0,0}, new[]{28,1,16,0,0} },
        // v3（70）
        new[] { new[]{15,1,55,0,0}, new[]{26,1,44,0,0}, new[]{18,2,17,0,0}, new[]{22,2,13,0,0} },
        // v4（100）
        new[] { new[]{20,1,80,0,0}, new[]{18,2,32,0,0}, new[]{26,2,24,0,0}, new[]{16,4,9,0,0} },
        // v5（134）
        new[] { new[]{26,1,108,0,0}, new[]{24,2,43,0,0}, new[]{18,2,15,2,16}, new[]{22,2,11,2,12} },
        // v6（172）
        new[] { new[]{18,2,68,0,0}, new[]{16,4,27,0,0}, new[]{24,4,19,0,0}, new[]{28,4,15,0,0} },
        // v7（196）
        new[] { new[]{20,2,78,0,0}, new[]{18,4,31,0,0}, new[]{18,2,14,4,15}, new[]{26,4,13,1,14} },
        // v8（242）
        new[] { new[]{24,2,97,0,0}, new[]{22,2,38,2,39}, new[]{22,4,18,2,19}, new[]{26,4,14,2,15} },
        // v9（292）
        new[] { new[]{30,2,116,0,0}, new[]{22,3,36,2,37}, new[]{20,4,16,4,17}, new[]{24,4,12,4,13} },
        // v10（346）
        new[] { new[]{18,2,68,2,69}, new[]{26,4,43,1,44}, new[]{24,6,19,2,20}, new[]{28,6,15,2,16} },
    };

    /// <summary>每版本总码字数（数据 + 纠错），用于数据放置时的位数校验</summary>
    private static readonly int[] TotalCodewords = { 26, 44, 70, 100, 134, 172, 196, 242, 292, 346 };

    // 对齐图案中心坐标（版本 1 无）
    private static readonly int[][] AlignmentCenters =
    {
        Array.Empty<int>(),          // v1
        new[]{6,18}, new[]{6,22}, new[]{6,26}, new[]{6,30}, new[]{6,34},
        new[]{6,22,38}, new[]{6,24,42}, new[]{6,26,46}, new[]{6,28,50},
    };

    /// <summary>把文本编码为二维码（字节模式，UTF-8）</summary>
    /// <param name="maskOverride">强制指定掩码 0-7（仅用于自测/对照参考实现）；-1 = 按罚分自动选最优</param>
    public static QrCode Encode(string text, Ecc ecc = Ecc.M, int maskOverride = -1)
    {
        var data = Encoding.UTF8.GetBytes(text ?? string.Empty);
        var version = ChooseVersion(data.Length, ecc);
        if (version == 0)
            throw new ArgumentException($"内容过长（{data.Length} 字节），超出本编码器版本 1-10 的字节模式容量");

        var codewords = BuildCodewords(data, version, ecc);
        return BuildMatrix(version, ecc, codewords, maskOverride);
    }

    /// <summary>选择能容纳指定字节数的最小版本；超范围返回 0</summary>
    private static int ChooseVersion(int byteCount, Ecc ecc)
    {
        for (var v = 1; v <= 10; v++)
        {
            var dataCodewords = DataCodewordCount(v, ecc);
            // 模式指示符 4 bit + 字符计数（v<10 为 8 bit，否则 16 bit）
            var overheadBits = 4 + (v < 10 ? 8 : 16);
            if (dataCodewords * 8 - overheadBits >= byteCount * 8) return v;
        }
        return 0;
    }

    private static int DataCodewordCount(int version, Ecc ecc)
    {
        var t = EcTable[version - 1][(int)ecc];
        return t[1] * t[2] + t[3] * t[4];
    }

    // ── 码字生成 ──

    private static byte[] BuildCodewords(byte[] data, int version, Ecc ecc)
    {
        var t = EcTable[version - 1][(int)ecc];
        var ecPerBlock = t[0];
        var b1 = t[1]; var d1 = t[2];
        var b2 = t[3]; var d2 = t[4];
        var dataCodewords = b1 * d1 + b2 * d2;

        // 1) 数据位流：模式 0100 + 字符计数 + 数据 + 终止符 + 补齐
        var bits = new List<bool>(dataCodewords * 8);
        AppendBits(bits, 0b0100, 4);
        AppendBits(bits, data.Length, version < 10 ? 8 : 16);
        foreach (var b in data) AppendBits(bits, b, 8);
        var capacity = dataCodewords * 8;
        // 终止符最多 4 个 0
        for (var i = 0; i < 4 && bits.Count < capacity; i++) bits.Add(false);
        // 补齐到字节边界
        while (bits.Count % 8 != 0) bits.Add(false);
        // 交替填充 0xEC / 0x11
        var pad = new byte[] { 0xEC, 0x11 };
        var padIndex = 0;
        while (bits.Count < capacity)
        {
            AppendBits(bits, pad[padIndex], 8);
            padIndex ^= 1;
        }

        var dataBytes = new byte[dataCodewords];
        for (var i = 0; i < dataBytes.Length; i++)
        {
            var v = 0;
            for (var j = 0; j < 8; j++) if (bits[i * 8 + j]) v |= 1 << (7 - j);
            dataBytes[i] = (byte)v;
        }

        // 2) 分块 + 每块 Reed-Solomon 纠错
        var blocks = new List<byte[]>();
        var ecBlocks = new List<byte[]>();
        var offset = 0;
        for (var i = 0; i < b1 + b2; i++)
        {
            var len = i < b1 ? d1 : d2;
            var block = new byte[len];
            Array.Copy(dataBytes, offset, block, 0, len);
            offset += len;
            blocks.Add(block);
            ecBlocks.Add(ComputeEc(block, ecPerBlock));
        }

        // 3) 交织：先按列输出各块数据码字，再输出各块纠错码字
        var result = new List<byte>(TotalCodewords[version - 1]);
        var maxData = Math.Max(d1, d2);
        for (var i = 0; i < maxData; i++)
            foreach (var block in blocks)
                if (i < block.Length) result.Add(block[i]);
        for (var i = 0; i < ecPerBlock; i++)
            foreach (var ec in ecBlocks)
                result.Add(ec[i]);
        return result.ToArray();
    }

    private static void AppendBits(List<bool> bits, int value, int count)
    {
        for (var i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
    }

    // ── GF(256) 与 Reed-Solomon ──

    private static readonly byte[] GfExp = new byte[512];
    private static readonly byte[] GfLog = new byte[256];

    static QrEncoder()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            GfExp[i] = (byte)x;
            GfLog[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D;   // 本原多项式
        }
        for (var i = 255; i < 512; i++) GfExp[i] = GfExp[i - 255];
    }

    private static byte GfMul(byte a, byte b)
        => (a == 0 || b == 0) ? (byte)0 : GfExp[GfLog[a] + GfLog[b]];

    /// <summary>计算一个数据块的纠错码字（多项式长除法）</summary>
    private static byte[] ComputeEc(byte[] data, int ecCount)
    {
        var gen = BuildGenerator(ecCount);
        var rem = new byte[ecCount];
        foreach (var b in data)
        {
            var factor = (byte)(b ^ rem[0]);
            Array.Copy(rem, 1, rem, 0, ecCount - 1);
            rem[ecCount - 1] = 0;
            if (factor == 0) continue;
            for (var i = 0; i < ecCount; i++)
                rem[i] ^= GfMul(gen[i], factor);
        }
        return rem;
    }

    /// <summary>构造生成多项式 ∏(x - α^i)，返回去掉最高次项后的系数（长度 = ecCount）</summary>
    private static byte[] BuildGenerator(int ecCount)
    {
        var poly = new List<byte> { 1 };
        for (var i = 0; i < ecCount; i++)
        {
            var root = GfExp[i];
            var next = new byte[poly.Count + 1];
            for (var j = 0; j < poly.Count; j++)
            {
                next[j] ^= poly[j];
                next[j + 1] ^= GfMul(poly[j], root);
            }
            poly = new List<byte>(next);
        }
        // poly[0] 必为 1（最高次）；返回其余系数
        var result = new byte[ecCount];
        Array.Copy(poly.ToArray(), 1, result, 0, ecCount);
        return result;
    }

    // ── 矩阵构造 ──

    private static QrCode BuildMatrix(int version, Ecc ecc, byte[] codewords, int maskOverride = -1)
    {
        var size = 17 + version * 4;
        var dark = new bool[size, size];
        var function = new bool[size, size];   // 功能图案占位（不参与掩码/数据）

        DrawFinder(dark, function, 0, 0, size);
        DrawFinder(dark, function, size - 7, 0, size);
        DrawFinder(dark, function, 0, size - 7, size);

        // 定位（时序）图案
        for (var i = 8; i < size - 8; i++)
        {
            var on = i % 2 == 0;
            dark[6, i] = on; function[6, i] = true;
            dark[i, 6] = on; function[i, 6] = true;
        }

        // 对齐图案（除三个定位图案角点外的所有组合；与定位图案重叠的角点跳过）
        var centers = AlignmentCenters[version - 1];
        var lastCenter = size - 7;
        foreach (var r in centers)
            foreach (var c in centers)
            {
                if ((r == 6 && c == 6) || (r == 6 && c == lastCenter) || (r == lastCenter && c == 6)) continue;
                DrawAlignment(dark, function, r, c);
            }

        // 固定深色模块
        dark[size - 8, 8] = true; function[size - 8, 8] = true;

        // 预留格式信息区（先占位，稍后按掩码写入）
        ReserveFormatAreas(function, size);

        // 版本信息（版本 >= 7）
        if (version >= 7) DrawVersionInfo(dark, function, version, size);

        // 放置数据位
        PlaceData(dark, function, codewords, size);

        // 选最优掩码（或用调用方指定的掩码，便于与参考实现对照）
        var bestMask = maskOverride;
        if (maskOverride < 0)
        {
            var bestScore = int.MaxValue;
            for (var mask = 0; mask < 8; mask++)
            {
                var candidate = (bool[,])dark.Clone();
                ApplyMask(candidate, function, mask, size);
                WriteFormatInfo(candidate, ecc, mask, size);
                var score = ComputePenalty(candidate, size);
                if (score < bestScore) { bestScore = score; bestMask = mask; }
            }
        }

        ApplyMask(dark, function, bestMask, size);
        WriteFormatInfo(dark, ecc, bestMask, size);
        return new QrCode { Version = version, Size = size, Mask = bestMask, Dark = dark };
    }

    private static void DrawFinder(bool[,] dark, bool[,] function, int row, int col, int size)
    {
        for (var r = -1; r <= 7; r++)
        {
            for (var c = -1; c <= 7; c++)
            {
                var rr = row + r; var cc = col + c;
                if (rr < 0 || rr >= size || cc < 0 || cc >= size) continue;
                function[rr, cc] = true;
                var inRing = (r >= 0 && r <= 6 && (c == 0 || c == 6)) || (c >= 0 && c <= 6 && (r == 0 || r == 6));
                var inCore = r >= 2 && r <= 4 && c >= 2 && c <= 4;
                dark[rr, cc] = inRing || inCore;
            }
        }
    }

    private static void DrawAlignment(bool[,] dark, bool[,] function, int row, int col)
    {
        for (var r = -2; r <= 2; r++)
            for (var c = -2; c <= 2; c++)
            {
                function[row + r, col + c] = true;
                var ringEdge = Math.Max(Math.Abs(r), Math.Abs(c));
                dark[row + r, col + c] = ringEdge != 1;   // 外圈 + 中心为深色
            }
    }

    private static void ReserveFormatAreas(bool[,] function, int size)
    {
        for (var i = 0; i < 9; i++)
        {
            if (i != 6) { function[8, i] = true; function[i, 8] = true; }
        }
        for (var i = 0; i < 8; i++)
        {
            function[8, size - 1 - i] = true;
            function[size - 1 - i, 8] = true;
        }
    }

    private static void DrawVersionInfo(bool[,] dark, bool[,] function, int version, int size)
    {
        var bits = VersionBits(version);
        for (var i = 0; i < 18; i++)
        {
            var bit = ((bits >> i) & 1) != 0;
            var a = i / 3;          // 0..5
            var b = i % 3;          // 0..2
            // 左下 3×6
            dark[size - 11 + b, a] = bit;
            function[size - 11 + b, a] = true;
            // 右上 6×3
            dark[a, size - 11 + b] = bit;
            function[a, size - 11 + b] = true;
        }
    }

    /// <summary>版本信息：6 位版本 + BCH(18,6)，生成多项式 0x1F25</summary>
    private static int VersionBits(int version)
    {
        var rem = version << 12;
        for (var i = 5; i >= 0; i--)
            if (((rem >> (i + 12)) & 1) != 0) rem ^= 0x1F25 << i;
        return (version << 12) | rem;
    }

    private static void PlaceData(bool[,] dark, bool[,] function, byte[] codewords, int size)
    {
        var bitIndex = 0;
        var totalBits = codewords.Length * 8;
        var dir = -1;                          // -1 = 向上，+1 = 向下
        var row = size - 1;

        for (var col = size - 1; col > 0; col -= 2)
        {
            if (col == 6) col--;               // 跳过时序列（第 6 列）
            while (true)
            {
                for (var k = 0; k < 2; k++)
                {
                    var cc = col - k;
                    if (function[row, cc]) continue;
                    var bit = false;
                    if (bitIndex < totalBits)
                        bit = ((codewords[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) != 0;
                    dark[row, cc] = bit;
                    bitIndex++;
                }
                row += dir;
                if (row < 0 || row >= size) { row -= dir; dir = -dir; break; }
            }
        }
    }

    private static void ApplyMask(bool[,] dark, bool[,] function, int mask, int size)
    {
        for (var r = 0; r < size; r++)
            for (var c = 0; c < size; c++)
            {
                if (function[r, c]) continue;
                if (MaskBit(mask, r, c)) dark[r, c] = !dark[r, c];
            }
    }

    private static bool MaskBit(int mask, int r, int c) => mask switch
    {
        0 => (r + c) % 2 == 0,
        1 => r % 2 == 0,
        2 => c % 3 == 0,
        3 => (r + c) % 3 == 0,
        4 => (r / 2 + c / 3) % 2 == 0,
        5 => (r * c) % 2 + (r * c) % 3 == 0,
        6 => ((r * c) % 2 + (r * c) % 3) % 2 == 0,
        _ => ((r + c) % 2 + (r * c) % 3) % 2 == 0,
    };

    /// <summary>写入格式信息（纠错等级 + 掩码），BCH(15,5) 生成多项式 0x537，结果异或 0x5412。
    /// <para>
    /// 位序说明（易错点）：15 位序列按**最高位先行**摆放（bit14 在 (8,0)），
    /// 这一点由两个成熟实现交叉验证得出（goQR.me 的 SVG 输出 + Python qrcode 库，
    /// 均通过 BCH 自校验唯一匹配本布局）。若把它写成"最低位在 (8,0)"，
    /// 矩阵结构看着完全正常但扫不出来（格式信息被读成另一组纠错等级/掩码）。
    /// </para>
    /// </summary>
    private static void WriteFormatInfo(bool[,] dark, Ecc ecc, int mask, int size)
    {
        var bits = FormatBits(ecc, mask);
        bool Bit(int i) => ((bits >> i) & 1) != 0;

        // 副本一：bit14 → (8,0) … bit9 → (8,5)，bit8 → (8,7)，bit7 → (8,8)，bit6 → (7,8)，bit5…bit0 → (5,8)…(0,8)
        for (var k = 0; k < 6; k++) dark[8, k] = Bit(14 - k);
        dark[8, 7] = Bit(8);
        dark[8, 8] = Bit(7);
        dark[7, 8] = Bit(6);
        for (var k = 9; k < 15; k++) dark[14 - k, 8] = Bit(14 - k);

        // 副本二：左下（行 size-1 → size-7）= bit14…bit8；右上（列 size-8 → size-1）= bit7…bit0
        for (var k = 0; k < 7; k++) dark[size - 1 - k, 8] = Bit(14 - k);
        for (var k = 7; k < 15; k++) dark[8, size - 15 + k] = Bit(14 - k);
    }

    private static int FormatBits(Ecc ecc, int mask)
    {
        // 2 位纠错标识（注意 ISO 顺序：M=00, L=01, H=10, Q=11）
        var eccBits = ecc switch
        {
            Ecc.L => 0b01,
            Ecc.M => 0b00,
            Ecc.Q => 0b11,
            _ => 0b10,
        };
        var data = (eccBits << 3) | mask;         // 5 位
        var rem = data << 10;
        for (var i = 4; i >= 0; i--)
            if (((rem >> (i + 10)) & 1) != 0) rem ^= 0x537 << i;
        return ((data << 10) | rem) ^ 0x5412;
    }

    /// <summary>掩码罚分（ISO 18004 四条规则，取最小者）</summary>
    private static int ComputePenalty(bool[,] m, int size)
    {
        var penalty = 0;

        // 规则 1：行/列连续同色 5 个以上
        for (var r = 0; r < size; r++)
        {
            var run = 1;
            for (var c = 1; c < size; c++)
            {
                if (m[r, c] == m[r, c - 1]) run++;
                else { if (run >= 5) penalty += 3 + (run - 5); run = 1; }
            }
            if (run >= 5) penalty += 3 + (run - 5);
        }
        for (var c = 0; c < size; c++)
        {
            var run = 1;
            for (var r = 1; r < size; r++)
            {
                if (m[r, c] == m[r - 1, c]) run++;
                else { if (run >= 5) penalty += 3 + (run - 5); run = 1; }
            }
            if (run >= 5) penalty += 3 + (run - 5);
        }

        // 规则 2：2×2 同色块
        for (var r = 0; r < size - 1; r++)
            for (var c = 0; c < size - 1; c++)
            {
                var v = m[r, c];
                if (v == m[r, c + 1] && v == m[r + 1, c] && v == m[r + 1, c + 1]) penalty += 3;
            }

        // 规则 3：类定位图案（1:1:3:1:1 + 四浅色）
        var p1 = new[] { true, false, true, true, true, false, true, false, false, false, false };
        var p2 = new[] { false, false, false, false, true, false, true, true, true, false, true };
        for (var r = 0; r < size; r++)
            for (var c = 0; c <= size - 11; c++)
            {
                if (MatchAt(m, r, c, p1, horizontal: true) || MatchAt(m, r, c, p2, horizontal: true)) penalty += 40;
            }
        for (var c = 0; c < size; c++)
            for (var r = 0; r <= size - 11; r++)
            {
                if (MatchAt(m, r, c, p1, horizontal: false) || MatchAt(m, r, c, p2, horizontal: false)) penalty += 40;
            }

        // 规则 4：深色比例偏离 50%
        var darkCount = 0;
        for (var r = 0; r < size; r++)
            for (var c = 0; c < size; c++)
                if (m[r, c]) darkCount++;
        var total = size * size;
        var percent = darkCount * 100.0 / total;
        var deviation = Math.Abs(percent - 50) / 5.0;
        penalty += (int)Math.Floor(deviation) * 10;

        return penalty;

        static bool MatchAt(bool[,] m, int r, int c, bool[] pattern, bool horizontal)
        {
            for (var i = 0; i < pattern.Length; i++)
            {
                var v = horizontal ? m[r, c + i] : m[r + i, c];
                if (v != pattern[i]) return false;
            }
            return true;
        }
    }
}
