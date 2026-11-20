namespace Waio.Feature.Screenshot.Domain;

/// <summary>
/// PP-OCR 的前后处理里与模型无关的纯算法(ADR-0019)。思路参考 PaddleOCR 的 DBPostProcess / CTCLabelDecode(Apache-2.0),
/// 按屏幕截图的特点做了简化:屏幕文字都是水平的,所以文字框用轴对齐矩形,不求最小外接旋转矩形,也不做方向分类。
/// </summary>
public static class OcrPostProcess
{
    /// <summary>概率图二值化阈值(PaddleOCR thresh)。</summary>
    public const float BinaryThreshold = 0.3f;

    /// <summary>文字框平均概率低于它就丢掉(PaddleOCR box_thresh 0.6,RapidOCR 0.5)。</summary>
    public const float BoxThreshold = 0.5f;

    /// <summary>文字框外扩比例(PaddleOCR unclip_ratio 1.5,RapidOCR 1.6)。</summary>
    public const double UnclipRatio = 1.6;

    /// <summary>
    /// 检测模型的输入尺寸:最长边不超过 <paramref name="maxSide"/>,宽高都取 32 的倍数(DB 网络的步长要求),至少 32。
    /// 返回缩放后的宽高(宽高的缩放比例可能略有不同)。
    /// </summary>
    public static (int Width, int Height) DetectionSize(int width, int height, int maxSide)
    {
        var ratio = Math.Max(width, height) > maxSide ? (double)maxSide / Math.Max(width, height) : 1;
        static int Round32(double v) => Math.Max(32, (int)Math.Round(v / 32) * 32);
        return (Round32(width * ratio), Round32(height * ratio));
    }

    /// <summary>
    /// 从概率图里找文字框(连通域 → 外接矩形 → 按平均概率过滤 → 外扩),坐标是概率图上的像素。
    /// <paramref name="probability"/> 是按行存放的 <paramref name="width"/>×<paramref name="height"/> 概率。
    /// </summary>
    public static List<TextBounds> Boxes(ReadOnlySpan<float> probability, int width, int height)
    {
        var boxes = new List<TextBounds>();
        var visited = new bool[width * height];
        var stack = new Stack<int>();
        for (var start = 0; start < probability.Length; start++)
        {
            if (visited[start] || probability[start] <= BinaryThreshold)
            {
                continue;
            }

            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
            double sum = 0;
            visited[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var index = stack.Pop();
                var x = index % width;
                var y = index / width;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                sum += probability[index];
                count++;
                Visit(x - 1, y, probability);
                Visit(x + 1, y, probability);
                Visit(x, y - 1, probability);
                Visit(x, y + 1, probability);
            }

            var w = maxX - minX + 1;
            var h = maxY - minY + 1;
            if (Math.Min(w, h) < 3 || sum / count < BoxThreshold)
            {
                continue;
            }

            // 外扩距离 = 面积 × 比例 / 周长(PaddleOCR unclip,矩形情况)
            var d = w * h * UnclipRatio / (2.0 * (w + h));
            var x0 = Math.Max(0, minX - d);
            var y0 = Math.Max(0, minY - d);
            var x1 = Math.Min(width, maxX + 1 + d);
            var y1 = Math.Min(height, maxY + 1 + d);
            boxes.Add(new TextBounds(x0, y0, x1 - x0, y1 - y0));
        }

        return boxes;

        void Visit(int x, int y, ReadOnlySpan<float> map)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                return;
            }

            var index = y * width + x;
            if (!visited[index] && map[index] > BinaryThreshold)
            {
                visited[index] = true;
                stack.Push(index);
            }
        }
    }

    /// <summary>
    /// CTC 贪心解码(PaddleOCR CTCLabelDecode):每一步取概率最大的类,去掉空白(0)和连续重复。
    /// 类别 k(≥1)对应 <paramref name="characters"/>[k - 1];超出字典的最后一类是空格(use_space_char)。
    /// 返回文字和保留下来的字符的平均概率。
    /// </summary>
    public static (string Text, float Confidence) DecodeCtc(ReadOnlySpan<float> scores, int steps, int classes, IReadOnlyList<string> characters)
    {
        var (chars, confidence) = DecodeCtcWithSteps(scores, steps, classes, characters);
        return (string.Concat(chars.Select(c => c.Value)), confidence);
    }

    /// <summary>同 <see cref="DecodeCtc"/>,另外给出每个字符是在第几步输出的(用来估计每个字的横向位置)。</summary>
    public static (List<(string Value, int Step)> Chars, float Confidence) DecodeCtcWithSteps(
        ReadOnlySpan<float> scores, int steps, int classes, IReadOnlyList<string> characters)
    {
        var text = new List<(string Value, int Step)>();
        var previous = -1;
        float total = 0;
        var kept = 0;
        for (var t = 0; t < steps; t++)
        {
            var row = scores.Slice(t * classes, classes);
            var best = 0;
            for (var c = 1; c < classes; c++)
            {
                if (row[c] > row[best])
                {
                    best = c;
                }
            }

            if (best != 0 && best != previous)
            {
                text.Add((best - 1 < characters.Count ? characters[best - 1] : " ", t));
                total += row[best];
                kept++;
            }

            previous = best;
        }

        return (text, kept == 0 ? 0 : total / kept);
    }

    /// <summary>
    /// 估计每个字的横向范围(原图像素)。识别模型的每一步对应输入图上等宽的一列:第 t 步的中心在
    /// <c>(t + 0.5) × 输入宽 / 步数</c>,再按 <paramref name="contentWidth"/>(缩放后有内容的宽度,右侧是补边)换算回文字框。
    /// 相邻两个字以中心的中点为界;首尾按相邻间距的一半外推,不超出文字框。
    /// </summary>
    public static List<TextChar> CharacterSpans(
        IReadOnlyList<(string Value, int Step)> chars, int steps, int inputWidth, int contentWidth, TextBounds box)
    {
        var centers = chars.Select(c => box.X + (c.Step + 0.5) * inputWidth / steps * box.Width / contentWidth).ToArray();
        var result = new List<TextChar>(chars.Count);
        for (var i = 0; i < chars.Count; i++)
        {
            double left, right;
            if (chars.Count == 1)
            {
                (left, right) = (box.X, box.Right);
            }
            else
            {
                left = i > 0 ? (centers[i - 1] + centers[i]) / 2 : centers[0] - (centers[1] - centers[0]) / 2;
                right = i < chars.Count - 1 ? (centers[i] + centers[i + 1]) / 2 : centers[i] + (centers[i] - centers[i - 1]) / 2;
            }

            result.Add(new TextChar(chars[i].Value, Math.Clamp(left, box.X, box.Right), Math.Clamp(right, box.X, box.Right)));
        }

        return result;
    }
}
