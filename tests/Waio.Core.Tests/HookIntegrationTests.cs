using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Waio.Core.Commands;
using Waio.Core.Input;
using Waio.Kit.Commands;
using Waio.Kit.Input;
using Xunit.Abstractions;

namespace Waio.Core.Tests;

/// <summary>仅在设置环境变量 WAIO_INTEGRATION=1 时运行:它会真实安装全局键盘钩子,并向系统注入按键。</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WAIO_INTEGRATION") != "1")
        {
            Skip = "设置环境变量 WAIO_INTEGRATION=1 才运行(会真实安装键盘钩子并注入按键)";
        }
    }
}

/// <summary>
/// 钩子延迟测量。用一个任何键盘上都不存在的组合 Ctrl+Alt+Shift+F23 做测试,
/// 即使钩子失效、按键漏给前台程序也基本无害。
/// </summary>
public class HookIntegrationTests(ITestOutputHelper output)
{
    // 规模刻意压小(约几十秒),并且一旦发现真实键盘事件就立刻中止:测试期间请不要碰键盘
    private const int Samples = 60;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(20);

    [IntegrationFact]
    public async Task 真实钩子_命中并吞掉_报告回调耗时()
    {
        var result = await RunAsync(gcStress: false, withAlt: true);

        Report("安静环境,带 Alt(会在回调里发掩码键)", result);
        if (result.Aborted is null)
        {
            Assert.Equal(Samples, result.Hits);
            Assert.True(result.MaxCallbackUs < 50_000, $"最大回调耗时 {result.MaxCallbackUs:N0}µs,逼近系统摘钩子的红线");
        }
    }

    [IntegrationFact]
    public async Task 真实钩子_安静环境_不带Alt_作为对照()
    {
        var result = await RunAsync(gcStress: false, withAlt: false);

        Report("安静环境,不带 Alt/Win(回调里不发掩码键)", result);
        if (result.Aborted is null)
        {
            Assert.Equal(Samples, result.Hits);
        }
    }

    [IntegrationFact]
    public async Task 真实钩子_GC压力下_报告回调耗时()
    {
        var result = await RunAsync(gcStress: true, withAlt: true);

        Report("GC 压力(持续分配 + 周期性 Gen2 回收),带 Alt", result);
        if (result.Aborted is null)
        {
            Assert.Equal(Samples, result.Hits);
            Assert.True(result.MaxCallbackUs < 50_000, $"GC 压力下最大回调耗时 {result.MaxCallbackUs:N0}µs");
        }
    }

    /// <param name="Aborted">非空 = 测量无效(用户碰了键盘/超时),原因写在这里;此时不做任何断言。</param>
    private record Result(
        int Hits, int Sent, double AvgCallbackUs, double MaxCallbackUs, double[] EndToEndMs,
        long Resyncs, double GcPauseMs, int Gen2Collections, string? Aborted);

