using Waio.Feature.SystemActions;
using Waio.Kit.Commands;
using Waio.Testing;

namespace Waio.Feature.SystemActions.Tests;

public class SystemActionsFeatureTests
{
    private readonly FakeFeatureContext _ctx = new("system");
    private readonly SystemActionsFeature _feature;

    public SystemActionsFeatureTests()
    {
        _feature = new SystemActionsFeature(_ctx);
    }

    private async Task ActivateAsync() => await _feature.ActivateAsync(default);

    [Fact]
    public void 清单合法_命令前缀与功能id一致()
    {
        var manifest = SystemActionsFeature.Manifest;

        Assert.Equal("system", manifest.Id.Value);
        Assert.All(manifest.Commands, c => Assert.Equal(manifest.Id.Value, c.Id.FeaturePart));
        Assert.Equal(["system.openApp", "system.openUrl"], manifest.Commands.Select(c => c.Id.Value));
    }

    [Fact]
    public async Task openApp_把参数原样交给启动器()
    {
        await ActivateAsync();

        await _ctx.Bus.PerformAsync(
            SystemActionsFeature.OpenApp,
            TestArgs.Json("""{"target":"notepad","arguments":"a.txt","workingDirectory":"C:\\Temp"}"""));

        var request = Assert.Single(_ctx.FakeLauncher.Requests);
        Assert.Equal("notepad", request.Target);
        Assert.Equal("a.txt", request.Arguments);
        Assert.Equal(@"C:\Temp", request.WorkingDirectory);
    }

    [Fact]
    public async Task openApp_默认不提权()
    {
        await ActivateAsync();

        await _ctx.Bus.PerformAsync(SystemActionsFeature.OpenApp, TestArgs.Json("""{"target":"notepad"}"""));

        Assert.False(Assert.Single(_ctx.FakeLauncher.Requests).RunElevated);
    }

    [Theory]
    [InlineData("""{"target":"x","runElevated":"true"}""")] // 字符串不算
    [InlineData("""{"target":"x","runElevated":1}""")] // 数字不算
    [InlineData("""{"target":"x","runElevated":null}""")]
    [InlineData("""{"target":"x","runElevated":false}""")]
    public async Task runElevated_只有明确的true才提权(string json)
    {
        await ActivateAsync();

        await _ctx.Bus.PerformAsync(SystemActionsFeature.OpenApp, TestArgs.Json(json));

        Assert.False(Assert.Single(_ctx.FakeLauncher.Requests).RunElevated);
    }

    [Fact]
    public async Task runElevated_明确为true才提权()
    {
        await ActivateAsync();

        await _ctx.Bus.PerformAsync(SystemActionsFeature.OpenApp, TestArgs.Json("""{"target":"x","runElevated":true}"""));

        Assert.True(Assert.Single(_ctx.FakeLauncher.Requests).RunElevated);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"target":""}""")]
    [InlineData("""{"target":"   "}""")]
    [InlineData("""{"target":42}""")]
    public async Task openApp_缺少target_抛InvalidArguments_且不启动(string json)
    {
        await ActivateAsync();

        var ex = await Assert.ThrowsAsync<CommandException>(
            async () => await _ctx.Bus.PerformAsync(SystemActionsFeature.OpenApp, TestArgs.Json(json)));

        Assert.Equal(CommandErrorKind.InvalidArguments, ex.Kind);
        Assert.Empty(_ctx.FakeLauncher.Requests);
    }

    [Fact]
    public async Task openUrl_交给启动器_且不提权()
    {
        await ActivateAsync();

        await _ctx.Bus.PerformAsync(SystemActionsFeature.OpenUrl, TestArgs.Json("""{"url":"https://example.com"}"""));

        var request = Assert.Single(_ctx.FakeLauncher.Requests);
        Assert.Equal("https://example.com", request.Target);
        Assert.False(request.RunElevated);
    }

    [Fact]
    public async Task openUrl_缺少url_抛InvalidArguments()
    {
        await ActivateAsync();

        var ex = await Assert.ThrowsAsync<CommandException>(
            async () => await _ctx.Bus.PerformAsync(SystemActionsFeature.OpenUrl, TestArgs.Json("{}")));

        Assert.Equal(CommandErrorKind.InvalidArguments, ex.Kind);
    }

    [Fact]
    public async Task 启动失败_HUD提示用户_并抛Failed()
    {
        _ctx.FakeLauncher.FailWith = "找不到:C:\\nope.exe";
        await ActivateAsync();

        var ex = await Assert.ThrowsAsync<CommandException>(
            async () => await _ctx.Bus.PerformAsync(SystemActionsFeature.OpenApp, TestArgs.Json("""{"target":"C:\\nope.exe"}""")));

        Assert.Equal(CommandErrorKind.Failed, ex.Kind);
        Assert.Equal("找不到:C:\\nope.exe", Assert.Single(_ctx.FakeOverlay.HudMessages));
    }

    [Fact]
    public async Task 停用后命令不可用()
    {
        await ActivateAsync();
        await _feature.DeactivateAsync();

        var ex = await Assert.ThrowsAsync<CommandException>(
            async () => await _ctx.Bus.PerformAsync(SystemActionsFeature.OpenApp, TestArgs.Json("""{"target":"x"}""")));

        Assert.Equal(CommandErrorKind.Unavailable, ex.Kind);
    }
}
