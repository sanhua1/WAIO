using Microsoft.Extensions.Logging.Abstractions;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Annotation;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Feature.Screenshot.Infrastructure.Rendering;
using Waio.Kit.Services;

namespace Waio.Feature.Screenshot.Tests;

/// <summary>贴图用到的基础设施:旋转、剪贴板图片、文字渲染、历史更新、重新编辑的输出。</summary>
public sealed class PinInfrastructureTests : IDisposable
{
    private static readonly SampledColor Red = new(255, 0, 0);
    private static readonly SampledColor Blue = new(0, 0, 255);
    private static readonly SampledColor White = new(255, 255, 255);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "waio-screenshot-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>3×2:左上角红,右下角蓝,其余白。</summary>
    private static PixelBuffer Sample()
    {
        var image = PixelBuffer.Solid(3, 2, White);
        image.SetColor(0, 0, Red);
        image.SetColor(2, 1, Blue);
        return image;
    }

    [Fact]
    public void 顺时针旋转90度_左上角转到右上角()
    {
        var once = Sample().Rotated(1);
        Assert.Equal((2, 3), (once.Width, once.Height));
        Assert.Equal(Red, once.ColorAt(1, 0));
        Assert.Equal(Blue, once.ColorAt(0, 2));

        var half = Sample().Rotated(2);
        Assert.Equal(Red, half.ColorAt(2, 1));
        Assert.Equal(Blue, half.ColorAt(0, 0));

        var back = Sample().Rotated(-1);
        Assert.Equal(Red, back.ColorAt(0, 2));
        Assert.Equal(Sample().Pixels, Sample().Rotated(4).Pixels);
    }

    [Fact]
    public void 透明像素叠到白底上()
    {
        byte[] pixels = [0, 0, 0, 0, 0, 0, 128, 128, 10, 20, 30, 255];
        PixelBuffer.FlattenOnWhite(pixels);
        Assert.Equal([255, 255, 255, 255, 127, 127, 255, 255, 10, 20, 30, 255], pixels);
    }

    [Fact]
    public async Task 剪贴板里的DIB图片_原样贴出来()
    {
        var dib = ImageCodec.ToDib(Sample(), 1.5);
        var content = await ClipboardImage.ReadAsync(new ClipboardContent { Image = dib }, 1);
        Assert.NotNull(content);
        Assert.False(content.IsText);
        Assert.Equal(Sample().Pixels, content.Image.Pixels);
    }

    [Fact]
    public async Task 剪贴板里是图片文件时读文件_是文字时渲染成白底图片()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "a.png");
        await File.WriteAllBytesAsync(path, await ImageCodec.EncodePngAsync(Sample()));
        var fromFile = await ClipboardImage.ReadAsync(ClipboardContent.FromFiles([Path.Combine(_dir, "x.txt"), path]), 1);
        Assert.Equal(Sample().Pixels, fromFile!.Image.Pixels);

        var text = await ClipboardImage.ReadAsync(ClipboardContent.FromText("贴图测试 Pin"), 2);
        Assert.True(text!.IsText);
        Assert.True(text.Image.Width > 48 && text.Image.Height > 48); // 内边距 12 pt × 2
        Assert.Equal(White, text.Image.ColorAt(0, 0));

        Assert.Null(await ClipboardImage.ReadAsync(ClipboardContent.FromText("   "), 1));
    }

    [Fact]
    public async Task 重新编辑后更新原记录_不新建_标注清空时删掉标注文件()
    {
        var store = new HistoryStore(_dir, NullLogger.Instance);
        var changes = 0;
        store.Changed += () => changes++;
        var record = await store.AddAsync(new HistoryStore.Entry([1, 2, 3], [4], 4, 4, 2), null, DateTimeOffset.Now);
        var annotation = new Annotation(new RectangleShape(new RectD(1, 2, 3, 4), false, false), AnnotationColors.Presets[0], 4);
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes<IReadOnlyList<Annotation>>([annotation]);

        await store.UpdateAsync(record.Id, [9, 9], json, [5]);
        var updated = Assert.Single(await store.RecordsAsync());
        Assert.True(updated.HasAnnotations);
        Assert.Equal(3 + 2 + json.Length + 1, updated.Bytes);
        Assert.Equal(annotation.Shape, Assert.Single(store.Annotations(record.Id)).Shape);
        Assert.Equal("rendered.png", Path.GetFileName(store.FinalImagePath(updated)));

        await store.UpdateAsync(record.Id, null, null, [6]);
        var cleared = (await store.RecordAsync(record.Id))!;
        Assert.False(cleared.HasAnnotations);
        Assert.Empty(store.Annotations(record.Id));
        Assert.False(File.Exists(Path.Combine(store.Folder(record.Id), "rendered.png")));
        Assert.Equal(3, changes);
    }

    [Fact]
    public async Task 收藏和取消收藏()
    {
        var store = new HistoryStore(_dir, NullLogger.Instance);
        var record = await store.AddAsync(new HistoryStore.Entry([1], [1], 1, 1, 1), null, DateTimeOffset.Now);
        await store.SetPinnedAsync(record.Id, true);
        Assert.True((await new HistoryStore(_dir, NullLogger.Instance).RecordAsync(record.Id))!.Pinned);
        await store.SetPinnedAsync(record.Id, false);
        Assert.False((await store.RecordAsync(record.Id))!.Pinned);
    }

    [Fact]
    public void 同一个目录共用一个历史实例()
    {
        Assert.Same(HistoryStore.For(_dir, NullLogger.Instance), HistoryStore.For(Path.Combine(_dir, "."), NullLogger.Instance));
    }

    [Fact]
    public void 重新编辑的输出按原图分辨率_标注按比例放大()
    {
        // 原图 400×200,屏幕上缩小一半显示在 (100, 50);屏幕上 (110, 60) 起 20×10 的实心红块 → 原图上 (20, 20) 起 40×20
        var image = PixelBuffer.Solid(400, 200, White);
        using var source = new EffectSource(image, new PxPoint(100, 50), 1, zoom: 2);
        var block = new Annotation(new RectangleShape(new RectD(110, 60, 20, 10), Filled: true, Rounded: false), AnnotationColors.Presets[0], 2);
        var output = AnnotationRenderer.RenderEditOutput([block], source);
        Assert.Equal((400, 200), (output.Width, output.Height));
        Assert.Equal(new SampledColor(237, 59, 54), output.ColorAt(30, 30)); // 预设红色
        Assert.Equal(White, output.ColorAt(70, 30));
        Assert.Equal(White, output.ColorAt(10, 10));
    }
}
