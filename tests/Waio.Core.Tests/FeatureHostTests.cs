using Microsoft.Extensions.Logging.Abstractions;
using Waio.Core.Bindings;
using Waio.Core.Commands;
using Waio.Core.Host;
using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Input;
using Waio.Testing;

namespace Waio.Core.Tests;

public class FeatureHostTests : IDisposable
{
    private readonly TempRoot _root = new();
    private readonly CommandBus _bus = new();
    private readonly FakeInputService _input = new();

    public void Dispose() => _root.Dispose();

    /// <summary>可编排行为的测试功能。</summary>
    private sealed class ScriptedFeature(
        IFeatureContext context,
        Func<IFeatureContext, CancellationToken, ValueTask>? onActivate = null,
        Func<IFeatureContext, ValueTask>? onDeactivate = null) : IFeature
    {
        public ValueTask ActivateAsync(CancellationToken cancellationToken) =>
            onActivate?.Invoke(context, cancellationToken) ?? ValueTask.CompletedTask;

        public ValueTask DeactivateAsync() => onDeactivate?.Invoke(context) ?? ValueTask.CompletedTask;
    }

    private sealed class FakeProbe(bool elevated) : ICapabilityProbe
    {
        public CapabilityCheck Check(Capability required) =>
            required.HasFlag(Capability.Elevated) && !elevated
                ? new CapabilityCheck(false, "需要管理员权限")
                : CapabilityCheck.Ok;
    }

    private static FeatureDescriptor Descriptor(
        string id,
        Func<IFeatureContext, IFeature> create,
        Capability capabilities = Capability.None,
        params string[] commands) =>
        new(
            new FeatureManifest(
                id, id, "测试", "", capabilities,
                [.. commands.Select(c => new CommandDescriptor(c, c))],
                []),
            create);

    private FeatureHost Host(
        IReadOnlyList<FeatureDescriptor> catalog,
        bool elevated = true,
        TimeSpan? timeout = null)
    {
        var services = new HostServices(
            _bus, _bus, _input,
            _ => new InMemoryFeatureSettings(),
            _ => new InMemorySecretStore(),
            _root.Paths,
            new RecordingOverlay(),
            new FakeSystemEvents(),
            new ShortcutDirectory(),
            new RecordingLauncher(), new FakeClipboard(), new ImmediateUiThread());
        return new FeatureHost(catalog, services, new FakeProbe(elevated), NullLoggerFactory.Instance, timeout);
    }

    [Fact]
    public async Task 正常激活_状态变Running_命令可用()
    {
        var host = Host([Descriptor("alpha", ctx => new ScriptedFeature(ctx, (c, _) =>
        {
            c.Commands.Register("alpha.hello", (_, _) => ValueTask.CompletedTask);
            return ValueTask.CompletedTask;
        }), commands: "alpha.hello")]);

        await host.StartAsync(["alpha"]);

        Assert.Equal(FeatureState.Running, host.GetStatus("alpha").State);
        await _bus.PerformAsync("alpha.hello", CommandArguments.Empty); // 不抛即可用
    }

    [Fact]
    public async Task 一个功能激活抛异常_被隔离为Failed_其他功能照常运行()
    {
        var host = Host(
        [
            Descriptor("bad", ctx => new ScriptedFeature(ctx, (_, _) => throw new InvalidOperationException("炸了"))),
            Descriptor("good", ctx => new ScriptedFeature(ctx)),
        ]);

        await host.StartAsync(["bad", "good"]);

        var bad = host.GetStatus("bad");
        Assert.Equal(FeatureState.Failed, bad.State);
        Assert.Equal("炸了", bad.Message);
        Assert.IsType<InvalidOperationException>(bad.Error);
        Assert.Equal(FeatureState.Running, host.GetStatus("good").State);
    }

    [Fact]
    public async Task 工厂本身抛异常_同样被隔离()
    {
        var host = Host([Descriptor("bad", _ => throw new InvalidOperationException("构造失败"))]);

        await host.StartAsync(["bad"]);

        Assert.Equal(FeatureState.Failed, host.GetStatus("bad").State);
    }

    [Fact]
    public async Task 激活失败时_已注册的命令被宿主回收()
    {
        var host = Host([Descriptor("bad", ctx => new ScriptedFeature(ctx, (c, _) =>
        {
            c.Commands.Register("bad.one", (_, _) => ValueTask.CompletedTask);
            throw new InvalidOperationException("注册完才失败");
        }), commands: "bad.one")]);

        await host.StartAsync(["bad"]);

        Assert.False(_bus.IsRegistered("bad.one"));
    }