    private async Task<Result> RunAsync(bool gcStress, bool withAlt)
    {
        var gcPauseBefore = GC.GetTotalPauseDuration();
        var gen2Before = GC.CollectionCount(2);
        var hitTimes = new ConcurrentQueue<long>();
        var bus = new CommandBus();
        bus.Register("test.hit", (_, _) =>
        {
            hitTimes.Enqueue(Stopwatch.GetTimestamp());
            return ValueTask.CompletedTask;
        });

        await using var engine = new InputEngine(bus, NullLogger.Instance, () => "integration-test");
        engine.SetBindings(
        [
            new CommandBinding(
                Guid.NewGuid(),
                new HotkeyTrigger(KeyChord.Parse(withAlt ? "ctrl+alt+shift+f23" : "ctrl+shift+f23")),
                "test.hit",
                CommandArguments.Empty,
                BindingScope.Global),
        ]);
        engine.Start();

        using var stop = new CancellationTokenSource();
        Task? churn = null;
        if (gcStress)
        {
            churn = Task.Run(() =>
            {
                var rng = new Random(1);
                var keep = new List<byte[]>();
                var n = 0;
                while (!stop.IsCancellationRequested)
                {
                    keep.Add(new byte[rng.Next(1_000, 200_000)]);
                    if (keep.Count > 400) { keep.RemoveRange(0, 200); }
                    if (++n % 50 == 0) { GC.Collect(2, GCCollectionMode.Forced, blocking: true); }
                }
            });
        }

        var latencies = new double[Samples];
        var sent = 0;
        string? aborted = null;
        var total = Stopwatch.StartNew();
        for (var i = 0; i < Samples; i++)
        {
            if (engine.Diagnostics.PhysicalEvents > 0)
            {
                aborted = $"检测到 {engine.Diagnostics.PhysicalEvents} 个真实键盘事件(用户碰了键盘),已在第 {i} 轮中止";
                break;
            }

            if (total.Elapsed > MaxDuration)
            {
                aborted = $"超过 {MaxDuration.TotalSeconds:N0}s 总时长上限,已在第 {i} 轮中止";
                break;
            }

            sent++;
            var before = Stopwatch.GetTimestamp();
            SendChord(withAlt);
            var deadline = Stopwatch.StartNew();
            while (hitTimes.Count <= i && deadline.ElapsedMilliseconds < 2000)
            {
                await Task.Delay(1);
            }

            if (hitTimes.Count > i)
            {
                var hitAt = hitTimes.ToArray()[i];
                latencies[i] = (hitAt - before) * 1000.0 / Stopwatch.Frequency;
            }
            else
            {
                latencies[i] = double.NaN;
            }

            await Task.Delay(5);
        }

        stop.Cancel();
        if (churn is not null)
        {
            await churn;
        }

        var diag = engine.Diagnostics;
        if (aborted is null && diag.PhysicalEvents > 0)
        {
            aborted = $"测试期间出现 {diag.PhysicalEvents} 个真实键盘事件";
        }

        return new Result(
            hitTimes.Count, sent, diag.AverageCallbackMicroseconds, diag.MaxCallbackMicroseconds,
            [.. latencies.Where(l => !double.IsNaN(l)).Order()],
            diag.StateResyncs,
            (GC.GetTotalPauseDuration() - gcPauseBefore).TotalMilliseconds,
            GC.CollectionCount(2) - gen2Before,
            aborted);
    }

    private void Report(string title, Result r)
    {
        if (r.Aborted is not null)
        {
            output.WriteLine($"[{title}] ⚠️ 测量无效:{r.Aborted}(已发 {r.Sent} 轮,命中 {r.Hits})。请在不碰键盘的情况下重跑。");
            return;
        }

        double P(double p) => r.EndToEndMs.Length == 0 ? double.NaN : r.EndToEndMs[(int)Math.Min(r.EndToEndMs.Length - 1, p * r.EndToEndMs.Length)];
        output.WriteLine($"[{title}] 命中 {r.Hits}/{r.Sent};" +
                         $"回调(钩子线程内)平均 {r.AvgCallbackUs:N1}µs/最大 {r.MaxCallbackUs:N0}µs;" +
                         $"注入→命令执行 P50 {P(0.5):N2}/P95 {P(0.95):N2}/P99 {P(0.99):N2}/最大 {P(1):N2}ms;" +
                         $"状态重新同步 {r.Resyncs} 次;GC 暂停合计 {r.GcPauseMs:N0}ms(Gen2 {r.Gen2Collections} 次)");
    }

    // ---- 测试自己的"未打标记"注入:引擎会把它当作真实用户按键处理 ----

    private static void SendChord(bool withAlt)
    {
        int[] mods = withAlt ? [0xA2, 0xA4, 0xA0] : [0xA2, 0xA0]; // ctrl, (alt), shift
        const int f23 = 0x86;
        var list = new List<Input>();
        list.AddRange(mods.Select(m => Key(m, down: true)));
        list.Add(Key(f23, down: true));
        list.Add(Key(f23, down: false));
        list.AddRange(mods.Reverse().Select(m => Key(m, down: false)));
        var array = list.ToArray();
        SendInput((uint)array.Length, array, Marshal.SizeOf<Input>());
    }

    private static Input Key(int vk, bool down) => new()
    {
        Type = 1,
        Ki = new KeybdInput { Vk = (ushort)vk, Flags = down ? 0u : 2u, ExtraInfo = 0 },
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Explicit)]
    private struct Input
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public KeybdInput Ki;

        [FieldOffset(8)]
        public MouseInput Mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}
