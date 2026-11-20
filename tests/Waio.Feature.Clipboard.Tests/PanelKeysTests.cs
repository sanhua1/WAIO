using Waio.Feature.Clipboard;
using Waio.Feature.Clipboard.Domain;

namespace Waio.Feature.Clipboard.Tests;

public class PanelKeysTests
{
    private static PanelContext Explicit(bool searching = false, bool composing = false, bool preview = false, bool queryEmpty = true) =>
        new(SearchMode.Explicit, searching, composing, preview, queryEmpty);

    private static PanelContext Typing(bool composing = false, bool preview = false, bool queryEmpty = true) =>
        new(SearchMode.TypeToSearch, SearchFocused: true, composing, preview, queryEmpty);

    private static PanelKeyInput K(PanelKey key, bool ctrl = false, bool shift = false) => new(key, ctrl, shift);

    private static PanelKeyInput Digit(int n) => new(PanelKey.Digit, Ctrl: true, Digit: n);

    private static PanelActionKind Act(PanelKeyInput key, PanelContext ctx) => PanelKeys.Resolve(key, ctx).Kind;

    // ---- 输入法组字:一律不处理 ----

    [Theory]
    [InlineData("Enter")]
    [InlineData("Space")]
    [InlineData("Backspace")]
    [InlineData("Delete")]
    [InlineData("Escape")]
    [InlineData("Up")]
    [InlineData("Down")]
    public void 输入法组字期间_所有按键都交给输入法(string keyName)
    {
        var key = Enum.Parse<PanelKey>(keyName);
        Assert.Equal(PanelActionKind.None, Act(K(key), Explicit(searching: true, composing: true)));
        Assert.Equal(PanelActionKind.None, Act(K(key), Typing(composing: true)));
    }

    [Fact]
    public void 输入法组字期间_Ctrl组合键也不处理()
    {
        Assert.Equal(PanelActionKind.None, Act(Digit(1), Explicit(composing: true)));
        Assert.Equal(PanelActionKind.None, Act(K(PanelKey.P, ctrl: true), Explicit(composing: true)));
    }

    // ---- 通用 ----

    [Fact]
    public void 焦点在列表时_上下键移动选择()
    {
        foreach (var ctx in new[] { Explicit(), Explicit(queryEmpty: false), Typing() })
        {
            Assert.Equal(PanelActionKind.MoveUp, Act(K(PanelKey.Up), ctx));
            Assert.Equal(PanelActionKind.MoveDown, Act(K(PanelKey.Down), ctx));
        }
    }

    [Fact]
    public void explicit_焦点在搜索框时_下键回到列表_上键交给文本框()
    {
        Assert.Equal(PanelActionKind.EnterList, Act(K(PanelKey.Down), Explicit(searching: true)));
        Assert.Equal(PanelActionKind.EnterList, Act(K(PanelKey.Down), Explicit(searching: true, queryEmpty: false)));
        Assert.Equal(PanelActionKind.None, Act(K(PanelKey.Up), Explicit(searching: true)));
    }

    // ---- 空格预览的两种方式 ----

    private static PanelContext Hold(bool preview = false) =>
        new(SearchMode.Explicit, false, false, preview, true, PreviewMode.Hold);

    [Fact]
    public void 空格自动重复_吃掉_不开关预览也不漏给列表()
    {
        Assert.Equal(PanelActionKind.Swallow, Act(new PanelKeyInput(PanelKey.Space, Repeat: true), Explicit(preview: true)));
        Assert.Equal(PanelActionKind.Swallow, Act(new PanelKeyInput(PanelKey.Space, Repeat: true), Hold(preview: true)));
    }

    [Fact]
    public void 按住模式_按下打开预览_松开关闭()
    {
        Assert.Equal(PanelActionKind.OpenPreview, Act(K(PanelKey.Space), Hold()));
        Assert.Equal(PanelActionKind.ClosePreview, PanelKeys.ResolveKeyUp(K(PanelKey.Space), Hold(preview: true)).Kind);
        Assert.Equal(PanelActionKind.None, PanelKeys.ResolveKeyUp(K(PanelKey.Space), Hold(preview: false)).Kind);
    }

    [Fact]
    public void 切换模式_松开空格不做任何事()
    {
        Assert.Equal(PanelActionKind.None, PanelKeys.ResolveKeyUp(K(PanelKey.Space), Explicit(preview: true)).Kind);
    }

    [Fact]
    public void 按住模式_在搜索框打字时空格仍是空格()
    {
        var ctx = new PanelContext(SearchMode.Explicit, true, false, false, false, PreviewMode.Hold);
        Assert.Equal(PanelActionKind.None, Act(K(PanelKey.Space), ctx));
        Assert.Equal(PanelActionKind.None, PanelKeys.ResolveKeyUp(K(PanelKey.Space), ctx).Kind);
    }

    [Fact]
    public void Ctrl数字_快速粘贴对应编号()
    {
        var action = PanelKeys.Resolve(Digit(3), Explicit());

        Assert.Equal(PanelActionKind.QuickPaste, action.Kind);
        Assert.Equal(3, action.Slot);
        Assert.Equal(0, PanelKeys.Resolve(Digit(0), Typing()).Slot);
    }

    [Fact]
    public void Ctrl_P固定_Ctrl_W关闭()
    {
        Assert.Equal(PanelActionKind.TogglePin, Act(K(PanelKey.P, ctrl: true), Explicit()));
        Assert.Equal(PanelActionKind.TogglePin, Act(K(PanelKey.P, ctrl: true), Typing()));
        Assert.Equal(PanelActionKind.Close, Act(K(PanelKey.W, ctrl: true), Explicit(searching: true)));
    }

