using Waio.Core.Storage;

namespace Waio.Core.Tests;

/// <summary>每个测试一个独立的临时根目录,测试结束删除。绝不碰真实用户数据。</summary>
internal sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "waio-core-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Paths = AppPaths.ForRoot(Path);
    }

    public string Path { get; }

    public AppPaths Paths { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结果
        }
    }
}
