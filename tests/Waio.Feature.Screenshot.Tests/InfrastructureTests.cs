using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Input;
using Waio.Testing;

namespace Waio.Feature.Screenshot.Tests;

/// <summary>像素、编码、历史存储、保存文件(MAIO ScreenshotInfrastructureTests)。</summary>
public sealed class InfrastructureTests : IDisposable
{
    private static readonly SampledColor Base = new(26, 43, 60);
    private static readonly SampledColor White = new(255, 255, 255);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "waio-screenshot-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>4×4 的纯色图,左上角一个白点。</summary>
    private static PixelBuffer Sample()
    {
        var image = PixelBuffer.Solid(4, 4, Base);
        image.SetColor(0, 0, White);
        return image;
    }

    [Fact]
    public void 取色以左上角为原点_越界返回null()
    {
        var image = Sample();
        Assert.Equal(White, image.ColorAt(0, 0));
        Assert.Equal(Base, image.ColorAt(2, 3));
        Assert.Null(image.ColorAt(4, 0));
    }

    [Fact]
    public void 裁剪保留对应位置的像素()
    {
        var crop = Sample().Crop(new PxRect(0, 0, 2, 3));
        Assert.Equal((2, 3), (crop.Width, crop.Height));
        Assert.Equal(White, crop.ColorAt(0, 0));
        Assert.Equal(Base, crop.ColorAt(1, 2));
    }

    [Fact]
    public async Task PNG编码再解码_像素不变_缩略图按最长边缩小()
    {
        var image = Sample();
        var decoded = await ImageCodec.DecodeAsync(await ImageCodec.EncodePngAsync(image, scale: 1.5));
        Assert.Equal(image.Pixels, decoded.Pixels);

        var wide = PixelBuffer.Solid(960, 240, Base);
        var thumb = await ImageCodec.DecodeAsync(await ImageCodec.EncodePngAsync(wide, maxSide: 480));
        Assert.Equal((480, 120), (thumb.Width, thumb.Height));
    }

    [Fact]
    public void 剪贴板DIB_自下而上_带DPI()
    {
        var dib = ImageCodec.ToDib(Sample(), scale: 1.5);

        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(dib));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8))); // 高度为正 = 自下而上
        Assert.Equal(32, BinaryPrimitives.ReadInt16LittleEndian(dib.AsSpan(14)));
        Assert.Equal(5669, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(24))); // 144 DPI
        var lastRow = 40 + 3 * 16; // 原图第 0 行在最后
        Assert.Equal([255, 255, 255, 255], dib.AsSpan(lastRow, 4).ToArray());
    }

    [Fact]
    public async Task 历史写入文件和索引_重新打开后还在()
    {
        var store = new HistoryStore(_dir, NullLogger.Instance);
        var record = await store.AddAsync(new HistoryStore.Entry([1, 2, 3], [4], 4, 4, 2), "notepad", DateTimeOffset.Now);

        Assert.True(File.Exists(Path.Combine(_dir, record.Id, "original.png")));
        Assert.True(File.Exists(Path.Combine(_dir, record.Id, "thumb.png")));
        Assert.Equal(4, record.Bytes);
        Assert.EndsWith("original.png", store.FinalImagePath(record));

        var reopened = await new HistoryStore(_dir, NullLogger.Instance).RecordsAsync();
        var loaded = Assert.Single(reopened);
        Assert.Equal(record with { CreatedAt = loaded.CreatedAt }, loaded);
        Assert.Equal("notepad", loaded.SourceApp);
    }

    [Fact]
    public async Task 索引损坏_改名保留并从目录重建()
    {
        var store = new HistoryStore(_dir, NullLogger.Instance);
        var record = await store.AddAsync(new HistoryStore.Entry([1, 2, 3], [4], 4, 4, 1), null, DateTimeOffset.Now);
        await File.WriteAllTextAsync(Path.Combine(_dir, "index.json"), "{ 坏了");

        var rebuilt = await new HistoryStore(_dir, NullLogger.Instance).RecordsAsync();

        Assert.Equal(record.Id, Assert.Single(rebuilt).Id);
        Assert.Single(Directory.GetFiles(_dir, "index.corrupt-*.json"));
    }

    [Fact]
    public async Task 超过上限时删掉最旧的记录和目录()
    {
        var store = new HistoryStore(_dir, NullLogger.Instance);
        var old = await store.AddAsync(new HistoryStore.Entry(new byte[100], [0], 1, 1, 1), null, DateTimeOffset.Now.AddDays(-1));
        await store.AddAsync(new HistoryStore.Entry(new byte[100], [0], 1, 1, 1), null, DateTimeOffset.Now);

        Assert.Equal(1, await store.EnforceAsync(150));
        Assert.False(Directory.Exists(Path.Combine(_dir, old.Id)));
        Assert.Equal((1, 101L, 0), await store.StatsAsync());
    }

    [Fact]
    public void 保存文件_按时间生成名字_重名加序号()
    {
        var date = new DateTimeOffset(2026, 11, 3, 14, 5, 33, TimeSpan.FromHours(8));
        var first = ImageExporter.Save([1], _dir, date);
        var second = ImageExporter.Save([2], _dir, date);

        Assert.Equal(date.ToLocalTime().ToString("'WAIO 'yyyy-MM-dd HH.mm.ss'.png'"), Path.GetFileName(first));
        Assert.EndsWith(" 2.png", second);
    }
}

