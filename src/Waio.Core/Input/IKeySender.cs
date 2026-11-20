using Waio.Kit.Input;

namespace Waio.Core.Input;

/// <summary>钩子线程送进管线的原始按键。<see cref="IsOurs"/> 表示这是 WAIO 自己发出的合成事件。</summary>
/// <param name="Time">事件时间戳(毫秒,KBDLLHOOKSTRUCT.time,会回绕);0 表示未知。</param>
internal readonly record struct RawKey(int VkCode, bool IsDown, bool IsOurs, uint Time = 0);

/// <summary>
/// 查询某个键此刻是否真的被物理按下。管线靠事件流维护按键状态,但事件可能丢失
/// (钩子回调超时被系统跳过;Win+L、UAC、Ctrl+Alt+Del 切到安全桌面后收不到"抬起"),
/// 因此在关键时刻用它与现实核对、重新同步,否则修饰键会永久"卡住"。
/// <para>
/// ⚠️ <b>被低级钩子吞掉的键,系统不会更新它的状态</b>:对 WAIO 吞掉的键,这里永远返回"没按着"。
/// 所以只能用它核对 WAIO 放行了的键(2026-11-10 的 bug:拿它核对被吞的 F24,导致 F24+Z 失效)。
/// </para>
/// </summary>
internal interface IKeyStateProbe
{
    bool IsDown(int vk);
}

internal sealed class WindowsKeyStateProbe : IKeyStateProbe
{
    public bool IsDown(int vk) => (InputNative.GetAsyncKeyState(vk) & 0x8000) != 0;
}

/// <summary>管线对外发送按键的出口。抽成接口,管线单测里用假的记录调用。</summary>
internal interface IKeySender
{
    void Send(KeyChord chord);

    /// <summary>发出单个按键事件(带 WAIO 标记),供实时过滤器改键用。</summary>
    void SendKey(int vk, bool down);

    /// <summary>
    /// 发一个无意义的按键(并带 WAIO 标记),用来打断"单按 Win/Alt 再松开"的系统行为:
    /// 吞掉 Win+X 后,若不这样做,松开 Win 会弹出开始菜单。
    /// </summary>
    void TapMaskKey();
}
