using Waio.Core.Platform;

namespace Waio.Core.Tests;

public class SingleInstanceGuardTests
{
    // 每个测试用独立名字,避免互相干扰,也不碰真实的 "WAIO.Main"
    private static string UniqueName() => $"WAIO.Test.{Guid.NewGuid():N}";

    [Fact]
    public void 第一个实例拿到守卫_第二个拿不到()
    {
        var name = UniqueName();

        using var first = SingleInstanceGuard.TryAcquire(name);
        using var second = SingleInstanceGuard.TryAcquire(name);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void 第一个实例释放后_可以重新获取()
    {
        var name = UniqueName();
        SingleInstanceGuard.TryAcquire(name)!.Dispose();

        using var again = SingleInstanceGuard.TryAcquire(name);

        Assert.NotNull(again);
    }

    [Fact]
    public void 通知已有实例_触发ActivationRequested()
    {
        var name = UniqueName();
        using var first = SingleInstanceGuard.TryAcquire(name)!;
        using var received = new ManualResetEventSlim();
        first.ActivationRequested += received.Set;

        var delivered = SingleInstanceGuard.SignalExisting(name);

        Assert.True(delivered);
        Assert.True(received.Wait(TimeSpan.FromSeconds(5)), "5 秒内没有收到激活请求");
    }

    [Fact]
    public void 没有实例在运行_通知返回false()
    {
        Assert.False(SingleInstanceGuard.SignalExisting(UniqueName()));
    }

    [Fact]
    public void 可以在其他线程释放()
    {
        var name = UniqueName();
        var guard = SingleInstanceGuard.TryAcquire(name)!;

        var ex = Record.Exception(() => Task.Run(guard.Dispose).GetAwaiter().GetResult());

        Assert.Null(ex);
    }

    [Fact]
    public void 释放后不再响应激活请求()
    {
        var name = UniqueName();
        var guard = SingleInstanceGuard.TryAcquire(name)!;
        var count = 0;
        guard.ActivationRequested += () => Interlocked.Increment(ref count);
        guard.Dispose();

        SingleInstanceGuard.SignalExisting(name);
        Thread.Sleep(100);

        Assert.Equal(0, count);
    }
}
