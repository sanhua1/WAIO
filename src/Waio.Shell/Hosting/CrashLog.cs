using Microsoft.Extensions.Logging;
using Waio.Core.Logging;
using Waio.Core.Storage;

namespace Waio.Shell.Hosting;

/// <summary>
/// 未处理异常的兜底记录。组合根还没建好(或已销毁)时也能写日志,所以自己临时创建 provider。
/// 本身绝不抛异常。
/// </summary>
public static class CrashLog
{
    public static void Write(string source, Exception exception)
    {
        try
        {
            using var provider = new FileLoggerProvider(AppPaths.ForCurrentUser().LogsDirectory);
            provider.CreateLogger(source).LogCritical(exception, "未处理的异常");
        }
        catch (Exception)
        {
            // 兜底日志失败无处可报
        }
    }
}
