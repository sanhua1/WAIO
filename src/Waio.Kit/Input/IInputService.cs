using System.Threading.Channels;

namespace Waio.Kit.Input;

/// <summary>
/// 功能接触键盘/鼠标的唯一入口。全仓库只有 Waio.Core/Input 允许安装系统钩子,
/// 功能不得直接调用 SetWindowsHookEx / RegisterRawInputDevices(架构测试会扫描源码)。
/// </summary>
public interface IInputService
{
    /// <summary>
    /// 注册按键实时过滤器。在钩子线程上同步调用,位置在键盘会话之后、快捷键匹配之前。
    /// 返回的 token 释放即注销。过滤器的约束见 <see cref="IRealtimeKeyFilter"/>。
    /// </summary>
    IDisposable RegisterKeyFilter(IRealtimeKeyFilter filter);

    /// <summary>
    /// 开启键盘会话:会话存续期间,匹配 <paramref name="options"/> 的按键事件全部交给会话,
    /// 不再走快捷键匹配。例:启动台"按住 Alt 期间持续接收 Tab/方向键,松开 Alt 时确认"。
    /// 这是功能拿到原始按键流的唯一途径。
    /// </summary>
    IKeyboardSession BeginKeyboardSession(KeyboardSessionOptions options);

    /// <summary>
    /// 发送合成按键。引擎统一给事件打标记(dwExtraInfo + INJECTED 标志),
    /// 以避免自己处理自己发出的事件而死循环。功能不得自己调用 SendInput。
    /// </summary>
    void SendKeys(KeyChord chord);

    /// <summary>
    /// 把用户配置里写的键名规范化("escape"→"esc"、"caps"→"capslock"、大小写);不认识返回 null。
    /// 功能校验配置时用它——键名表属于输入引擎,功能不应各自维护一份。
    /// </summary>
    string? NormalizeKey(string name);

    /// <summary>
    /// 把锁定键("capslock"、"numlock"、"scrolllock")的锁定状态设为 <paramref name="on"/>;已经是则什么都不做(ADR-0013)。
    /// 例:Super Caps 把 CapsLock 吞掉后,用户再也无法自己关掉大写锁定,所以启用时先把它关掉。
    /// 不得在钩子线程(过滤器里)调用。
    /// </summary>
    /// <exception cref="ArgumentException">不是锁定键。</exception>
    void SetLockState(string key, bool on);
}

/// <summary>
/// 按键实时过滤器(ADR-0008):看到每一个物理按键事件(按下、抬起、长按重复),可以放行、吞掉,
/// 并通过 <see cref="IKeyEmitter"/> 同步发出替代按键——改键、双功能键(轻点/按住)靠它实现。
/// <para>
/// ⚠️ 在钩子线程同步调用:不得阻塞、不得做 IO、不得访问 UI 线程状态、尽量不分配内存。
/// Windows 的低级钩子回调超时,系统会静默摘除钩子,表现为"全局快捷键莫名其妙全部失效"。
/// 需要耗时处理时,只做判断并返回,真正的工作丢给命令总线异步执行。
/// 实现类必须放在功能目录的 Realtime/ 子目录(架构测试会校验该目录里没有 UI 线程 API)。
/// </para>
/// </summary>
public interface IRealtimeKeyFilter
{
    /// <param name="emitter">仅在本次调用期间有效,不得保存。</param>
    FilterDecision OnKey(in KeyEvent keyEvent, IKeyEmitter emitter);

    /// <summary>
    /// 输入引擎认为期间的"抬起"事件已经丢失时调用(锁屏解锁、切换会话后,在下一个按键事件之前,钩子线程上;ADR-0013)。
    /// 远程桌面程序切到前台、引擎开始把按键让给它时也会调用(ADR-0033)。
    /// 过滤器应清掉"某键按住中"之类的状态,并用 <paramref name="emitter"/> 释放自己发出、还没抬起的键。默认什么都不做。
    /// </summary>
    /// <param name="emitter">仅在本次调用期间有效,不得保存。</param>
    void OnReset(IKeyEmitter emitter)
    {
    }
}

/// <summary>
/// 在钩子线程上同步发出单个合成按键事件。事件由引擎打标记,不会再次进入过滤器(不会死循环)。
/// 注意:合成事件不会更新引擎内部的修饰键状态,因此"改成修饰键"的键对 WAIO 自己的快捷键匹配不可见(目标程序能正常看到)。
/// </summary>
public interface IKeyEmitter
{
    /// <summary>按下 <paramref name="key"/>(键名同 <see cref="KeyEvent.Key"/>,如 "esc"、"lctrl")。未知键名被忽略。</summary>
    void KeyDown(string key);

    void KeyUp(string key);
}

public sealed record KeyboardSessionOptions(
    /// <summary>会话在哪个修饰键松开时自动结束,如 Alt。None 表示由功能显式 Dispose。</summary>
    KeyModifiers EndOnRelease = KeyModifiers.None,

    /// <summary>会话期间是否把事件吞掉(不让目标程序收到)。</summary>
    bool ConsumeEvents = true);

public interface IKeyboardSession : IAsyncDisposable
{
    /// <summary>会话期间的按键事件流。会话结束后通道完成。</summary>
    ChannelReader<KeyEvent> Events { get; }
}
