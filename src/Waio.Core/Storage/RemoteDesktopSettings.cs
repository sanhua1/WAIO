namespace Waio.Core.Storage;

/// <summary>
/// app.json 里的远程桌面设置(ADR-0033):名单里的程序在前台时,WAIO 的快捷键和改键让出,按键原样交给远程软件转发。
/// 字段只增不删。
/// </summary>
public sealed class RemoteDesktopSettings
{
    /// <summary>
    /// 默认名单(进程名,不含 .exe)。只有 UU 远程(GameViewer)、ToDesk 核对过安装目录里的程序名,
    /// 其余按公开资料填写;控制远程电脑的窗口是不是这个进程都没在真机确认。
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultApps =
    [
        "GameViewer", // 网易 UU 远程
        "ToDesk",
        "SunloginClient", // 向日葵
        "mstsc", // Windows 远程桌面连接
        "msrdc", // Windows App / 远程桌面客户端
        "parsecd", // Parsec
        "TeamViewer",
        "AnyDesk",
        "rustdesk",
        "vncviewer",
    ];

    /// <summary>远程桌面程序在前台时让出按键。默认开。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>算作远程桌面的程序(进程名)。</summary>
    public List<string> Apps { get; set; } = [.. DefaultApps];

    /// <summary>当前生效的名单:关闭时为空;配置是不可信输入,规范化(去空白、去 .exe)并去重。</summary>
    public IReadOnlyList<string> ActiveApps() => Enabled ? Normalize(Apps) : [];

    /// <summary>规范化进程名:去掉首尾空白和 .exe 后缀,丢掉空项和含路径分隔符的项,不区分大小写去重。</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? names) =>
        [.. (names ?? [])
            .Select(NormalizeOne)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>规范化一个进程名;不合法返回 null。</summary>
    public static string? NormalizeOne(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4].TrimEnd();
        }

        return trimmed.Length == 0 || trimmed.IndexOfAny(['\\', '/', ':']) >= 0 ? null : trimmed;
    }
}
