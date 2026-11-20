using Microsoft.ML.OnnxRuntime;
using Waio.Feature.Screenshot.Domain;

namespace Waio.Feature.Screenshot.Infrastructure;

/// <summary>
/// 用 PP-OCRv5 mobile 模型(ONNX Runtime,CPU)识别文字(ADR-0019):检测文字框 → 逐个识别 → 交给 <see cref="OcrLayout"/> 拼成文字。
/// 模型由 <c>scripts/fetch-ocr-models.ps1</c> 下载,随程序集放在 <c>Ocr\</c> 子目录。会话第一次用时创建后一直复用,<c>Run</c> 是线程安全的。
/// 本机实测(32 核,用户的真实截图,2026-11-11):2000×260 的一段文字约 0.45 秒,2588×1400 整屏(55 行)约 1.7 秒。
/// </summary>
internal sealed class PaddleOcr : IDisposable
{
    /// <summary>检测模型输入的最长边。截图通常不超过它;更大的整屏截图缩小后文字仍有十几像素高,够用。</summary>
    internal const int DetectionMaxSide = 1920;

    private const int RecognitionHeight = 48;
    private const int RecognitionMinWidth = 320;

    /// <summary>识别结果平均概率低于它的行丢掉(RapidOCR text_score)。</summary>
    private const float MinConfidence = 0.5f;

