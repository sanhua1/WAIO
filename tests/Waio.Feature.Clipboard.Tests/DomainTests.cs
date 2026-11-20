using Waio.Feature.Clipboard;
using Waio.Feature.Clipboard.Domain;
using Waio.Kit.Services;
using Waio.UI;

namespace Waio.Feature.Clipboard.Tests;

public class ClipTextTests
{
    [Theory]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("  hello \n", "hello")]
    public void 规范化_统一换行并去掉首尾空白(string input, string expected)
    {
        Assert.Equal(expected, ClipText.Normalized(input));
    }

    [Theory]
    [InlineData("hello", "hello world", true)]
    [InlineData("hello world", "hello", true)]
    [InlineData("hello", "  hello  ", true)]
    [InlineData("abc", "xyz", false)]
    [InlineData("", "abc", false)]
    [InlineData("abc", "   ", false)]
    public void 选区增长_一段包含另一段(string old, string next, bool expected)
    {
        Assert.Equal(expected, ClipText.IsSelectionGrowth(old, next));
    }

    [Fact]
    public void 摘要_合并空白并截断()
    {
        Assert.Equal("a b c", ClipText.Summary("a \n\t b   c"));
        var summary = ClipText.Summary(new string('x', 500), limit: 10);
        Assert.Equal(new string('x', 10) + "…", summary);
    }
}

public class ClipContentsTests
{
    [Fact]
    public void 类型判断_文件优先_有富文本算富文本_只有图片算图片()
    {
        Assert.Equal(ClipKind.Files, ClipContents.KindOf(new ClipboardContent { Files = [@"C:\a.txt"], Text = "x" }));
        Assert.Equal(ClipKind.RichText, ClipContents.KindOf(new ClipboardContent { Text = "x", Rtf = [1] }));
        Assert.Equal(ClipKind.RichText, ClipContents.KindOf(new ClipboardContent { Text = "x", Html = [1] }));
        Assert.Equal(ClipKind.Text, ClipContents.KindOf(ClipboardContent.FromText("x")));
        Assert.Equal(ClipKind.Image, ClipContents.KindOf(new ClipboardContent { Image = [1, 2, 3] }));
    }

    [Fact]
    public void 文本和图片同时存在_算文本_如Excel复制区域()
    {
        Assert.Equal(ClipKind.Text, ClipContents.KindOf(new ClipboardContent { Text = "a\tb", Image = [1, 2, 3] }));
    }

    [Fact]
    public void 摘要_文件显示文件名_多个文件显示个数()
    {
        Assert.Equal("a.txt", ClipContents.SummaryOf(ClipboardContent.FromFiles([@"C:\dir\a.txt"])));
        Assert.Equal("a.txt 等 2 个文件", ClipContents.SummaryOf(ClipboardContent.FromFiles([@"C:\dir\a.txt", @"C:\b.txt"])));
        Assert.Equal("图片", ClipContents.SummaryOf(new ClipboardContent { Image = [1] }));
    }

    [Fact]
    public void 去重键_文字忽略换行风格和首尾空白_也忽略RTF有无()
    {
        var plain = ClipboardContent.FromText("hello\nworld");
        var withRtf = new ClipboardContent { Text = "  hello\r\nworld ", Rtf = [1, 2] };

        Assert.Equal(ClipContents.ContentKey(plain), ClipContents.ContentKey(withRtf));
        Assert.NotEqual(ClipContents.ExactHash(plain), ClipContents.ExactHash(withRtf)); // 完整哈希则不同
    }

    [Fact]
    public void 去重键_不同内容不同_文字和文件不会撞()
    {
        var text = ClipContents.ContentKey(ClipboardContent.FromText(@"C:\a.txt"));
        var file = ClipContents.ContentKey(ClipboardContent.FromFiles([@"C:\a.txt"]));

        Assert.NotEqual(text, file);
    }

    [Fact]
    public void 完整哈希_拼接歧义不会撞()
    {
        var a = new ClipboardContent { Text = "ab", Rtf = [99] };
        var b = new ClipboardContent { Text = "a", Rtf = [98, 99] };

        Assert.NotEqual(ClipContents.ExactHash(a), ClipContents.ExactHash(b));
    }

