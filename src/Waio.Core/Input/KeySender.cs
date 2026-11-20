using System.Runtime.InteropServices;
using Waio.Kit.Input;

namespace Waio.Core.Input;

/// <summary>用 SendInput 发合成按键。所有事件都带 WAIO 魔数,钩子据此放行自己发出的事件。</summary>
internal sealed class KeySender : IKeySender
{
    private static readonly int InputSize = Marshal.SizeOf<InputNative.INPUT>();

    public void Send(KeyChord chord) => Send(chord, []);

    /// <param name="heldModifiers">
    /// 用户此刻物理按着的修饰键(虚拟键码)。不属于 <paramref name="chord"/> 的先发"抬起",否则会和组合叠在一起
    /// ——例:按 Alt+Q 触发翻译时 Alt 还按着,直接发 Ctrl+C,目标程序收到的是 Ctrl+Alt+C。不会再补发按下:
    /// 用户之后松开时系统只是多收到一次抬起,无害。Win/Alt 的"单按弹菜单"已由命中快捷键时的掩码键抵消。
    /// </param>
    public void Send(KeyChord chord, IReadOnlyList<int> heldModifiers) => Submit(Build(chord, heldModifiers));

    /// <summary>组合键对应的事件序列(纯函数,单测用;不发送)。</summary>
    internal static List<InputNative.INPUT> Build(KeyChord chord, IReadOnlyList<int> heldModifiers)
    {
        if (!KeyNames.TryGetVk(chord.Key, out var vk))
        {
            throw new ArgumentException($"未知的键名 '{chord.Key}'", nameof(chord));
        }

        var modifiers = ModifierVks(chord.Modifiers);
        if (chord.Hold is { } hold && KeyNames.TryGetVk(hold, out var holdVk))
        {
            modifiers.Add(holdVk); // 自定义修饰键按"先按住、最后松开"的方式发出
        }

        var wanted = KeyNames.ToModifiers(modifiers.Sum(m => KeyNames.ModifierBit(m)));
        var inputs = new List<InputNative.INPUT>(modifiers.Count * 2 + 2 + heldModifiers.Count);
        inputs.AddRange(heldModifiers
            .Where(m => (KeyNames.ToModifiers(KeyNames.ModifierBit(m)) & wanted) == 0)
            .Select(m => Key(m, down: false)));
        inputs.AddRange(modifiers.Select(m => Key(m, down: true)));
        inputs.Add(Key(vk, down: true));
        inputs.Add(Key(vk, down: false));
        inputs.AddRange(Enumerable.Reverse(modifiers).Select(m => Key(m, down: false)));
        return inputs;
    }

    public void SendKey(int vk, bool down) => Submit([Key(vk, down)]);

    public void TapMaskKey() =>
        Submit([Key(KeyNames.MaskKeyVk, down: true), Key(KeyNames.MaskKeyVk, down: false)]);

    private static void Submit(List<InputNative.INPUT> inputs)
    {
        var array = inputs.ToArray();
        var sent = InputNative.SendInput((uint)array.Length, array, InputSize);
        if (sent != array.Length)
        {
            // 被 UIPI 阻止(目标是更高权限窗口)或桌面被锁定时会少发;不抛异常,调用方无法处理
            System.Diagnostics.Debug.WriteLine($"SendInput 只发出了 {sent}/{array.Length} 个事件,错误码 {Marshal.GetLastWin32Error()}");
        }
    }

    private static List<int> ModifierVks(KeyModifiers modifiers)
    {
        var list = new List<int>(4);
        if (modifiers.HasFlag(KeyModifiers.Ctrl)) { list.Add(0xA2); }
        if (modifiers.HasFlag(KeyModifiers.Shift)) { list.Add(0xA0); }
        if (modifiers.HasFlag(KeyModifiers.Alt)) { list.Add(0xA4); }
        if (modifiers.HasFlag(KeyModifiers.Win)) { list.Add(0x5B); }
        return list;
    }

    /// <summary>
    /// 同时填虚拟键码和<b>扫描码</b>。只填虚拟键码时扫描码是 0:记事本等按虚拟键码处理的程序没问题,
    /// 但终端(otty、Windows Terminal 等)、游戏、部分跨平台程序按物理扫描码识别按键,收到"扫描码 0"就认不出是哪个键
    /// ——2026-11-10 用户报告剪贴板面板在 otty 终端里粘贴无效,判断为此原因(修复后待真机确认)。
    /// </summary>
    internal static InputNative.INPUT Key(int vk, bool down)
    {
        var flags = down ? 0u : InputNative.KEYEVENTF_KEYUP;
        if (KeyNames.IsExtended(vk))
        {
            flags |= InputNative.KEYEVENTF_EXTENDEDKEY;
        }

        var scan = (ushort)(InputNative.MapVirtualKeyW((uint)vk, InputNative.MAPVK_VK_TO_VSC) & 0xFF);
        return new InputNative.INPUT
        {
            Type = InputNative.INPUT_KEYBOARD,
            Keyboard = new InputNative.KEYBDINPUT { Vk = (ushort)vk, Scan = scan, Flags = flags, ExtraInfo = InputNative.Magic },
        };
    }
}
