using Microsoft.Extensions.Logging.Abstractions;
using Waio.Core.Clipboard;
using Waio.Kit.Services;
using static Waio.Core.Clipboard.ClipboardNative;

namespace Waio.Core.Tests;

/// <summary>
/// 真实剪贴板读写。<b>会覆盖你当前的剪贴板内容</b>,所以需要 WAIO_INTEGRATION=1(不要在正在使用的电脑上随手运行)。
/// 用 ClipboardFormatsIO 直接写剪贴板来模拟"其他程序复制"。
/// </summary>
public sealed class ClipboardIntegrationTests : IDisposable
{
    private readonly ClipboardService _service = new(NullLogger.Instance);

    public ClipboardIntegrationTests()
    {
        if (Environment.GetEnvironmentVariable("WAIO_INTEGRATION") == "1")
        {
            _service.Start();
        }
    }

    public void Dispose() => _service.Dispose();

    /// <summary>模拟别的程序复制:在测试线程上打开剪贴板直接写。</summary>
    private static void CopyLikeAnotherApp(ClipboardContent content)
    {
        for (var i = 0; i < 50 && !OpenClipboard(0); i++)
        {
            Thread.Sleep(20);
        }

        try
        {
            Assert.True(ClipboardFormatsIO.Write(content));
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static ClipboardContent? ReadBack()
    {
        for (var i = 0; i < 50 && !OpenClipboard(0); i++)
        {
            Thread.Sleep(20);
        }

        try
        {
            return ClipboardFormatsIO.Read(out _);
        }
        finally
        {
            CloseClipboard();
        }
    }

    [IntegrationFact]
    public async Task 外部复制文字_触发Changed_内容一致()
    {
        var received = new TaskCompletionSource<ClipboardChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Changed += change => received.TrySetResult(change);

        CopyLikeAnotherApp(ClipboardContent.FromText("waio clipboard test 中文 ✓"));

        var change = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("waio clipboard test 中文 ✓", change.Content.Text);
    }

    [IntegrationFact]
    public async Task WAIO自己写入_不触发Changed()
    {
        var count = 0;
        _service.Changed += _ => Interlocked.Increment(ref count);

        Assert.True(await _service.SetAsync(ClipboardContent.FromText("written by waio")));
        await Task.Delay(500); // 给 WM_CLIPBOARDUPDATE 足够的时间到达

        Assert.Equal(0, count);
        Assert.Equal("written by waio", ReadBack()?.Text);
    }

    [IntegrationFact]
    public async Task 多种格式写入后读回一致()
    {
        var original = new ClipboardContent
        {
            Text = "rich",
            Rtf = "{\\rtf1 hi}"u8.ToArray(),
            Html = "Version:0.9\r\nStartHTML:0\r\n<b>hi</b>"u8.ToArray(),
        };

        Assert.True(await _service.SetAsync(original));

        var read = ReadBack()!;
        Assert.Equal("rich", read.Text);
        Assert.Equal(original.Rtf, read.Rtf);
        Assert.Equal(original.Html, read.Html);
    }

    [IntegrationFact]
    public async Task 文件列表写入后读回一致()
    {
        var files = new[] { @"C:\Windows\notepad.exe", @"C:\Windows\System32\cmd.exe" };

        Assert.True(await _service.SetAsync(ClipboardContent.FromFiles(files)));

        Assert.Equal(files, ReadBack()!.Files);
    }

    [IntegrationFact]
    public async Task 图片DIB写入后读回一致()
    {
        // 2x2、32 位、BITMAPINFOHEADER(40 字节)+ 16 字节像素
        var dib = new byte[40 + 16];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(2).CopyTo(dib, 4);
        BitConverter.GetBytes(2).CopyTo(dib, 8);
        BitConverter.GetBytes((ushort)1).CopyTo(dib, 12);
        BitConverter.GetBytes((ushort)32).CopyTo(dib, 14);
        for (var i = 40; i < dib.Length; i++)
        {
            dib[i] = (byte)(i * 7);
        }

        Assert.True(await _service.SetAsync(new ClipboardContent { Image = dib }));

        var image = ReadBack()!.Image!;
        Assert.Equal(dib, image[..dib.Length]); // 系统可能补齐尾部,前面部分必须一致
    }

    [IntegrationFact]
    public async Task 来源程序声明不要记录_默认不触发Changed_关闭遵守后触发()
    {
        var count = 0;
        _service.Changed += _ => Interlocked.Increment(ref count);
        var exclude = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");

        void CopyExcluded(string text)
        {
            for (var i = 0; i < 50 && !OpenClipboard(0); i++)
            {
                Thread.Sleep(20);
            }

            try
            {
                ClipboardFormatsIO.Write(ClipboardContent.FromText(text));
                var memory = GlobalAlloc(GmemMoveable | GmemZeroInit, 1);
                SetClipboardData(exclude, memory);
            }
            finally
            {
                CloseClipboard();
            }
        }

        _service.RespectExclusionFormats = true;
        CopyExcluded("secret 1");
        await Task.Delay(500);
        Assert.Equal(0, count);

        _service.RespectExclusionFormats = false;
        CopyExcluded("secret 2");
        await Task.Delay(500);
        Assert.Equal(1, count);
    }
}
