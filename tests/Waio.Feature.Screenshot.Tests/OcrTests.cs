using Microsoft.Extensions.Logging.Abstractions;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Feature.Screenshot.UI;

namespace Waio.Feature.Screenshot.Tests;

/// <summary>识别文字(第 4 阶段):拼行规则移植 MAIO OCRLayoutTests(同输入同期望,坐标是归一化的,规则与单位无关)。</summary>
public sealed class OcrTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "waio-screenshot-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static RecognizedLine Line(string text, double y, double x = 0.02, double width = 0.5, double height = 0.08) =>
        new(text, new TextBounds(x, y, width, height));

    private static RecognizedWord Word(string text, double x) => new(text, new TextBounds(x, 0, 10, 10));

    [Fact]
    public void 从上到下排列行()
    {
        Assert.Equal("第一行\n第二行", OcrLayout.Text([Line("第二行", 0.12), Line("第一行", 0.02)]));
    }

    [Fact]
    public void 同一行的左右两栏从左到右用空格连接()
    {
        // MAIO 实验数据:左栏比右栏稍高一点,中心仍在同一行范围内
        var lines = new[]
        {
            Line("Right column text", 0.427, x: 0.55, width: 0.2, height: 0.083),
            Line("左栏文字", 0.432, x: 0.022, width: 0.09, height: 0.07),
        };
        Assert.Equal("左栏文字 Right column text", OcrLayout.Text(lines));
    }

    [Fact]
    public void 行距大时用空行分段()
    {
        Assert.Equal("Title\n\nBody one\nBody two", OcrLayout.Text([Line("Title", 0.0), Line("Body one", 0.3), Line("Body two", 0.4)]));
    }

    [Fact]
    public void 丢掉空白结果并去掉首尾空格()
    {
        Assert.Equal("hello", OcrLayout.Text([Line("  hello ", 0.0), Line("   ", 0.1)]));
    }

    [Fact]
    public void 没有结果时是空文字()
    {
        Assert.Equal("", OcrLayout.Text([]));
    }

    [Fact]
    public void 中文词之间不加空格_中英之间和英文之间加空格()
    {
        var line = OcrLayout.JoinWords([Word("截图", 0), Word("识别", 12), Word("Windows", 24), Word("OCR", 60), Word("，", 70), Word("好", 80)]);
        Assert.Equal("截图识别 Windows OCR，好", line.Text);
        Assert.Equal(new TextBounds(0, 0, 90, 10), line.Box);
    }

    [Fact]
    public void 识别动作不复制图片_确认提示随主动作变()
    {
        Assert.True(CaptureAction.Ocr.RecognizesText());
        Assert.True(CaptureAction.Translate.RecognizesText());
        Assert.False(CaptureAction.Copy.RecognizesText());
        Assert.Equal("识别文字(⏎)", CaptureAction.Ocr.ConfirmHelp());
        Assert.Equal("复制(⏎)", CaptureAction.Pin.ConfirmHelp());
    }

    [Fact]
    public void 放大两倍_纯色不变_尺寸翻倍()
    {
        var resized = TextRecognizer.Resize(PixelBuffer.Solid(3, 2, new SampledColor(10, 20, 30)), 2);
        Assert.Equal((6, 4), (resized.Width, resized.Height));
        Assert.Equal(new SampledColor(10, 20, 30), resized.ColorAt(5, 3));
    }

    [Fact]
    public void 结果窗口大小_按文字多少估算并有上下限()
    {
        Assert.Equal((420.0, 260.0), OcrResultWindow.SizeFor("短"));
        Assert.Equal((720.0, 600.0), OcrResultWindow.SizeFor(string.Join('\n', Enumerable.Repeat(new string('字', 200), 40))));
    }

    [Fact]
    public async Task 识别出的文字写入历史_可以按它搜索()
    {
        var store = new HistoryStore(_dir, NullLogger.Instance);
        var record = await store.AddAsync(new HistoryStore.Entry([1], [2], 4, 4, 1), "notepad", DateTimeOffset.Now);
        await store.SetOcrTextAsync(record.Id, "发票号码 12345");

        var reopened = await new HistoryStore(_dir, NullLogger.Instance).RecordsAsync();
        Assert.Equal("发票号码 12345", reopened[0].OcrText);
        Assert.Single(HistorySearch.Filter(reopened, "发票"));
    }

    // ---- PP-OCR 前后处理(ADR-0019) ----

    [Fact]
    public void 检测输入尺寸_取32的倍数_最长边不超过上限()
    {
        Assert.Equal((2048, 256), OcrPostProcess.DetectionSize(2064, 266, 2560));
        Assert.Equal((1920, 1024), OcrPostProcess.DetectionSize(2588, 1400, 1920));
        Assert.Equal((32, 32), OcrPostProcess.DetectionSize(10, 5, 1920));
    }

    [Fact]
    public void 概率图找框_两个文字块各成一框_低概率和太小的丢掉()
    {
        const int width = 40, height = 20;
        var map = new float[width * height];
        void Fill(int x0, int y0, int x1, int y1, float value)
        {
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    map[y * width + x] = value;
                }
            }
        }

        Fill(5, 5, 15, 9, 0.9f); // 文字块 10×4
        Fill(20, 10, 36, 14, 0.8f); // 文字块 16×4
        Fill(2, 14, 10, 18, 0.4f); // 超过二值化阈值但平均概率不够
        Fill(30, 2, 32, 4, 0.9f); // 2×2,太小

        var boxes = OcrPostProcess.Boxes(map, width, height).OrderBy(b => b.X).ToList();
        Assert.Equal(2, boxes.Count);
        // 外扩距离 = 10×4×1.6 / (2×14) ≈ 2.29
        Assert.Equal(5 - 40 * 1.6 / 28, boxes[0].X, 3);
        Assert.Equal(15 + 40 * 1.6 / 28, boxes[0].Right, 3);
        Assert.True(boxes[1].X < 20 && boxes[1].Right > 36);
    }

    [Fact]
    public void CTC解码_去掉空白和连续重复_末类是空格()
    {
        string[] dict = ["a", "b"]; // 类别:0 空白,1 a,2 b,3 空格
        float[][] steps = [[0, 0.9f, 0, 0], [0, 0.8f, 0, 0], [0.9f, 0, 0, 0], [0, 0.7f, 0, 0], [0, 0, 0, 0.6f], [0, 0, 1f, 0]];
        var (text, confidence) = OcrPostProcess.DecodeCtc(steps.SelectMany(s => s).ToArray(), steps.Length, 4, dict);
        Assert.Equal("aa b", text);
        Assert.Equal((0.9f + 0.7f + 0.6f + 1f) / 4, confidence, 3);
    }

    /// <summary>
    /// 真正跑 PP-OCRv5(离线,不碰桌面):渲染容易认错的内容(快捷键、百分号、编号,中英混排),100% 和 150% 两种缩放。
    /// 系统识别在这类内容上会出 Ctrt+0、1％ 之类的错(ADR-0019 的实验)。模型由 check.ps1 先下载。
    /// 样例避开 "Alt":渲染用的无衬线字体里大写 I 和小写 l 几乎一样,PP-OCR 也会认成 "AIt"(已知局限,见 ADR-0019)。
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public async Task PP_OCR识别中英混排的快捷键和数字(double scale)
    {
        Assert.True(PaddleOcr.ModelsPresent, $"缺少识别模型,先运行 scripts/fetch-ocr-models.ps1({PaddleOcr.ModelDirectory})");
        var image = ClipboardImage.RenderText("框选后按 Ctrl+O 识别文字\n缩放 150% 时 Win+F1 截图,见 ADR-0018", scale);

        var (text, fallback) = await TextRecognizer.RecognizeAsync(image);

        Assert.Null(fallback);
        var compact = text.Replace(" ", "", StringComparison.Ordinal).Replace('，', ',');
        Assert.Contains("框选后按Ctrl+O识别文字", compact);
        Assert.Contains("缩放150%时Win+F1截图,见ADR-0018", compact);
        Assert.Equal(2, text.Split('\n').Length);
    }

    /// <summary>系统识别(模型缺失时的退路)还能用。系统没装任何识别组件时只检查报错内容。</summary>
    [Fact]
    public async Task 系统识别退路_能认出中文()
    {
        var image = ClipboardImage.RenderText("截图识别文字", 1);
        try
        {
            Assert.Contains("截图识别文字", await TextRecognizer.RecognizeWithWindowsAsync(image));
        }
        catch (TextRecognitionException ex)
        {
            Assert.Contains("没有可用的文字识别引擎", ex.Message);
        }
    }

}
