using Waio.Feature.Launcher.Domain;
using Waio.Feature.Launcher.UI;

namespace Waio.Feature.Launcher.Tests;

/// <summary>文件结果的极简 / 详细样式(对齐 Everything,默认极简,可配置)。</summary>
public class RowStyleTests
{
    [Fact]
    public void 默认是极简样式() => Assert.Equal(FileResultStyle.Compact, new LauncherSettings().FileResultStyle);

    [Fact]
    public void 极简一行贴紧_详细两行()
    {
        var file = LauncherItem.File(Path.Combine(Path.GetTempPath(), "a.txt"), "a.txt", isDirectory: false);

        var compact = LauncherRow.ForItem(file, 0, compact: true);
        var detailed = LauncherRow.ForItem(file, 0);

        Assert.Equal(LauncherRow.CompactHeight, compact.Height);
        Assert.Equal(0, compact.RowPadding.Top);
        Assert.Equal(LauncherRow.ItemHeight, detailed.Height);
        Assert.True(compact.Height * 1.8 < detailed.Height, "极简样式至少省掉近一半高度");
        Assert.False(LauncherRow.Header("文件").IsCompact);
    }
}
