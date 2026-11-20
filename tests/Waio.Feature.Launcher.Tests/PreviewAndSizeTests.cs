using System.Text;
using Waio.Feature.Launcher.Domain;
using Waio.Feature.Launcher.Panel;
using Waio.UI;

namespace Waio.Feature.Launcher.Tests;

/// <summary>面板大小(按显示器百分比 + 逻辑像素上下限)和预览规则。</summary>
public class PreviewAndSizeTests
{
    private static ScreenRect Bounds(int workWidth, int workHeight, double scale, int widthPercent = 40, int heightPercent = 40) =>
        PanelGeometry.Bounds(new PanelPlacement(new ScreenRect(0, 0, workWidth, workHeight), scale, widthPercent, heightPercent), 56);

    [Fact]
    public void 四K_150缩放_正好是工作区的40百分比()
    {
        var bounds = Bounds(3840, 2112, 1.5);

        Assert.Equal(1536, bounds.Width);
        Assert.Equal(845, bounds.Height);
        Assert.Equal((3840 - 1536) / 2, bounds.Left); // 水平居中
        Assert.True(PanelGeometry.ShowsPreview(bounds.Width / 1.5));
    }

    [Fact]
    public void 小屏高缩放_不低于逻辑像素下限_列表和预览放得下()
    {
        var bounds = Bounds(1920, 1032, 1.5); // 40% 只有 512 × 275 逻辑像素

        Assert.Equal(1140, bounds.Width); // 760 逻辑像素
        Assert.Equal(630, bounds.Height); // 420 逻辑像素
        Assert.True(PanelGeometry.ShowsPreview(bounds.Width / 1.5));
    }

    [Fact]
    public void 大屏低缩放_不超过逻辑像素上限()
    {
        var bounds = Bounds(3840, 2112, 1.0);

        Assert.Equal(1400, bounds.Width);
        Assert.Equal(845, bounds.Height);
    }

    [Fact]
    public void 极小屏幕_不超出工作区_太窄时收起预览()
    {
        var bounds = Bounds(640, 480, 1.0);

        Assert.Equal(608, bounds.Width); // 工作区的 95%
        Assert.True(bounds.Top >= 0 && bounds.Bottom <= 480);
        Assert.False(PanelGeometry.ShowsPreview(bounds.Width));
    }

    [Fact]
    public void 百分比越界_夹到合理范围()
    {
        Assert.Equal(PanelGeometry.MinPercent, PanelGeometry.ClampPercent(1));
        Assert.Equal(PanelGeometry.MaxPercent, PanelGeometry.ClampPercent(500));
        Assert.Equal(Bounds(3840, 2112, 1.5, 90, 90), Bounds(3840, 2112, 1.5, 300, 300));
    }

    [Fact]
    public void 搜索框在黄金分割附近_面板不超出工作区底部()
    {
        var bounds = Bounds(3840, 2112, 1.5, 40, 90);

        Assert.True(bounds.Bottom <= 2112);
        Assert.True(bounds.Top >= 0);
    }

    [Theory]
    [InlineData(@"C:\a\readme.MD", "Text")]
    [InlineData(@"C:\a\app.log", "Text")]
    [InlineData(@"C:\a\.gitignore", "Text")]
    [InlineData(@"C:\a\photo.JPG", "Image")]
    [InlineData(@"C:\a\pic.webp", "Image")]
    [InlineData(@"C:\a\setup.exe", "None")]
    [InlineData(@"C:\a\report.docx", "None")]
    [InlineData(@"C:\a\noext", "None")]
    public void 按扩展名决定怎么预览(string path, string kind) => Assert.Equal(kind, PreviewRules.KindOf(path).ToString());

    [Fact]
    public void 文本解码_UTF8_带或不带BOM()
    {
        Assert.Equal("你好 abc", PreviewRules.Decode(Encoding.UTF8.GetBytes("你好 abc")));
        Assert.Equal("你好", PreviewRules.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("你好")]));
    }

    [Fact]
    public void 文本解码_UTF16_按BOM()
    {
        Assert.Equal("中文", PreviewRules.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("中文")]));
        Assert.Equal("中文", PreviewRules.Decode([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("中文")]));
    }

    [Fact]
    public void 文本解码_不是UTF8就按GB18030_中文老文件()
    {
        Assert.Equal("中文", PreviewRules.Decode([0xD6, 0xD0, 0xCE, 0xC4]));
    }

    [Fact]
    public void 含NUL的当作二进制_不预览()
    {
        Assert.Null(PreviewRules.Decode([0x4D, 0x5A, 0x00, 0x01]));
    }

    [Fact]
    public void 截断处切开了多字节字符_丢掉半个字_不误判成GB18030()
    {
        var bytes = Encoding.UTF8.GetBytes("中文"); // E4 B8 AD E6 96 87

        Assert.Equal("中", PreviewRules.Decode(bytes.AsSpan(0, 5)));
        Assert.Equal("中", PreviewRules.Decode(bytes.AsSpan(0, 4)));
    }

    [Theory]
    [InlineData(512L, "512 字节")]
    [InlineData(12_595L, "12.3 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    public void 文件大小的写法(long bytes, string text) => Assert.Equal(text, PreviewRules.FormatSize(bytes));
}
