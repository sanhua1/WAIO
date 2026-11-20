using System.Text.Json;
using Waio.Core.Commands;
using Waio.Kit.Commands;

namespace Waio.Core.Tests;

public class CommandBusTests
{
    private sealed record EchoInput(string Text, int Times = 1);

    private static readonly CommandContract<EchoInput, string> Echo = new("test.echo");

    private static CommandArguments Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var dict = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        return new CommandArguments(dict);
    }

    [Fact]
    public async Task 普通命令_收到绑定里的参数()
    {
        var bus = new CommandBus();
        string? received = null;
        bus.Register("test.open", (args, _) =>
        {
            received = args.GetString("app");
            return ValueTask.CompletedTask;
        });

        await bus.PerformAsync("test.open", Args("""{"app":"notepad"}"""));

        Assert.Equal("notepad", received);
    }

    [Fact]
    public async Task 契约命令_进程内强类型调用()
    {
        var bus = new CommandBus();
        bus.Register(Echo, (input, _) => ValueTask.FromResult(string.Concat(Enumerable.Repeat(input.Text, input.Times))));

        var result = await bus.PerformAsync(Echo, new EchoInput("ab", 3));

        Assert.Equal("ababab", result);
    }

    [Fact]
    public async Task 契约命令_可以被绑定的JSON参数触发()
    {
        var bus = new CommandBus();
        EchoInput? received = null;
        bus.Register(Echo, (input, _) =>
        {
            received = input;
            return ValueTask.FromResult("ok");
        });

        await bus.PerformAsync("test.echo", Args("""{"text":"hi","times":2}"""));

        Assert.Equal(new EchoInput("hi", 2), received);
    }

    [Fact]
    public async Task 未注册的命令_抛Unavailable()
    {
        var bus = new CommandBus();

        var ex = await Assert.ThrowsAsync<CommandException>(
            async () => await bus.PerformAsync("test.missing", CommandArguments.Empty));

        Assert.Equal(CommandErrorKind.Unavailable, ex.Kind);
    }

    [Fact]
    public async Task 释放注册token后_命令变为不可用()
    {
        var bus = new CommandBus();
        var token = bus.Register("test.open", (_, _) => ValueTask.CompletedTask);
        Assert.True(bus.IsRegistered("test.open"));

        token.Dispose();

        Assert.False(bus.IsRegistered("test.open"));
        var ex = await Assert.ThrowsAsync<CommandException>(
            async () => await bus.PerformAsync("test.open", CommandArguments.Empty));
        Assert.Equal(CommandErrorKind.Unavailable, ex.Kind);
    }

    [Fact]
    public void 重复注册同一命令_抛异常()
    {
        var bus = new CommandBus();
        bus.Register("test.open", (_, _) => ValueTask.CompletedTask);

        Assert.Throws<InvalidOperationException>(
            () => bus.Register("test.open", (_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task handler抛业务异常_被包装成Failed并保留内层异常()
    {
        var bus = new CommandBus();
        bus.Register("test.boom", (_, _) => throw new InvalidOperationException("炸了"));

        var ex = await Assert.ThrowsAsync<CommandException>(
            async () => await bus.PerformAsync("test.boom", CommandArguments.Empty));

        Assert.Equal(CommandErrorKind.Failed, ex.Kind);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task 取消不被包装()
    {
        var bus = new CommandBus();
        bus.Register("test.slow", async (_, ct) => await Task.Delay(Timeout.Infinite, ct));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await bus.PerformAsync("test.slow", CommandArguments.Empty, cts.Token));
    }

    [Fact]
    public async Task 契约输入类型与注册不一致_抛InvalidArguments()
    {
        var bus = new CommandBus();
        bus.Register(Echo, (input, _) => ValueTask.FromResult(input.Text));
        var wrong = new CommandContract<int, string>("test.echo");

        var ex = await Assert.ThrowsAsync<CommandException>(
            async () => await bus.PerformAsync(wrong, 42));

        Assert.Equal(CommandErrorKind.InvalidArguments, ex.Kind);
    }

    [Fact]
    public async Task 参数无法转换成契约输入_抛InvalidArguments()
    {
        var bus = new CommandBus();
        bus.Register(Echo, (input, _) => ValueTask.FromResult(input.Text));

        var ex = await Assert.ThrowsAsync<CommandException>(
            async () => await bus.PerformAsync("test.echo", Args("""{"text":123}""")));

        Assert.Equal(CommandErrorKind.InvalidArguments, ex.Kind);
    }
}
