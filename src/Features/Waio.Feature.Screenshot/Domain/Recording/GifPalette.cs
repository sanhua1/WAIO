namespace Waio.Feature.Screenshot.Domain.Recording;

/// <summary>
/// GIF 每帧的调色板(MAIO GIF 编码器的做法,ADR-0021):
/// <list type="bullet">
/// <item>颜色不超过可用数量时直接用原色,完全无损(界面、文字大多如此);</item>
/// <item>否则在 15 位(每通道 5 位)直方图上做中位切分,不抖动(屏幕内容抖动后满屏噪点,文件也更大)。</item>
/// </list>
/// 像素是 0xAARRGGBB;Alpha 为 0 的表示"和上一帧一样",映射到透明色 <c>maxColors</c>(调用方保证调色板留了这一格)。
/// </summary>
internal static class GifPalette
{
    /// <summary>选调色板并把每个像素换成下标。返回调色板(0xRRGGBB)。</summary>
    /// <param name="maxColors">不透明颜色最多几种(有透明像素时 ≤ 255,透明色用下标 maxColors)。</param>
    public static uint[] Build(ReadOnlySpan<uint> pixels, int maxColors, Span<byte> indices) =>
        TryExact(pixels, maxColors, indices) ?? MedianCut(pixels, maxColors, indices);

    private static uint[]? TryExact(ReadOnlySpan<uint> pixels, int maxColors, Span<byte> indices)
    {
        const int Size = 1024; // 2 的幂,装 256 种颜色负载 ≤ 25%
        var keys = new uint[Size];
        var values = new byte[Size];
        var used = new bool[Size];
        var palette = new List<uint>(maxColors);
        var lastColor = uint.MaxValue;
        byte lastIndex = 0;
        var transparent = (byte)Math.Min(maxColors, 255);
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            if (p >> 24 == 0)
            {
                indices[i] = transparent;
                continue;
            }

            var color = p & 0xFFFFFF;
            if (color == lastColor)
            {
                indices[i] = lastIndex;
                continue;
            }

            var slot = (int)((color * 2654435761u) >> 22);
            while (used[slot] && keys[slot] != color)
            {
                slot = (slot + 1) & (Size - 1);
            }

            if (!used[slot])
            {
                if (palette.Count == maxColors)
                {
                    return null;
                }

                used[slot] = true;
                keys[slot] = color;
                values[slot] = (byte)palette.Count;
                palette.Add(color);
            }

            lastColor = color;
            lastIndex = values[slot];
            indices[i] = lastIndex;
        }

        return [.. palette];
    }

    private static uint[] MedianCut(ReadOnlySpan<uint> pixels, int maxColors, Span<byte> indices)
    {
        // 直方图:每个 15 位格子的像素数和真实颜色之和(调色板取平均色,比格子中心准)
        var counts = new int[32768];
        var sums = new long[32768 * 3];
        foreach (var p in pixels)
        {
            if (p >> 24 == 0)
            {
                continue;
            }

            var cell = Cell(p);
            counts[cell]++;
            sums[cell * 3] += (p >> 16) & 0xFF;
            sums[cell * 3 + 1] += (p >> 8) & 0xFF;
            sums[cell * 3 + 2] += p & 0xFF;
        }

        var cells = new List<int>();
        for (var c = 0; c < counts.Length; c++)
        {
            if (counts[c] > 0)
            {
                cells.Add(c);
            }
        }

        var array = cells.ToArray();
        var boxes = new List<(int Start, int End)> { (0, array.Length) };
        var scores = new List<(int Range, long Count)> { Score(array, counts, 0, array.Length) }; // 每个盒子的分数只算一次
        while (boxes.Count < maxColors)
        {
            // 切范围最大的盒子(范围相同时切像素多的);只有一个格子的盒子不能再切
            var best = -1;
            for (var b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].End - boxes[b].Start >= 2
                    && (best < 0 || scores[b].Range > scores[best].Range
                        || (scores[b].Range == scores[best].Range && scores[b].Count > scores[best].Count)))
                {
                    best = b;
                }
            }

            if (best < 0)
            {
                break;
            }

            var box = boxes[best];
            var (channel, _) = WidestChannel(array, box.Start, box.End);
            Array.Sort(array, box.Start, box.End - box.Start, Comparer<int>.Create((a, b) => Channel(a, channel).CompareTo(Channel(b, channel))));

            // 按像素数找中位:前半段累计到一半为止,两边都至少留一个格子
            var half = scores[best].Count / 2;
            var split = box.Start + 1;
            for (long acc = counts[array[box.Start]]; split < box.End - 1 && acc < half; split++)
            {
                acc += counts[array[split]];
            }

            boxes[best] = (box.Start, split);
            scores[best] = Score(array, counts, box.Start, split);
            boxes.Add((split, box.End));
            scores.Add(Score(array, counts, split, box.End));
        }

        var palette = new uint[boxes.Count];
        var lookup = new byte[32768];
        for (var b = 0; b < boxes.Count; b++)
        {
            long n = 0, r = 0, g = 0, bl = 0;
            for (var i = boxes[b].Start; i < boxes[b].End; i++)
            {
                var c = array[i];
                lookup[c] = (byte)b;
                n += counts[c];
                r += sums[c * 3];
                g += sums[c * 3 + 1];
                bl += sums[c * 3 + 2];
            }

            n = Math.Max(1, n);
            palette[b] = (uint)((r / n) << 16 | (g / n) << 8 | (bl / n));
        }

        var transparent = (byte)Math.Min(maxColors, 255);
        for (var i = 0; i < pixels.Length; i++)
        {
            indices[i] = pixels[i] >> 24 == 0 ? transparent : lookup[Cell(pixels[i])];
        }

        return palette;
    }

    private static int Cell(uint p) => (int)(((p >> 19) & 0x1F) << 10 | ((p >> 11) & 0x1F) << 5 | ((p >> 3) & 0x1F));

    private static int Channel(int cell, int channel) => (cell >> (10 - channel * 5)) & 0x1F;

    private static (int Channel, int Range) WidestChannel(int[] cells, int start, int end)
    {
        var best = (Channel: 0, Range: -1);
        for (var channel = 0; channel < 3; channel++)
        {
            int lo = 31, hi = 0;
            for (var i = start; i < end; i++)
            {
                var v = Channel(cells[i], channel);
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }

            if (hi - lo > best.Range)
            {
                best = (channel, hi - lo);
            }
        }

        return best;
    }

    private static (int Range, long Count) Score(int[] cells, int[] counts, int start, int end) =>
        (WidestChannel(cells, start, end).Range, Count(cells, counts, start, end));

    private static long Count(int[] cells, int[] counts, int start, int end)
    {
        long total = 0;
        for (var i = start; i < end; i++)
        {
            total += counts[cells[i]];
        }

        return total;
    }
}
