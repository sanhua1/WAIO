using Waio.Feature.KeyMapping;
using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Input;
using Waio.Testing;

namespace Waio.Feature.KeyMapping.Tests;

public class KeyMappingFeatureTests
{
    private readonly FakeFeatureContext _ctx = new("keymap");
    private readonly KeyMappingFeature _feature;

    public KeyMappingFeatureTests()
    {
        _feature = new KeyMappingFeature(_ctx);
    }

    private static KeyEvent Down(string key) => new(KeyEventKind.Down, key, KeyModifiers.None, false);

    private Task Configure(params KeyRuleSettings[] rules) =>
        _ctx.FakeSettings.SaveAsync(new KeyMappingSettings { Rules = [.. rules] }).AsTask();

    [Fact]
    public void 清单合法_命令前缀与功能id一致()
    {
        var manifest = KeyMappingFeature.Manifest;

        Assert.Equal("keymap", manifest.Id.Value);
        Assert.All(manifest.Commands, c => Assert.Equal("keymap", c.Id.FeaturePart));
        Assert.True(manifest.RequiredCapabilities.HasFlag(Capability.InputHooks));
    }

    [Fact]
    public async Task 激活_注册过滤器和命令()
    {
        await _feature.ActivateAsync(default);

        Assert.Single(_ctx.FakeInput.ActiveFilters);
        Assert.Contains(KeyMappingFeature.Toggle, _ctx.Bus.RegisteredCommands);
    }

    [Fact]
    public async Task 配置的规则生效_按键被改写()
    {
        await Configure(new KeyRuleSettings { From = "a", To = "b" });
        await _feature.ActivateAsync(default);

        var decision = _ctx.FakeInput.Feed(Down("a"));

        Assert.Equal(FilterDecision.Consume, decision);
        Assert.Equal([("b", true)], _ctx.FakeInput.Emitted);
    }

    [Fact]
    public async Task 保存新设置_立即生效_不用重启()
    {
        await _feature.ActivateAsync(default);
        Assert.Equal(FilterDecision.Pass, _ctx.FakeInput.Feed(Down("a")));

        await Configure(new KeyRuleSettings { From = "a", To = "b" });

        Assert.Equal(FilterDecision.Consume, _ctx.FakeInput.Feed(Down("a")));
    }

    [Fact]
    public async Task 配置里有坏规则_只丢弃坏的_好的照常生效()
    {
        await Configure(new KeyRuleSettings { From = "nonsense", To = "b" }, new KeyRuleSettings { From = "a", To = "b" });
        await _feature.ActivateAsync(default);

        Assert.Equal(FilterDecision.Consume, _ctx.FakeInput.Feed(Down("a")));
    }

    [Fact]
    public async Task toggle命令_暂停和恢复_并用HUD告知()
    {
        await Configure(new KeyRuleSettings { From = "a", To = "b" });
        await _feature.ActivateAsync(default);

        await _ctx.Bus.PerformAsync(KeyMappingFeature.Toggle, CommandArguments.Empty);
        Assert.Equal(FilterDecision.Pass, _ctx.FakeInput.Feed(Down("a")));

        await _ctx.Bus.PerformAsync(KeyMappingFeature.Toggle, CommandArguments.Empty);
        Assert.Equal(FilterDecision.Consume, _ctx.FakeInput.Feed(Down("a")));

        Assert.Equal(["按键映射:已暂停", "按键映射:已恢复"], _ctx.FakeOverlay.HudMessages);
    }

    [Fact]
    public async Task 暂停期间保存设置_仍保持暂停()
    {
        await _feature.ActivateAsync(default);
        await _ctx.Bus.PerformAsync(KeyMappingFeature.Toggle, CommandArguments.Empty);

        await Configure(new KeyRuleSettings { From = "a", To = "b" });

        Assert.Equal(FilterDecision.Pass, _ctx.FakeInput.Feed(Down("a")));
    }

    [Fact]
    public async Task 停用_注销过滤器和命令_不再响应设置变化()
    {
        await _feature.ActivateAsync(default);
        await _feature.DeactivateAsync();

        Assert.Empty(_ctx.FakeInput.ActiveFilters);
        Assert.DoesNotContain(KeyMappingFeature.Toggle, _ctx.Bus.RegisteredCommands);

        var ex = await Record.ExceptionAsync(() => Configure(new KeyRuleSettings { From = "a", To = "b" }));
        Assert.Null(ex);
        Assert.Empty(_ctx.FakeInput.ActiveFilters);
    }
}

