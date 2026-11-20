using Microsoft.Extensions.Logging.Abstractions;
using Waio.Core.Storage;

namespace Waio.Core.Tests;

/// <summary>功能密钥:DPAPI 加密落盘(ADR-0014)。只在本机当前用户的临时目录读写,不影响桌面。</summary>
public sealed class SecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "waio-secrets-" + Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_dir, "secrets.json");

    [Fact]
    public void 写入后能读回_文件里没有明文()
    {
        new SecretStore(File, NullLogger.Instance).Set("deepl.authKey", "sk-very-secret-123");

        Assert.Equal("sk-very-secret-123", new SecretStore(File, NullLogger.Instance).Get("deepl.authKey"));
        Assert.DoesNotContain("sk-very-secret", System.IO.File.ReadAllText(File));
    }

    [Fact]
    public void 空值等于删除_没有的名字返回null()
    {
        var store = new SecretStore(File, NullLogger.Instance);
        store.Set("a", "1");
        store.Set("a", "  ");

        Assert.Null(store.Get("a"));
        Assert.Null(store.Get("never"));
    }

    [Fact]
    public void 密文被改坏_当作没有设置_不抛异常()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{\"a\":\"bm90LWRwYXBp\"}");

        Assert.Null(new SecretStore(File, NullLogger.Instance).Get("a"));
    }

    [Fact]
    public void 文件损坏_当作空的()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "not json");

        Assert.Null(new SecretStore(File, NullLogger.Instance).Get("a"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