    [Fact]
    public void 搜索文本_小写且被截断()
    {
        Assert.Equal("hello", ClipContents.SearchTextOf(ClipboardContent.FromText("HeLLo")));
        var huge = ClipContents.SearchTextOf(ClipboardContent.FromText(new string('A', ClipContents.MaxSearchChars + 500)));
        Assert.Equal(ClipContents.MaxSearchChars, huge.Length);
    }

    [Fact]
    public void 大小_累加各格式()
    {
        var content = new ClipboardContent { Text = "abc", Image = new byte[100], Rtf = new byte[10] };

        Assert.Equal(3 + 100 + 10, ClipContents.ByteSize(content));
    }
}

public class PickerStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static ClipItem Item(long id, int minutesAgo, bool pinned = false) =>
        new(id, ClipKind.Text, $"item{id}", null, T0.AddMinutes(-minutesAgo), T0.AddMinutes(-minutesAgo), 10, pinned);

    [Fact]
    public void 排序_未固定的最近使用在前_固定的排最后()
    {
        var state = new PickerState();

        state.SetItems([Item(1, 30), Item(2, 5, pinned: true), Item(3, 10), Item(4, 1)]);

        Assert.Equal([4L, 3, 1, 2], state.Items.Select(i => i.Id));
    }

    [Fact]
    public void 默认选中第一条()
    {
        var state = new PickerState();
        state.SetItems([Item(1, 5), Item(2, 1)]);

        Assert.Equal(2, state.Selected?.Id);
    }

    [Fact]
    public void 上下移动_到头循环()
    {
        var state = new PickerState();
        state.SetItems([Item(1, 1), Item(2, 2), Item(3, 3)]);

        state.Move(-1);
        Assert.Equal(2, state.SelectedIndex);
        state.Move(1);
        Assert.Equal(0, state.SelectedIndex);
        state.Move(-4);
        Assert.Equal(2, state.SelectedIndex);
    }

    [Fact]
    public void 空列表_移动和选择不崩溃()
    {
        var state = new PickerState();

        state.Move(1);
        state.Select(5);

        Assert.Null(state.Selected);
    }

    [Fact]
    public void Select_超出范围被夹住()
    {
        var state = new PickerState();
        state.SetItems([Item(1, 1), Item(2, 2)]);

        state.Select(99);
        Assert.Equal(1, state.SelectedIndex);
        state.Select(-3);
        Assert.Equal(0, state.SelectedIndex);
    }

    [Fact]
    public void 替换列表_可选择保持原选中项()
    {
        var state = new PickerState();
        state.SetItems([Item(1, 1), Item(2, 2), Item(3, 3)]);
        state.Select(2); // item3

        state.SetItems([Item(9, 0), Item(3, 3)], keepSelection: true);
        Assert.Equal(3, state.Selected?.Id);

        state.SetItems([Item(9, 0), Item(3, 3)], keepSelection: false);
        Assert.Equal(9, state.Selected?.Id);
    }

    [Fact]
    public void 快速粘贴_只数未固定的_0是第10条()
    {
        var state = new PickerState();
        var items = Enumerable.Range(1, 12).Select(i => Item(i, i)).ToList();
        items.Add(Item(100, 0, pinned: true));
        state.SetItems(items);

        Assert.Equal(1, state.QuickItem(1)?.Id);
        Assert.Equal(9, state.QuickItem(9)?.Id);
        Assert.Equal(10, state.QuickItem(0)?.Id);
        Assert.Null(state.QuickSlotOf(Item(100, 0, pinned: true))); // 固定的没有编号
        Assert.Equal(0, state.QuickSlotOf(state.Items[9]));
        Assert.Null(state.QuickSlotOf(state.Items[10])); // 第 11 条没有
    }

    [Fact]
    public void 删除选中项_选中位置夹回范围()
    {
        var state = new PickerState();
        state.SetItems([Item(1, 1), Item(2, 2)]);
        state.Select(1);

        state.Remove(2);

        Assert.Equal(0, state.SelectedIndex);
        Assert.Equal(1, state.Selected?.Id);
        state.Remove(1);
        Assert.Null(state.Selected);
    }
}

