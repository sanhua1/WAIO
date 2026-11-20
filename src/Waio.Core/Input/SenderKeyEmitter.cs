using Waio.Kit.Input;

namespace Waio.Core.Input;

/// <summary>
/// 给实时过滤器用的 <see cref="IKeyEmitter"/>:键名 → 虚拟键码 → 带 WAIO 标记发出。
/// 整个管线共用一个实例(过滤器不得保存它),调用路径上不分配内存。
/// </summary>
internal sealed class SenderKeyEmitter(IKeySender sender) : IKeyEmitter
{
    public void KeyDown(string key)
    {
        if (KeyNames.TryGetVk(key, out var vk))
        {
            sender.SendKey(vk, down: true);
        }
    }

    public void KeyUp(string key)
    {
        if (KeyNames.TryGetVk(key, out var vk))
        {
            sender.SendKey(vk, down: false);
        }
    }
}
