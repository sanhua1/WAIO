using Waio.Feature.Translate.Infrastructure;
using Waio.Kit.Contracts;
using Waio.Kit.Features;
using Waio.Kit.Input;
using Waio.Kit.Services;
using Waio.Testing;

namespace Waio.Feature.Translate.Tests;

/// <summary>读取选中文字(MAIO SelectionReader 的流程)与功能接线。</summary>
public class SelectionAndFeatureTests
{
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeInputService _input = new();

    private SelectionReader Reader(string? accessibility, Action? onCopy = null)
    {
        _input.OnSendKeys = chord =>
        {
            if (chord == KeyChord.Parse("ctrl+c"))
            {
                onCopy?.Invoke();
            }
        };
        return new SelectionReader(_ => Task.FromResult(accessibility), _clipboard, _input);
    }

    [Fact]
    public async Task 辅助功能读到选中文字_直接用_不碰剪贴板()
    {
        var text = await Reader("hello").ReadAsync();

        Assert.Equal("hello", text);
        Assert.Empty(_input.SentKeys);
        Assert.Empty(_clipboard.Written);
    }

    [Fact]
    public async Task 读不到时模拟CtrlC_读完恢复原剪贴板_期间不让剪贴板历史记录()
    {
        _clipboard.Copy("原来的内容");
        var changes = 0;
        ((IClipboardService)_clipboard).Changed += _ => changes++;
        var suppressedDuringCopy = false;

        var text = await Reader(null, () =>
        {
            suppressedDuringCopy = _clipboard.ActiveSuppressions > 0;
            _clipboard.ExternalWrite(ClipboardContent.FromText("选中的文字"));
        }).ReadAsync();

        Assert.Equal("选中的文字", text);
        Assert.Equal([KeyChord.Parse("ctrl+c")], _input.SentKeys);
        Assert.True(suppressedDuringCopy);
        Assert.Equal(0, changes); // 剪贴板历史收不到这次复制
        Assert.Equal("原来的内容", _clipboard.Current?.Text);
        Assert.Equal(0, _clipboard.ActiveSuppressions);
    }

    [Fact]
    public async Task 读不到_而且CtrlC没有让剪贴板变化_返回null_剪贴板原样不动()
    {
        _clipboard.Copy("原来的内容");

        var text = await Reader(" ").ReadAsync();

        Assert.Null(text);
        Assert.Empty(_clipboard.Written);
        Assert.Equal(0, _clipboard.ActiveSuppressions);
    }

    [Fact]
    public async Task 复制出来的只有空白_当作没有选中_但仍恢复剪贴板()
    {
        _clipboard.Copy("原来的内容");

        var text = await Reader(null, () => _clipboard.ExternalWrite(ClipboardContent.FromText("  \n"))).ReadAsync();

        Assert.Null(text);
        Assert.Equal("原来的内容", _clipboard.Current?.Text);
    }

    [Fact]
    public void 清单_默认快捷键AltQ_声明联网_命令前缀一致()
    {
        var manifest = TranslateFeature.Manifest;

        Assert.Equal("translate", manifest.Id.Value);
        Assert.True(manifest.RequiredCapabilities.HasFlag(Capability.Network));
        Assert.All(manifest.Commands, c => Assert.Equal("translate", c.Id.FeaturePart));
        var binding = Assert.Single(manifest.DefaultBindings);
        Assert.Equal(KeyChord.Parse("win+t"), ((HotkeyTrigger)binding.Trigger).Chord);
        Assert.Equal(TranslateFeature.Show, binding.Command);
        Assert.Contains(manifest.Commands, c => c.Id == TranslateContracts.TranslateText.Id);
    }

    [Fact]
    public async Task 激活_注册全部命令_停用后全部注销()
    {
        var ctx = new FakeFeatureContext("translate");
        var feature = new TranslateFeature(ctx);

        await feature.ActivateAsync(default);
        Assert.Equal(TranslateFeature.Manifest.Commands.Select(c => c.Id.Value).Order(), ctx.Bus.RegisteredCommands.Select(c => c.Value).Order());

        await feature.DeactivateAsync();
        Assert.Empty(ctx.Bus.RegisteredCommands);
    }

    [Fact]
    public void 设置文件里没有的字段_用默认值_服务列表为默认三个免费源加关闭的DeepSeek()
    {
        var ctx = new FakeFeatureContext("translate");

        var settings = new TranslateFeature(ctx).LoadSettings();

        Assert.Equal(["google", "bing", "transmart", "deepseek"], settings.Services.Select(s => s.Id));
        Assert.Equal(HotkeyBehavior.Smart, settings.HotkeyBehavior);
    }
}