public class RetentionPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static RetentionPolicy.Entry E(long id, long size, int minutesAgo, bool pinned = false) =>
        new(id, size, T0.AddMinutes(-minutesAgo), pinned);

    [Fact]
    public void 超过上限_从最久没用的开始删_直到不超过()
    {
        var removed = RetentionPolicy.OverLimit([E(1, 40, 30), E(2, 40, 20), E(3, 40, 10)], limit: 100);

        Assert.Equal([1L], removed);
    }

    [Fact]
    public void 没超上限或不限制_什么都不删()
    {
        Assert.Empty(RetentionPolicy.OverLimit([E(1, 10, 1)], limit: 100));
        Assert.Empty(RetentionPolicy.OverLimit([E(1, 999, 1)], limit: null));
    }

    [Fact]
    public void 固定的永远不删_即使超限()
    {
        var removed = RetentionPolicy.OverLimit([E(1, 500, 99, pinned: true), E(2, 10, 5)], limit: 100);

        Assert.Equal([2L], removed); // 只能删未固定的,删完仍超限也不动固定的
    }

    [Fact]
    public void 删除最旧的N字节()
    {
        var removed = RetentionPolicy.Oldest([E(1, 30, 30), E(2, 30, 20), E(3, 30, 10)], bytes: 50);

        Assert.Equal([1L, 2], removed); // 30 不够 50,再删一条
    }

    [Fact]
    public void 删除某日期之前_不含固定的()
    {
        var removed = RetentionPolicy.Before(
            [E(1, 1, 100), E(2, 1, 100, pinned: true), E(3, 1, 1)], T0.AddMinutes(-10));

        Assert.Equal([1L], removed);
    }

    [Theory]
    [InlineData(StorageLimit.Mb500, 500_000_000L)]
    [InlineData(StorageLimit.Gb2, 2_000_000_000L)]
    [InlineData(StorageLimit.Gb20, 20_000_000_000L)]
    public void 存储上限换算字节(StorageLimit limit, long expected)
    {
        Assert.Equal(expected, limit.Bytes());
    }

    [Fact]
    public void 永久保留_没有上限()
    {
        Assert.Null(StorageLimit.Unlimited.Bytes());
    }
}

public class PopupPlacementTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void 下方放得下_放在光标行正下方_并左移内边距()
    {
        var anchor = new ScreenRect(500, 300, 502, 320);

        var p = PopupPlacement.TopLeft(anchor, 400, 300, Screen);

        Assert.Equal(new ScreenPoint(500 - PopupPlacement.ContentInset, 320 + PopupPlacement.Gap), p);
    }

    [Fact]
    public void 下方放不下_放到光标行上方_不挡住光标行()
    {
        var anchor = new ScreenRect(500, 900, 502, 920);

        var p = PopupPlacement.TopLeft(anchor, 400, 300, Screen);

        Assert.Equal(900 - PopupPlacement.Gap - 300, p.Y);
    }

    [Fact]
    public void 靠右边缘_水平方向夹在工作区内()
    {
        var anchor = new ScreenRect(1900, 300, 1902, 320);

        var p = PopupPlacement.TopLeft(anchor, 400, 300, Screen);

        Assert.Equal(1920 - 400 - 4, p.X);
    }

    [Fact]
    public void 靠左边缘_不超出工作区左侧()
    {
        var p = PopupPlacement.TopLeft(new ScreenRect(0, 300, 2, 320), 400, 300, Screen);

        Assert.Equal(4, p.X);
    }

    [Fact]
    public void 面板比工作区还高_不会算出负坐标()
    {
        var p = PopupPlacement.TopLeft(new ScreenRect(500, 100, 502, 120), 400, 2000, Screen);

        Assert.True(p.Y >= Screen.Top);
    }

    [Fact]
    public void 居中_在非原点的显示器上也正确()
    {
        var second = new ScreenRect(1920, 0, 3840, 1080);

        var p = PopupPlacement.Centered(400, 300, second);

        Assert.Equal(new ScreenPoint(1920 + (1920 - 400) / 2, (1080 - 300) / 2), p);
    }
}