    // 检测模型的归一化(ImageNet 均值方差,按 B、G、R 通道顺序,与 PaddleOCR 用 OpenCV 读图一致)
    private static readonly float[] DetMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] DetStd = [0.229f, 0.224f, 0.225f];
    private static readonly float[] RecMean = [0.5f, 0.5f, 0.5f];
    private static readonly float[] RecStd = [0.5f, 0.5f, 0.5f];

    private static readonly Lock Gate = new();
    private static PaddleOcr? _shared;

    private readonly InferenceSession _detector;
    private readonly InferenceSession _recognizer;
    private readonly string[] _characters;

    private PaddleOcr(string directory)
    {
        // 检测一次只跑一张大图,用满默认线程;识别是很多行并行跑,每次只给少量线程,避免几路并发抢同一个线程池
        using var detectorOptions = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        using var recognizerOptions = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = RecognitionThreads };
        _detector = new InferenceSession(Path.Combine(directory, "det.onnx"), detectorOptions);
        recognizerOptions.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        _recognizer = new InferenceSession(Path.Combine(directory, "rec.onnx"), recognizerOptions);
        _characters = File.ReadAllLines(Path.Combine(directory, "dict.txt"));
    }

    /// <summary>
    /// 模型所在目录,相对截图功能的程序集(不用 AppContext.BaseDirectory,见 ADR-0019):
    /// 普通引用(测试项目)按 csproj 的 Link 放在 <c>Ocr\</c>;WinUI 应用把类库的内容文件放在 <c>&lt;程序集名&gt;\Assets\Ocr\</c>(不认 Link)。
    /// 两处都没有时返回第一处(用于提示)。
    /// </summary>
    internal static string ModelDirectory { get; } = FindModelDirectory();

    private static string FindModelDirectory()
    {
        var assembly = typeof(PaddleOcr).Assembly;
        var directory = Path.GetDirectoryName(assembly.Location) ?? ".";
        string[] candidates = [Path.Combine(directory, "Ocr"), Path.Combine(directory, assembly.GetName().Name ?? "", "Assets", "Ocr")];
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "rec.onnx"))) ?? candidates[0];
    }

    public static bool ModelsPresent =>
        File.Exists(Path.Combine(ModelDirectory, "det.onnx")) && File.Exists(Path.Combine(ModelDirectory, "rec.onnx"))
        && File.Exists(Path.Combine(ModelDirectory, "dict.txt"));

    /// <summary>
    /// 共用的实例(第一次识别时加载模型,约 0.5 秒,之后常驻,内存约百兆级——未精确测量)。
    /// 功能停用时不释放:可能还有识别在跑,释放会话会让它崩掉;进程退出时由系统回收。
    /// </summary>
    public static PaddleOcr Shared
    {
        get
        {
            lock (Gate)
            {
                return _shared ??= new PaddleOcr(ModelDirectory);
            }
        }
    }

    /// <summary>识别全部文字段(带逐字位置,贴图上选择文字用)。顺序同检测结果,拼文字时交给 <see cref="OcrLayout"/> 排序。</summary>
    public IReadOnlyList<TextSegment> Recognize(PixelBuffer image)
    {
        // 各行互不相关,并行识别(Run 是线程安全的)。耗时和行宽成正比,所以最宽的先开始,免得最后只剩一条长行在跑
        var boxes = Detect(image).Where(b => b.Height >= MinLineHeight).ToList();
        var segments = new TextSegment?[boxes.Count];
        var order = Enumerable.Range(0, boxes.Count).OrderByDescending(i => boxes[i].Width / boxes[i].Height);
        Parallel.ForEach(
            System.Collections.Concurrent.Partitioner.Create(order, System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = RecognitionParallelism },
            i => segments[i] = RecognizeLine(image, boxes[i]));
        return [.. segments.OfType<TextSegment>()];
    }

    /// <summary>识别每次运行用的线程数;并行路数 = 核数 / 它(最多 8 路)。</summary>
    private const int RecognitionThreads = 1;

    /// <summary>比它矮的框(像素,外扩后)不可能是能读的文字(100% 缩放下最小的字外扩后也有十几像素),多是圆点、图标边缘。</summary>
    private const double MinLineHeight = 10;

    private static int RecognitionParallelism { get; } = Math.Clamp(Environment.ProcessorCount / RecognitionThreads, 1, 8);

    /// <summary>检测文字框,返回原图上的像素坐标。</summary>
    internal List<TextBounds> Detect(PixelBuffer image)
    {
        var (width, height) = OcrPostProcess.DetectionSize(image.Width, image.Height, DetectionMaxSide);
        var input = new float[3 * width * height];
        Fill(image, new TextBounds(0, 0, image.Width, image.Height), width, height, width, input, DetMean, DetStd);
        using var tensor = OrtValue.CreateTensorValueFromMemory(input, [1, 3, height, width]);
        using var results = _detector.Run(new RunOptions(), [_detector.InputNames[0]], [tensor], [_detector.OutputNames[0]]);
        var map = results[0].GetTensorDataAsSpan<float>();
        var sx = (double)image.Width / width;
        var sy = (double)image.Height / height;
        return [.. OcrPostProcess.Boxes(map, width, height).Select(b => new TextBounds(b.X * sx, b.Y * sy, b.Width * sx, b.Height * sy))];
    }

    private TextSegment? RecognizeLine(PixelBuffer image, TextBounds box)
    {
        var scaled = (int)Math.Ceiling(RecognitionHeight * box.Width / Math.Max(1, box.Height));
        var width = Math.Max(RecognitionMinWidth, scaled);
        var input = new float[3 * RecognitionHeight * width]; // 右侧不足的部分保持 0(归一化后的中灰),同 PaddleOCR 的补边
        Fill(image, box, scaled, RecognitionHeight, width, input, RecMean, RecStd);
        using var tensor = OrtValue.CreateTensorValueFromMemory(input, [1, 3, RecognitionHeight, width]);
        using var results = _recognizer.Run(new RunOptions(), [_recognizer.InputNames[0]], [tensor], [_recognizer.OutputNames[0]]);
        var shape = results[0].GetTensorTypeAndShape().Shape; // [1, 步数, 类别数]
        var steps = (int)shape[1];
        var (decoded, confidence) = OcrPostProcess.DecodeCtcWithSteps(results[0].GetTensorDataAsSpan<float>(), steps, (int)shape[2], _characters);
        var chars = OcrPostProcess.CharacterSpans(decoded, steps, width, scaled, box);

        // 去掉首尾的空格(同以前的 Trim)
        var first = chars.FindIndex(c => !string.IsNullOrWhiteSpace(c.Value));
        var last = chars.FindLastIndex(c => !string.IsNullOrWhiteSpace(c.Value));
        return confidence >= MinConfidence && first >= 0 ? new TextSegment(box, chars[first..(last + 1)]) : null;
    }

    /// <summary>
    /// 把原图的 <paramref name="source"/> 区域双线性缩放成 <paramref name="width"/>×<paramref name="height"/>,
    /// 按 B、G、R 三个通道归一化后写入 CHW 张量(张量每行宽 <paramref name="stride"/>)。
    /// </summary>
    private static void Fill(PixelBuffer image, TextBounds source, int width, int height, int stride, float[] target, float[] mean, float[] std)
    {
        var pixels = image.Pixels;
        var plane = stride * height;
        var scaleX = source.Width / width;
        var scaleY = source.Height / height;

        // 每列的采样位置只算一次
        var left = new int[width];
        var right = new int[width];
        var weight = new float[width];
        for (var x = 0; x < width; x++)
        {
            var sx = Math.Clamp(source.X + (x + 0.5) * scaleX - 0.5, 0, image.Width - 1);
            left[x] = (int)sx * 4;
            right[x] = Math.Min((int)sx + 1, image.Width - 1) * 4;
            weight[x] = (float)(sx - (int)sx);
        }

        var scale = new float[3];
        var offset = new float[3];
        for (var c = 0; c < 3; c++)
        {
            scale[c] = 1 / (255f * std[c]);
            offset[c] = mean[c] / std[c];
        }

        Parallel.For(0, height, y =>
        {
            var sy = Math.Clamp(source.Y + (y + 0.5) * scaleY - 0.5, 0, image.Height - 1);
            var top = (int)sy * image.Stride;
            var bottom = Math.Min((int)sy + 1, image.Height - 1) * image.Stride;
            var fy = (float)(sy - (int)sy);
            for (var c = 0; c < 3; c++)
            {
                var row = c * plane + y * stride;
                for (var x = 0; x < width; x++)
                {
                    var fx = weight[x];
                    var upper = pixels[top + left[x] + c] + (pixels[top + right[x] + c] - pixels[top + left[x] + c]) * fx;
                    var lower = pixels[bottom + left[x] + c] + (pixels[bottom + right[x] + c] - pixels[bottom + left[x] + c]) * fx;
                    target[row + x] = (upper + (lower - upper) * fy) * scale[c] - offset[c];
                }
            }
        });
    }

    public void Dispose()
    {
        _detector.Dispose();
        _recognizer.Dispose();
    }
}
