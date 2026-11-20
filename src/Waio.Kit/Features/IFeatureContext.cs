using Microsoft.Extensions.Logging;
using Waio.Kit.Commands;
using Waio.Kit.Input;
using Waio.Kit.Services;

namespace Waio.Kit.Features;

/// <summary>
/// 功能能接触到的全部世界。功能拿不到任何全局单例,
/// 测试时可以把整个 context 换成假实现(Waio.Testing)。
/// </summary>
public interface IFeatureContext
{
    FeatureId FeatureId { get; }

    /// <summary>注册本功能的命令。只能注册 manifest 里声明过的命令。</summary>
    ICommandRegistrar Commands { get; }

    /// <summary>调用其他功能提供的契约命令(§4.3)。提供方未启用时抛 CommandException(Unavailable)。</summary>
    ICommandBus CommandBus { get; }

    /// <summary>键盘会话、实时过滤器、合成按键。全仓库唯一的输入入口。</summary>
    IInputService Input { get; }

    /// <summary>只能读写本功能命名空间下的设置。</summary>
    IFeatureSettings Settings { get; }

    /// <summary>只能访问本功能的数据目录。</summary>
    IFeatureStorage Storage { get; }

    /// <summary>本功能的密钥(API Key 等),加密保存在本机(ADR-0014)。</summary>
    ISecretStore Secrets { get; }

    /// <summary>浮层、HUD、提示(UI 无关的抽象,具体渲染由 Shell 实现)。</summary>
    IOverlayService Overlay { get; }

    /// <summary>系统事实事件:前台窗口变化等。事实用事件,请求用命令。</summary>
    ISystemEvents Events { get; }

    /// <summary>快捷键占用登记与查询,用于冲突检测。</summary>
    IShortcutDirectory Shortcuts { get; }

    /// <summary>启动外部程序/文件/链接。默认降权(ADR-0006);功能不得自己 Process.Start。</summary>
    ILauncher Launcher { get; }

    /// <summary>系统剪贴板的监听与读写。</summary>
    IClipboardService Clipboard { get; }

    /// <summary>UI 线程投递。需要创建窗口的功能用它。</summary>
    IUiThread Ui { get; }

    /// <summary>已按 FeatureId 分类的日志器。</summary>
    ILogger Logger { get; }
}
