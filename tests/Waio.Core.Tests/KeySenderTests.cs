using Waio.Core.Input;

namespace Waio.Core.Tests;

/// <summary>只检查构造出来的 INPUT 结构,不真正调用 SendInput(不会影响桌面)。</summary>
public class KeySenderTests
{
    [Theory]
    [InlineData(0x56)] // V
    [InlineData(0xA2)] // 左 Ctrl
    [InlineData(0x0D)] // Enter
    [InlineData(0x25)] // ← (扩展键)
    public void 合成按键_带扫描码_终端等按物理键识别的程序才认得(int vk)
    {
        var input = KeySender.Key(vk, down: true);

        Assert.Equal((ushort)vk, input.Keyboard.Vk);
        Assert.NotEqual(0, input.Keyboard.Scan);
        Assert.Equal(InputNative.Magic, input.Keyboard.ExtraInfo);
    }

    [Fact]
    public void 扩展键_带扩展标志_抬起带抬起标志()
    {
        var up = KeySender.Key(0x25, down: false);

        Assert.NotEqual(0u, up.Keyboard.Flags & InputNative.KEYEVENTF_EXTENDEDKEY);
        Assert.NotEqual(0u, up.Keyboard.Flags & InputNative.KEYEVENTF_KEYUP);
    }
}

/// <summary>发组合键前先松开用户按着、但不属于组合的修饰键(翻译:按 Alt+Q 时发 Ctrl+C,ADR-0014)。</summary>
public class KeySenderChordTests
{
    private static List<(int Vk, bool Down)> Sequence(string chord, params int[] held) =>
        [.. KeySender.Build(Waio.Kit.Input.KeyChord.Parse(chord), held)
            .Select(i => ((int)i.Keyboard.Vk, (i.Keyboard.Flags & InputNative.KEYEVENTF_KEYUP) == 0))];

    [Fact]
    public void 用户按着左Alt时发CtrlC_先抬起Alt_不然目标程序收到CtrlAltC()
    {
        Assert.Equal([(0xA4, false), (0xA2, true), (0x43, true), (0x43, false), (0xA2, false)], Sequence("ctrl+c", 0xA4));
    }

    [Fact]
    public void 用户按着的修饰键属于这个组合_不动它()
    {
        Assert.Equal([(0xA2, true), (0x43, true), (0x43, false), (0xA2, false)], Sequence("ctrl+c", 0xA3)); // 右 Ctrl 也是 Ctrl
    }

    [Fact]
    public void 没有按着的修饰键_和以前一样()
    {
        Assert.Equal([(0xA2, true), (0x56, true), (0x56, false), (0xA2, false)], Sequence("ctrl+v"));
    }
}
