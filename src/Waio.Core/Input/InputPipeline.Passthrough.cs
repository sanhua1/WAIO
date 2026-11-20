namespace Waio.Core.Input;

/// <summary>
/// 远程桌面让出按键(ADR-0033):名单里的程序在前台时,快捷键、改键过滤器、自定义修饰键都不执行,
/// 按键原样交给它(由远程软件转发给远程电脑)。
/// <list type="bullet">
/// <item>照常:键盘会话(快捷键录制等)——会话期间按键归会话。</item>
/// <item>进入让出前已被吞掉按下的键,松开(和长按重复)照样吞掉,目标程序不会收到孤立的抬起。</item>
/// <item>刚切进让出状态时(下一个按键事件之前),过滤器 <c>OnReset</c>:释放它们发出、还没抬起的键,清掉层 / 双功能键的按住状态;
/// 按住中的自定义修饰键作废(它的按下已被吞,抬起由"已吞"记录吞掉,不补发)。</item>
/// </list>
/// </summary>
internal sealed partial class InputPipeline
{
    private HashSet<string> _passthroughApps = new(StringComparer.OrdinalIgnoreCase);

    // 只在钩子线程访问
    private bool _inPassthrough;

    /// <summary>设置让出按键的程序(进程名,不含 .exe,不区分大小写)。可在任意线程调用,原子替换;空 = 关闭。</summary>
    public void SetPassthroughApps(IEnumerable<string> processNames) =>
        Volatile.Write(ref _passthroughApps, new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 这个事件要原样让给前台的远程桌面程序吗?刚进入让出状态时顺带清理过滤器和自定义修饰键的按住状态。
    /// 只在钩子线程、且没有键盘会话时调用。
    /// </summary>
    private bool EnterOrStayPassthrough()
    {
        var apps = Volatile.Read(ref _passthroughApps);
        var active = apps.Count > 0 && _foregroundProcess() is { Length: > 0 } process && apps.Contains(process);
        if (active && !_inPassthrough)
        {
            _held = null;
            foreach (var filter in Volatile.Read(ref _filters))
            {
                try
                {
                    filter.OnReset(_emitter);
                }
                catch (Exception ex)
                {
                    _onError(ex);
                }
            }
        }

        _inPassthrough = active;
        return active;
    }
}
