using Waio.Kit.Input;

namespace Waio.Kit.Services;

/// <summary>
/// 功能的设置。每个功能的设置是一个 POCO(带 schemaVersion 字段),存成 features/&lt;featureId&gt;.json。
/// 规则:字段只增不删;解析失败时备份坏文件并用默认值,绝不因配置损坏而崩溃。
/// </summary>
public interface IFeatureSettings
{
    /// <summary>读取当前设置;文件不存在或损坏时返回 new T()。</summary>
    T Get<T>() where T : class, new();

    ValueTask SaveAsync<T>(T value, CancellationToken cancellationToken = default) where T : class, new();

    /// <summary>设置变化(包括用户在设置窗口里修改)。订阅后实时生效,不需要重启。</summary>
    event Action Changed;
}

/// <summary>功能私有数据目录:%LOCALAPPDATA%\WAIO\data\&lt;featureId&gt;\。功能不得访问其他位置。</summary>
public interface IFeatureStorage
{
    string DataDirectory { get; }
}

/// <summary>UI 无关的浮层抽象,具体渲染由 Shell 用 WinUI 3 实现。</summary>
public interface IOverlayService
{
    /// <summary>屏幕上短暂显示一条 HUD 提示。</summary>
    ValueTask ShowHudAsync(string text, TimeSpan? duration = null);
}

public sealed record ForegroundWindowInfo(nint Handle, string ProcessName, string Title);

/// <summary>系统事实事件。不使用全局消息总线做跨功能通信:请求用命令,事实用事件。</summary>
public interface ISystemEvents
{
    /// <summary>当前前台窗口(引擎维护的原子快照,可在任意线程读取)。</summary>
    ForegroundWindowInfo? Foreground { get; }

    event Action<ForegroundWindowInfo> ForegroundChanged;
}

/// <summary>快捷键占用登记。保存绑定时用于冲突检测(已有绑定、已知系统快捷键)。</summary>
public interface IShortcutDirectory
{
    /// <summary>登记占用;已被他人占用时返回 false 并给出占用方。</summary>
    bool TryClaim(KeyChord chord, string owner, out string? conflictOwner);

    void Release(KeyChord chord, string owner);

    bool IsClaimed(KeyChord chord);
}

/// <summary>
/// 本功能的密钥(API Key 等,ADR-0014)。只用当前 Windows 用户能解开的方式加密保存在本机,不写进设置文件。
/// 名字由功能自己定(如 "deepl.authKey"),只在本功能内可见。任意线程可调用。
/// </summary>
public interface ISecretStore
{
    /// <summary>读取;没有或无法解密(如换了 Windows 用户)返回 null。</summary>
    string? Get(string name);

    /// <summary>保存;<paramref name="value"/> 为 null 或空白 = 删除。写盘失败抛 IOException。</summary>
    void Set(string name, string? value);
}
