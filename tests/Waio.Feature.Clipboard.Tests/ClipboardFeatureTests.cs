using Microsoft.Extensions.Logging.Abstractions;
using Waio.Feature.Clipboard;
using Waio.Feature.Clipboard.Infrastructure;
using Waio.Kit.Commands;
using Waio.Testing;

namespace Waio.Feature.Clipboard.Tests;

/// <summary>功能整体接线:命令注册、设置实时生效(设置页 ADR-0010 依赖这两点)。</summary>
public sealed class ClipboardFeatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "waio-clip-feature-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结果
        }
    }

    [Fact]
    public async Task 启用后注册显示和清空两个命令_停用后注销()
    {
        var ctx = new FakeFeatureContext("clipboard", _dir);
        var feature = new ClipboardFeature(ctx);

        await feature.ActivateAsync(CancellationToken.None);
        Assert.Contains(ClipboardFeature.Show, ctx.Bus.RegisteredCommands);
        Assert.Contains(ClipboardFeature.ClearHistory, ctx.Bus.RegisteredCommands);

        await feature.DeactivateAsync();
        Assert.Empty(ctx.Bus.RegisteredCommands);
    }

    [Fact]
    public async Task 清空历史命令_删除未固定的_保留固定的()
    {
        var ctx = new FakeFeatureContext("clipboard", _dir);
        var feature = new ClipboardFeature(ctx);
        await feature.ActivateAsync(CancellationToken.None);
        ctx.FakeClipboard.Copy("keep me");
        ctx.FakeClipboard.Copy("drop me");
        await feature.DeactivateAsync(); // 停用会先处理完队列里的复制

        using (var store = SqliteClipStore.Open(_dir, NullLogger.Instance))
        {
            store.SetPinned(store.List(10).Single(i => i.Summary == "keep me").Id, true);
        }

        feature = new ClipboardFeature(ctx);
        await feature.ActivateAsync(CancellationToken.None);
        await ctx.Bus.PerformAsync(ClipboardFeature.ClearHistory, CommandArguments.Empty);
        await feature.DeactivateAsync();

        using var after = SqliteClipStore.Open(_dir, NullLogger.Instance);
        Assert.Equal(["keep me"], after.List(10).Select(i => i.Summary));
        Assert.Contains("清空", Assert.Single(ctx.FakeOverlay.HudMessages));
    }

    [Fact]
    public async Task 设置页保存后_运行中的功能立即生效()
    {
        var ctx = new FakeFeatureContext("clipboard", _dir);
        var feature = new ClipboardFeature(ctx);
        await feature.ActivateAsync(CancellationToken.None);
        Assert.False(ctx.FakeClipboard.RespectExclusionFormats); // 默认记录敏感内容

        await ctx.Settings.SaveAsync(new ClipboardSettings { RespectExclusionFormats = true });

        Assert.True(ctx.FakeClipboard.RespectExclusionFormats);
        await feature.DeactivateAsync();
    }
}