/// <summary>功能接线(MAIO ScreenshotTests)。</summary>
public class ScreenshotFeatureTests
{
    [Fact]
    public void 默认快捷键WinC截图_AltF2贴剪贴板_AltF3录屏_CtrlAltF2全屏录屏_声明屏幕捕获_命令前缀一致()
    {
        var manifest = ScreenshotFeature.Manifest;

        Assert.Equal("screenshot", manifest.Id.Value);
        Assert.True(manifest.RequiredCapabilities.HasFlag(Capability.ScreenCapture));
        Assert.All(manifest.Commands, c => Assert.Equal("screenshot", c.Id.FeaturePart));
        Assert.Collection(
            manifest.DefaultBindings,
            b => Assert.Equal((KeyChord.Parse("win+c"), ScreenshotFeature.Capture), (((HotkeyTrigger)b.Trigger).Chord, b.Command)),
            b => Assert.Equal((KeyChord.Parse("alt+f2"), ScreenshotFeature.PinClipboard), (((HotkeyTrigger)b.Trigger).Chord, b.Command)),
            b => Assert.Equal((KeyChord.Parse("alt+f3"), ScreenshotFeature.Record), (((HotkeyTrigger)b.Trigger).Chord, b.Command)),
            b => Assert.Equal((KeyChord.Parse("ctrl+alt+f2"), ScreenshotFeature.RecordFullScreen), (((HotkeyTrigger)b.Trigger).Chord, b.Command)));
    }

    [Fact]
    public async Task 激活_注册声明的全部命令_停用后全部注销()
    {
        var ctx = new FakeFeatureContext("screenshot", Path.Combine(Path.GetTempPath(), "waio-screenshot-feature", Guid.NewGuid().ToString("N")));
        var feature = new ScreenshotFeature(ctx, prewarm: false);

        await feature.ActivateAsync(default);
        Assert.Equal(ScreenshotFeature.Manifest.Commands.Select(c => c.Id.Value).Order(), ctx.Bus.RegisteredCommands.Select(c => c.Value).Order());

        await feature.DeactivateAsync();
        Assert.Empty(ctx.Bus.RegisteredCommands);
    }

    [Fact]
    public async Task 剪贴板CopyAsync_像用户复制一样触发变化事件()
    {
        var clipboard = new FakeClipboard();
        var changes = new List<string?>();
        ((Waio.Kit.Services.IClipboardService)clipboard).Changed += c => changes.Add(c.SourceApp);

        Assert.True(await clipboard.CopyAsync(Waio.Kit.Services.ClipboardContent.FromText("#1A2B3C")));
        Assert.True(await clipboard.SetAsync(Waio.Kit.Services.ClipboardContent.FromText("不记录")));

        Assert.Equal(["Waio"], changes);
    }
}
