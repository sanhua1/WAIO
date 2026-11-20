using Waio.Core.Input;
using Waio.Kit.Input;

namespace Waio.Core.Tests;

/// <summary>实时过滤器的新能力(ADR-0008):看到抬起和长按重复事件、通过发射器发出替代按键。</summary>
public class InputPipelineFilterTests
{
    private sealed class Sender : IKeySender
    {
        public List<(int Vk, bool Down)> Keys { get; } = [];

        public void Send(KeyChord chord)
        {
        }

        public void SendKey(int vk, bool down) => Keys.Add((vk, down));

        public void TapMaskKey()
        {
        }
    }

    private sealed class Probe : IKeyStateProbe
    {
        public HashSet<int> Down { get; } = [];

        public bool IsDown(int vk) => Down.Contains(vk);
    }

    private sealed class Recorder(Func<KeyEvent, FilterDecision>? decide = null) : IRealtimeKeyFilter
    {
        public List<KeyEvent> Seen { get; } = [];

        public FilterDecision OnKey(in KeyEvent keyEvent, IKeyEmitter emitter)
        {
            Seen.Add(keyEvent);
            if (keyEvent.Key == "capslock")
            {
                // 简单改键 capslock → esc
                if (keyEvent.Kind == KeyEventKind.Down)
                {
                    emitter.KeyDown("esc");
                }
                else
                {
                    emitter.KeyUp("esc");
                }

                return FilterDecision.Consume;
            }

            return decide?.Invoke(keyEvent) ?? FilterDecision.Pass;
        }
    }

    private readonly Sender _sender = new();
    private readonly Probe _probe = new();
    private readonly InputPipeline _pipeline;

    public InputPipelineFilterTests()
    {
        _pipeline = new InputPipeline(_sender, _probe, _ => { }, () => null, _ => { });
    }

    private bool Key(string name, bool down, bool ours = false)
    {
        KeyNames.TryGetVk(name, out var vk);
        var consumed = _pipeline.Process(new RawKey(vk, down, ours));
        if (down)
        {
            _probe.Down.Add(vk);
        }
        else
        {
            _probe.Down.Remove(vk);
        }

        return consumed;
    }

    [Fact]
    public void 过滤器能看到抬起事件()
    {
        var filter = new Recorder();
        _pipeline.AddFilter(filter);

        Key("f5", down: true);
        Key("f5", down: false);

        Assert.Equal([KeyEventKind.Down, KeyEventKind.Up], filter.Seen.Select(e => e.Kind));
    }

    [Fact]
    public void 过滤器能看到长按重复_并标记IsRepeat()
    {
        var filter = new Recorder();
        _pipeline.AddFilter(filter);

        Key("f5", down: true);
        Key("f5", down: true);
        Key("f5", down: true);

        Assert.Equal([false, true, true], filter.Seen.Select(e => e.IsRepeat));
    }

    [Fact]
    public void 改键_按下抬起重复都转成目标键_并吞掉原键()
    {
        _pipeline.AddFilter(new Recorder());
        KeyNames.TryGetVk("esc", out var esc);

        Assert.True(Key("capslock", down: true));
        Assert.True(Key("capslock", down: true)); // 长按重复:也要吞,并继续发 esc 的重复
        Assert.True(Key("capslock", down: false));

        Assert.Equal([(esc, true), (esc, true), (esc, false)], _sender.Keys);
    }

    [Fact]
    public void 过滤器吞掉抬起_即使按下时是放行的()
    {
        _pipeline.AddFilter(new Recorder(e => e is { Kind: KeyEventKind.Up, Key: "f5" } ? FilterDecision.Consume : FilterDecision.Pass));

        Assert.False(Key("f5", down: true));
        Assert.True(Key("f5", down: false));
    }

    [Fact]
    public void 自己发出的合成事件_不再进入过滤器_不会死循环()
    {
        var filter = new Recorder();
        _pipeline.AddFilter(filter);

        Key("esc", down: true, ours: true);
        Key("esc", down: false, ours: true);

        Assert.Empty(filter.Seen);
    }

    [Fact]
    public void 发射器未知键名_被忽略_不抛异常()
    {
        _pipeline.AddFilter(new LambdaFilter((_, emitter) =>
        {
            emitter.KeyDown("no-such-key");
            return FilterDecision.Pass;
        }));

        var ex = Record.Exception(() => Key("f5", down: true));

        Assert.Null(ex);
        Assert.Empty(_sender.Keys);
    }

    private sealed class LambdaFilter(Func<KeyEvent, IKeyEmitter, FilterDecision> body) : IRealtimeKeyFilter
    {
        public FilterDecision OnKey(in KeyEvent keyEvent, IKeyEmitter emitter) => body(keyEvent, emitter);
    }
}
