using System.Text;
using Microsoft.Extensions.Logging;

namespace Waio.Core.Logging;

/// <summary>
/// 极简文件日志(ADR-0007):按天一个文件 <c>waio-yyyyMMdd.log</c>,启动时清理超期文件。
/// 日志写失败<b>永远不抛异常</b>(磁盘满、目录被删都不能拖垮程序)。
/// 不在键盘钩子回调里调用——那里禁止任何慢操作(ARCHITECTURE §5)。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minimum;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();

    public FileLoggerProvider(string directory, LogLevel minimum = LogLevel.Information, int retainDays = 7, Func<DateTime>? now = null)
    {
        _directory = directory;
        _minimum = minimum;
        _now = now ?? (() => DateTime.Now);
        try
        {
            Directory.CreateDirectory(directory);
            PurgeOld(retainDays);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 没有日志目录就没有日志,但程序照常运行
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private string FileFor(DateTime time) => Path.Combine(_directory, $"waio-{time:yyyyMMdd}.log");

    private void PurgeOld(int retainDays)
    {
        var cutoff = _now().Date.AddDays(-retainDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "waio-*.log"))
        {
            if (File.GetLastWriteTime(file) < cutoff)
            {
                File.Delete(file);
            }
        }
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var time = _now();
        var text = new StringBuilder()
            .Append(time.ToString("HH:mm:ss.fff")).Append(' ')
            .Append(Abbreviate(level)).Append(' ')
            .Append(category).Append(" | ")
            .Append(message);
        if (exception is not null)
        {
            text.AppendLine().Append(exception);
        }

        text.AppendLine();

        try
        {
            lock (_gate)
            {
                File.AppendAllText(FileFor(time), text.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 见类注释:日志本身出错不能传播
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner._minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