    [Fact]
    public async Task 激活超时_标记Failed_并向功能传递取消()
    {
        var cancelled = false;
        var host = Host(
            [Descriptor("slow", ctx => new ScriptedFeature(ctx, async (_, ct) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    throw;
                }
            }))],
            timeout: TimeSpan.FromMilliseconds(100));

        await host.StartAsync(["slow"]);

        var status = host.GetStatus("slow");
        Assert.Equal(FeatureState.Failed, status.State);
        Assert.Contains("超过", status.Message);
        await Task.Delay(100);
        Assert.True(cancelled);
    }

    [Fact]
    public async Task 需要管理员但当前不是_NeedsAttention_且不创建功能()
    {
        var created = false;
        var host = Host(
            [Descriptor("index", ctx => { created = true; return new ScriptedFeature(ctx); }, Capability.Elevated)],
            elevated: false);

        await host.StartAsync(["index"]);

        var status = host.GetStatus("index");
        Assert.Equal(FeatureState.NeedsAttention, status.State);
        Assert.Contains("管理员", status.Message);
        Assert.False(created);
    }

    [Fact]
    public async Task 条件满足后重新激活_可以从NeedsAttention恢复()
    {
        var catalog = new[] { Descriptor("index", ctx => new ScriptedFeature(ctx), Capability.Elevated) };
        var probe = new MutableProbe();
        var host = new FeatureHost(catalog, ServicesFor(), probe, NullLoggerFactory.Instance);
        await host.ActivateAsync("index");
        Assert.Equal(FeatureState.NeedsAttention, host.GetStatus("index").State);

        probe.Elevated = true;
        await host.ActivateAsync("index");

        Assert.Equal(FeatureState.Running, host.GetStatus("index").State);
    }

    [Fact]
    public async Task 停用_注销命令并释放输入过滤器_即使功能自己没释放()
    {
        var filter = new PassFilter();
        var host = Host([Descriptor("keys", ctx => new ScriptedFeature(ctx, (c, _) =>
        {
            c.Commands.Register("keys.go", (_, _) => ValueTask.CompletedTask);
            c.Input.RegisterKeyFilter(filter); // 故意不保存 token,也不在 Deactivate 里释放
            return ValueTask.CompletedTask;
        }), commands: "keys.go")]);
        await host.StartAsync(["keys"]);
        Assert.Single(_input.ActiveFilters);

        await host.DeactivateAsync("keys");

        Assert.Equal(FeatureState.Disabled, host.GetStatus("keys").State);
        Assert.False(_bus.IsRegistered("keys.go"));
        Assert.Empty(_input.ActiveFilters);
    }

    [Fact]
    public async Task 停用时释放键盘会话()
    {
        var host = Host([Descriptor("sess", ctx => new ScriptedFeature(ctx, (c, _) =>
        {
            c.Input.BeginKeyboardSession(new KeyboardSessionOptions());
            return ValueTask.CompletedTask;
        }))]);
        await host.StartAsync(["sess"]);
        Assert.Equal(1, _input.ActiveSessionCount);

        await host.DeactivateAsync("sess");

        Assert.Equal(0, _input.ActiveSessionCount);
    }

    [Fact]
    public async Task 功能的Deactivate抛异常_宿主仍然完成清理()
    {
        var host = Host([Descriptor("flaky", ctx => new ScriptedFeature(
            ctx,
            (c, _) =>
            {
                c.Commands.Register("flaky.run", (_, _) => ValueTask.CompletedTask);
                return ValueTask.CompletedTask;
            },
            _ => throw new InvalidOperationException("停用时炸了")), commands: "flaky.run")]);
        await host.StartAsync(["flaky"]);

        await host.DeactivateAsync("flaky");

        Assert.Equal(FeatureState.Disabled, host.GetStatus("flaky").State);
        Assert.False(_bus.IsRegistered("flaky.run"));
    }

    [Fact]
    public async Task 注册清单里没声明的命令_被拒绝_功能Failed()
    {
        var host = Host([Descriptor("sneaky", ctx => new ScriptedFeature(ctx, (c, _) =>
        {
            c.Commands.Register("sneaky.hidden", (_, _) => ValueTask.CompletedTask);
            return ValueTask.CompletedTask;
        }), commands: "sneaky.declared")]);

        await host.StartAsync(["sneaky"]);

        Assert.Equal(FeatureState.Failed, host.GetStatus("sneaky").State);
        Assert.False(_bus.IsRegistered("sneaky.hidden"));
    }

    [Fact]
    public async Task 失败后可以重新激活()
    {
        var attempts = 0;
        var host = Host([Descriptor("retry", ctx => new ScriptedFeature(ctx, (_, _) =>
            ++attempts == 1 ? throw new InvalidOperationException("第一次失败") : ValueTask.CompletedTask))]);

        await host.ActivateAsync("retry");
        Assert.Equal(FeatureState.Failed, host.GetStatus("retry").State);
        await host.ActivateAsync("retry");

        Assert.Equal(FeatureState.Running, host.GetStatus("retry").State);
    }

    [Fact]
    public async Task 重复激活_是幂等的()
    {
        var created = 0;
        var host = Host([Descriptor("once", ctx => { created++; return new ScriptedFeature(ctx); })]);

        await host.ActivateAsync("once");
        await host.ActivateAsync("once");

        Assert.Equal(1, created);
    }

    [Fact]
    public async Task StopAsync_停用全部_按激活的逆序()
    {
        var order = new List<string>();
        FeatureDescriptor Make(string id) => Descriptor(id, ctx => new ScriptedFeature(ctx, onDeactivate: _ =>
        {
            order.Add(id);
            return ValueTask.CompletedTask;
        }));
        var host = Host([Make("a"), Make("b"), Make("c")]);
        await host.StartAsync(["a", "b", "c"]);

        await host.StopAsync();

        Assert.Equal(["c", "b", "a"], order);
        Assert.All(host.Statuses, s => Assert.Equal(FeatureState.Disabled, s.State));
    }

    [Fact]
    public async Task 状态变化会通知订阅者()
    {
        var host = Host([Descriptor("n", ctx => new ScriptedFeature(ctx))]);
        var seen = new List<FeatureState>();
        host.StatusChanged += s => seen.Add(s.State);

        await host.ActivateAsync("n");
        await host.DeactivateAsync("n");

        Assert.Equal([FeatureState.Running, FeatureState.Disabled], seen);
    }

    [Fact]
    public async Task 订阅者抛异常_不影响宿主()
    {
        var host = Host([Descriptor("n", ctx => new ScriptedFeature(ctx))]);
        host.StatusChanged += _ => throw new InvalidOperationException("订阅者有 bug");

        await host.ActivateAsync("n");

        Assert.Equal(FeatureState.Running, host.GetStatus("n").State);
    }

    [Fact]
    public async Task 外部取消激活_回到Disabled并抛出取消()
    {
        var host = Host([Descriptor("c", ctx => new ScriptedFeature(ctx, async (_, ct) => await Task.Delay(Timeout.Infinite, ct)))]);
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ActivateAsync("c", cts.Token));

        Assert.Equal(FeatureState.Disabled, host.GetStatus("c").State);
    }

    [Fact]
    public void 目录校验_功能id重复()
    {
        var ex = Assert.Throws<ArgumentException>(() => Host([Descriptor("dup", ctx => new ScriptedFeature(ctx)), Descriptor("dup", ctx => new ScriptedFeature(ctx))]));

        Assert.Contains("重复", ex.Message);
    }

    [Fact]
    public void 目录校验_命令必须以功能id开头()
    {
        var ex = Assert.Throws<ArgumentException>(() => Host([Descriptor("alpha", ctx => new ScriptedFeature(ctx), commands: "beta.steal")]));

        Assert.Contains("不属于", ex.Message);
    }

    [Fact]
    public void 目录校验_出厂绑定必须引用已声明的命令()
    {
        var manifest = new FeatureManifest(
            "alpha", "alpha", "", "", Capability.None,
            [new CommandDescriptor("alpha.one", "one")],
            [new DefaultBinding(new HotkeyTrigger(KeyChord.Parse("f1")), "alpha.ghost")]);

        var ex = Assert.Throws<ArgumentException>(() => Host([new FeatureDescriptor(manifest, ctx => new ScriptedFeature(ctx))]));

        Assert.Contains("未声明", ex.Message);
    }

    [Fact]
    public void 查询不存在的功能_抛KeyNotFound()
    {
        var host = Host([]);

        Assert.Throws<KeyNotFoundException>(() => host.GetStatus("nope"));
    }

    private HostServices ServicesFor() => new(
        _bus, _bus, _input, _ => new InMemoryFeatureSettings(), _ => new InMemorySecretStore(), _root.Paths,
        new RecordingOverlay(), new FakeSystemEvents(), new ShortcutDirectory(), new RecordingLauncher(), new FakeClipboard(), new ImmediateUiThread());

    private sealed class MutableProbe : ICapabilityProbe
    {
        public bool Elevated { get; set; }

        public CapabilityCheck Check(Capability required) =>
            required.HasFlag(Capability.Elevated) && !Elevated ? new CapabilityCheck(false, "需要管理员权限") : CapabilityCheck.Ok;
    }

    private sealed class PassFilter : IRealtimeKeyFilter
    {
        public FilterDecision OnKey(in KeyEvent keyEvent, IKeyEmitter emitter) => FilterDecision.Pass;
    }
}

