using Waio.Kit.Input;

namespace Waio.Core.Input;

/// <summary>
/// 自定义修饰键(ADR-0012):用户声明的键(如 F24)按住时像修饰键一样参与组合("f24+j"),单独轻点时仍是它自己。
/// <list type="bullet">
/// <item>按下:吞掉(目标程序看不到),记为"按住中";长按重复也吞。</item>
/// <item>按住期间按下别的键:组合里带上它去匹配("f24+j");无论是否命中,都记为"用过了"。没命中的键照常放行。</item>
/// <item>松开:如果期间没按别的键,算一次"轻点"——有 "f24" 这个快捷键就触发它,否则把 F24 补发给目标程序,
/// 所以原本靠 F24 工作的程序不受影响。</item>
/// </list>
/// 代价:单独的 F24 快捷键改为在<b>松开</b>时触发。同一时间只有一个自定义修饰键生效,按住 F24 时再按 F23,F23 当普通键。
/// <para>
/// "抬起"丢了怎么办:<b>不能</b>问系统 F24 还按没按着——它的按下被我们吞了,系统状态永远是"松开"(曾因此导致 F24+Z 失效)。
/// 改为:再次按下时若离上次超过自动重复的最长间隔,当作新的按下(<c>LostUp</c>);锁屏解锁/切换会话后整体清空(<c>RequestReset</c>)。
/// </para>
/// </summary>
internal sealed partial class InputPipeline
{
    private HashSet<string> _holdKeys = new(StringComparer.Ordinal);

    // 以下状态只在钩子线程访问
    private string? _held;
    private bool _heldUsed;

    /// <summary>设置自定义修饰键(规范键名)。可在任意线程调用,原子替换。</summary>
    public void SetCustomModifiers(IEnumerable<string> keys) =>
        Volatile.Write(ref _holdKeys, new HashSet<string>(keys.Select(KeyNames.Canonicalize), StringComparer.Ordinal));

    /// <summary>当前声明的自定义修饰键(录制器用)。</summary>
    public IReadOnlySet<string> CustomModifiers => Volatile.Read(ref _holdKeys);

    /// <summary>这个键要按自定义修饰键处理:已声明,且当前没有按住别的自定义修饰键。</summary>
    private bool IsHoldKey(string key) => (_held is null || _held == key) && Volatile.Read(ref _holdKeys).Contains(key);

    /// <returns>是否吞掉这个事件(按下和重复总是吞;抬起的吞掉由"按下被吞"的记录负责)。</returns>
    private bool OnHoldKey(in RawKey raw, string key, KeyModifiers modifiers, bool isRepeat)
    {
        if (raw.IsDown)
        {
            if (!isRepeat)
            {
                _held = key;
                _heldUsed = false;
            }

            return true;
        }

        if (_held != key)
        {
            return false;
        }

        var tapped = !_heldUsed;
        _held = null;
        if (tapped && !Fire(new KeyChord(modifiers, key)))
        {
            // 轻点且没有对应的快捷键:把它本身补发给目标程序(按下时被吞了)
            _sender.SendKey(raw.VkCode, down: true);
            _sender.SendKey(raw.VkCode, down: false);
        }

        return true;
    }
}