    [Fact]
    public void 其他Ctrl组合_交给文本框()
    {
        // Ctrl+A / C / V 在搜索框里要照常可用;这里没有对应的 PanelKey,统一为 Other
        Assert.Equal(PanelActionKind.None, Act(K(PanelKey.Other, ctrl: true), Explicit(searching: true)));
    }

    // ---- explicit 模式(默认) ----

    [Fact]
    public void explicit_列表里按回车粘贴_Shift回车粘贴纯文本()
    {
        Assert.Equal(PanelActionKind.Paste, Act(K(PanelKey.Enter), Explicit()));
        Assert.Equal(PanelActionKind.PastePlain, Act(K(PanelKey.Enter, shift: true), Explicit()));
    }

    [Fact]
    public void explicit_搜索中回车只是结束搜索_不粘贴()
    {
        Assert.Equal(PanelActionKind.EndSearch, Act(K(PanelKey.Enter), Explicit(searching: true)));
        Assert.Equal(PanelActionKind.EndSearch, Act(K(PanelKey.Enter, shift: true), Explicit(searching: true)));
    }

    [Fact]
    public void explicit_Ctrl_F开始搜索()
    {
        Assert.Equal(PanelActionKind.StartSearch, Act(K(PanelKey.F, ctrl: true), Explicit()));
    }

    [Fact]
    public void explicit_空格预览_删除键删条目()
    {
        Assert.Equal(PanelActionKind.TogglePreview, Act(K(PanelKey.Space), Explicit()));
        Assert.Equal(PanelActionKind.Delete, Act(K(PanelKey.Backspace), Explicit()));
        Assert.Equal(PanelActionKind.Delete, Act(K(PanelKey.Delete), Explicit()));
        Assert.Equal(PanelActionKind.Delete, Act(K(PanelKey.Backspace, ctrl: true), Explicit()));
    }

    [Fact]
    public void explicit_搜索框有焦点时_空格和删除是打字不是预览和删条目()
    {
        var ctx = Explicit(searching: true, queryEmpty: false);

        Assert.Equal(PanelActionKind.None, Act(K(PanelKey.Space), ctx));
        Assert.Equal(PanelActionKind.None, Act(K(PanelKey.Backspace), ctx));
        Assert.Equal(PanelActionKind.None, Act(K(PanelKey.Delete), ctx));
        Assert.Equal(PanelActionKind.None, Act(K(PanelKey.Backspace, ctrl: true), ctx)); // Ctrl+Backspace 删词
    }

    [Fact]
    public void explicit_Esc逐层退出_预览_搜索_面板()
    {
        Assert.Equal(PanelActionKind.ClosePreview, Act(K(PanelKey.Escape), Explicit(preview: true, searching: true, queryEmpty: false)));
        Assert.Equal(PanelActionKind.ClearSearch, Act(K(PanelKey.Escape), Explicit(searching: true, queryEmpty: false)));
        Assert.Equal(PanelActionKind.ClearSearch, Act(K(PanelKey.Escape), Explicit(searching: false, queryEmpty: false))); // 搜索结果保留着
        Assert.Equal(PanelActionKind.Close, Act(K(PanelKey.Escape), Explicit()));
    }

    // ---- typeToSearch 模式 ----

    [Fact]
    public void typeToSearch_回车直接粘贴()
    {
        Assert.Equal(PanelActionKind.Paste, Act(K(PanelKey.Enter), Typing()));
        Assert.Equal(PanelActionKind.Paste, Act(K(PanelKey.Enter), Typing(queryEmpty: false)));
        Assert.Equal(PanelActionKind.PastePlain, Act(K(PanelKey.Enter, shift: true), Typing()));
    }

    [Fact]
    public void typeToSearch_搜索框为空时空格预览_删除键删条目()
    {
        Assert.Equal(PanelActionKind.TogglePreview, Act(K(PanelKey.Space), Typing(queryEmpty: true)));
        Assert.Equal(PanelActionKind.Delete, Act(K(PanelKey.Backspace), Typing(queryEmpty: true)));
    }

    [Fact]
    public void typeToSearch_有搜索文字时空格和删除是打字()
    {
        Assert.Equal(PanelActionKind.None, Act(K(PanelKey.Space), Typing(queryEmpty: false)));
        Assert.Equal(PanelActionKind.None, Act(K(PanelKey.Backspace), Typing(queryEmpty: false)));
    }

    [Fact]
    public void typeToSearch_Esc先关预览_再关面板()
    {
        Assert.Equal(PanelActionKind.ClosePreview, Act(K(PanelKey.Escape), Typing(preview: true)));
        Assert.Equal(PanelActionKind.Close, Act(K(PanelKey.Escape), Typing()));
        Assert.Equal(PanelActionKind.Close, Act(K(PanelKey.Escape), Typing(queryEmpty: false)));
    }

    [Fact]
    public void typeToSearch_Ctrl_F不做事_已经在搜索了()
    {
        Assert.Equal(PanelActionKind.None, Act(K(PanelKey.F, ctrl: true), Typing()));
    }

    [Fact]
    public void 普通字符键_不处理_交给文本框()
    {
        Assert.Equal(PanelActionKind.None, Act(K(PanelKey.Other), Explicit(searching: true)));
        Assert.Equal(PanelActionKind.None, Act(K(PanelKey.Other), Typing()));
    }
}

