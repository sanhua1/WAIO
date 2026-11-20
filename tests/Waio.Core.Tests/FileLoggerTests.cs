using Microsoft.Extensions.Logging;
using Waio.Core.Logging;

namespace Waio.Core.Tests;

public class FileLoggerTests : IDisposable
{
    private readonly TempRoot _root = new();

    public void Dispose() => _root.Dispose();

    private string LogDir => Path.Combine(_root.Path, "logs");

    [Fact]
    public void 写入一行_包含时间级别类别和消息()
    {
        using var provider = new FileLoggerProvider(LogDir, now: () => new DateTime(2026, 11, 9, 13, 5, 9, 123));
        var logger = provider.CreateLogger("feature.system");

        logger.LogWarning("找不到 {Path}", @"C:\x.exe");

        var text = File.ReadAllText(Path.Combine(LogDir, "waio-20261109.log"));
        Assert.Contains("13:05:09.123 WRN feature.system | 找不到 C:\\x.exe", text);
    }

    [Fact]
    public void 异常会连同堆栈一起写入()
    {
        using var provider = new FileLoggerProvider(LogDir);

        provider.CreateLogger("t").LogError(new InvalidOperationException("boom"), "失败");

        var text = File.ReadAllText(Directory.GetFiles(LogDir).Single());
        Assert.Contains("失败", text);
        Assert.Contains("InvalidOperationException: boom", text);
    }

    [Fact]
    public void 低于最低级别的日志不写入()
    {
        using var provider = new FileLoggerProvider(LogDir, LogLevel.Warning);
        var logger = provider.CreateLogger("t");

        logger.LogInformation("不该出现");
        logger.LogWarning("应该出现");

        var text = File.ReadAllText(Directory.GetFiles(LogDir).Single());
        Assert.DoesNotContain("不该出现", text);
        Assert.Contains("应该出现", text);
    }

    [Fact]
    public void 启动时清理超期的日志文件_保留近期的()
    {
        Directory.CreateDirectory(LogDir);
        var old = Path.Combine(LogDir, "waio-20260101.log");
        var recent = Path.Combine(LogDir, "waio-20261108.log");
        var unrelated = Path.Combine(LogDir, "notes.txt");
        foreach (var file in new[] { old, recent, unrelated })
        {
            File.WriteAllText(file, "x");
        }

        File.SetLastWriteTime(old, new DateTime(2026, 1, 1));
        File.SetLastWriteTime(unrelated, new DateTime(2026, 1, 1));
        File.SetLastWriteTime(recent, new DateTime(2026, 11, 8));

        using var provider = new FileLoggerProvider(LogDir, retainDays: 7, now: () => new DateTime(2026, 11, 9));

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(unrelated)); // 只清理 waio-*.log
    }

    [Fact]
    public void 日志目录被删后写日志_不抛异常()
    {
        using var provider = new FileLoggerProvider(LogDir);
        var logger = provider.CreateLogger("t");
        Directory.Delete(LogDir, recursive: true);

        var ex = Record.Exception(() => logger.LogError("目录没了"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task 并发写入_每条日志完整不交错()
    {
        using var provider = new FileLoggerProvider(LogDir);
        var logger = provider.CreateLogger("t");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                logger.LogInformation("线程{T}-第{I}条-结束", t, i);
            }
        })));

        var lines = File.ReadAllLines(Directory.GetFiles(LogDir).Single());
        Assert.Equal(400, lines.Length);
        Assert.All(lines, l => Assert.EndsWith("-结束", l));
    }
}
