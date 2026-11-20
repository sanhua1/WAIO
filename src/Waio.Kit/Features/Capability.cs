namespace Waio.Kit.Features;

/// <summary>
/// 功能声明自己需要的系统能力。宿主据此做启动前检查、设置页提示和故障隔离。
/// Windows 没有 macOS 那种逐项授权的 TCC,这里描述的是"运行条件",不是弹窗授权。
/// </summary>
[Flags]
public enum Capability
{
    None = 0,

    /// <summary>需要全局键盘/鼠标钩子(通过 IInputService,不得自己装钩子)。</summary>
    InputHooks = 1 << 0,

    /// <summary>需要管理员权限(如读取 NTFS MFT 做全盘索引)。受 ADR-0004 的权限策略约束。</summary>
    Elevated = 1 << 1,

    /// <summary>需要屏幕捕获。</summary>
    ScreenCapture = 1 << 2,

    /// <summary>需要联网(翻译、检查更新)。</summary>
    Network = 1 << 3,
}
