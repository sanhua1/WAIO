namespace Waio.Core.Bindings;

/// <summary>系统快捷键清单的数据。新增条目:找到对应分组加一行;组合重复会在启动时抛异常(有测试兜底)。</summary>
public static partial class KnownSystemShortcuts
{
    private static void Fill(Action<string> group, Action<string, string, string?> add, Action<string, string, string> reserved)
    {
        group("常用");
        add("win+r", "运行", null);
        add("win+e", "资源管理器", null);
        add("win+i", "设置", null);
        add("win+v", "剪贴板历史", null);
        add("win+s", "搜索", null);
        add("win+q", "搜索", null);
        add("win+x", "快速链接菜单(相当于右键开始按钮)", null);
        add("win+a", "快速设置(操作中心)", null);
        add("win+n", "通知中心和日历", null);
        add("win+shift+v", "依次切换通知", null);
        add("win+c", "Copilot", "没有 Copilot 时是打开 / 关闭搜索;旧版系统是 Cortana / 聊天");
        add("win+w", "小组件", null);
        add("win+j", "Recall(回顾)", "只在支持 Recall 的 Copilot+ 电脑上有效");
        add("win+f", "反馈中心", null);
        add("win+k", "投屏(连接无线显示器)", null);
        add("win+p", "投影模式(多显示器显示方式)", null);
        add("win+o", "锁定屏幕方向", "平板 / 二合一设备");
        add("win+u", "设置 › 辅助功能", null);
        add("win+pause", "设置 › 系统 › 关于", null);
        add("win+alt+d", "在桌面上显示 / 隐藏日期和时间", null);
        add("win+alt+k", "麦克风静音 / 取消静音(支持的通话程序)", null);
        add("win+ctrl+v", "快速设置 › 声音输出", null);
        add("win+ctrl+q", "快速助手", null);
        add("win+ctrl+f", "查找网络上的计算机", "需要域环境");
        add("win+shift+a", "聚焦到 Windows 提示", null);
        add("win+y", "在 Windows Mixed Reality 和桌面之间切换输入", "需要混合现实设备");
        add("ctrl+esc", "开始菜单", null);
        add("ctrl+shift+esc", "任务管理器", null);

        group("窗口");
        add("alt+tab", "切换窗口", null);
        add("ctrl+alt+tab", "切换窗口(松开按键后界面保持打开)", null);
        add("alt+esc", "按打开顺序循环切换窗口", null);
        add("alt+f4", "关闭当前窗口(在桌面上是关机菜单)", null);
        add("alt+space", "窗口菜单(还原 / 移动 / 大小 / 关闭)", null);
        add("win+tab", "任务视图", null);
        add("win+d", "显示 / 隐藏桌面", null);
        add("win+m", "最小化所有窗口", null);
        add("win+shift+m", "还原最小化的窗口", null);
        add("win+home", "最小化 / 还原当前窗口以外的所有窗口", null);
        add("win+comma", "临时查看桌面", null);
        add("win+up", "最大化窗口", null);
        add("win+down", "还原 / 最小化窗口", null);
        add("win+left", "窗口贴靠到屏幕左侧", null);
        add("win+right", "窗口贴靠到屏幕右侧", null);
        add("win+alt+up", "窗口贴靠到屏幕上半部分", null);
        add("win+alt+down", "窗口贴靠到屏幕下半部分", null);
        add("win+shift+up", "窗口纵向拉伸到屏幕顶部和底部", null);
        add("win+shift+down", "还原纵向拉伸 / 最大化的窗口", null);
        add("win+shift+left", "把窗口移到左边的显示器", null);
        add("win+shift+right", "把窗口移到右边的显示器", null);
        add("win+z", "贴靠布局", null);
        add("win+shift+enter", "UWP 应用切换全屏", null);

        group("虚拟桌面");
        add("win+ctrl+d", "新建虚拟桌面", null);
        add("win+ctrl+left", "切换到左边的虚拟桌面", null);
        add("win+ctrl+right", "切换到右边的虚拟桌面", null);
        add("win+ctrl+f4", "关闭当前虚拟桌面", null);

        group("任务栏");
        add("win+t", "依次切换任务栏上的程序", null);
        add("win+b", "聚焦到任务栏通知区域", null);
        for (var i = 1; i <= 10; i++)
        {
            var key = (i % 10).ToString();
            add($"win+{key}", $"打开 / 切换到任务栏第 {i} 个程序", null);
            add($"win+shift+{key}", $"新开一个任务栏第 {i} 个程序", null);
            add($"win+ctrl+{key}", $"切换到任务栏第 {i} 个程序上次使用的窗口", null);
            add($"win+alt+{key}", $"打开任务栏第 {i} 个程序的跳转列表", null);
            add($"win+ctrl+shift+{key}", $"以管理员身份新开任务栏第 {i} 个程序", null);
        }

        group("截图与录屏");
        add("printscreen", "截图(框选区域,复制到剪贴板)", "需要在设置里打开\"用 PrtScn 键打开截图\",新系统默认开");
        add("alt+printscreen", "截取当前窗口到剪贴板", null);
        add("win+printscreen", "截全屏并保存到\"图片\\屏幕截图\"", null);
        add("win+shift+s", "截图工具(框选截图)", null);
        add("win+shift+r", "截图工具(框选录屏)", "较新版本的截图工具");

        group("游戏栏");
        add("win+g", "打开游戏栏", null);
        add("win+alt+r", "开始 / 停止录制(游戏栏)", null);
        add("win+alt+g", "录制刚才那段时间(游戏栏)", null);
        add("win+alt+printscreen", "游戏截图(游戏栏)", null);
        add("win+alt+t", "显示 / 隐藏录制计时器(游戏栏)", null);
        add("win+alt+m", "录制时麦克风开 / 关(游戏栏)", null);
        add("win+alt+b", "打开 / 关闭 HDR", null);

        group("输入法与文字");
        add("win+space", "切换输入法(向后)", null);
        add("win+shift+space", "切换输入法(向前)", null);
        add("win+ctrl+space", "切换回上一个输入法", null);
        add("ctrl+space", "中文输入法开 / 关", "被占用后仍可用 Shift 切换中英文");
        add("ctrl+shift+f", "微软拼音:简体 / 繁体切换", "只在微软拼音输入法激活时");
        add("ctrl+period", "微软拼音:中 / 英文标点切换", "只在微软拼音输入法激活时");
        add("shift+space", "微软拼音:全角 / 半角切换", "只在微软拼音输入法激活时");
        add("win+period", "表情符号面板", null);
        add("win+semicolon", "表情符号面板", null);
        add("win+slash", "输入法重新转换", null);
        add("win+h", "语音输入", null);
        add("win+alt+h", "语音输入打开时,把焦点移到键盘", null);

        group("辅助功能");
        add("win+equals", "放大镜:放大", null);
        add("win+numadd", "放大镜:放大", null);
        add("win+minus", "放大镜:缩小", null);
        add("win+numsubtract", "放大镜:缩小", null);
        add("win+esc", "关闭放大镜", null);
        add("win+ctrl+enter", "讲述人", null);
        add("win+ctrl+n", "讲述人设置", null);
        add("win+ctrl+o", "屏幕键盘", null);
        add("win+ctrl+c", "颜色滤镜开 / 关", "需要先在设置里允许快捷键");
        add("win+ctrl+s", "Windows 语音识别", "旧组件,新系统可能没有");

        group("系统保留");
        reserved("ctrl+alt+delete", "安全选项(锁定 / 切换用户 / 注销 / 任务管理器)", "安全注意序列,任何程序都拦不住");
        reserved("win+l", "锁定电脑", "由 winlogon 处理,键盘钩子拦不住;只能用组策略连锁屏功能一起禁用,WAIO 不这么做");
        reserved("win+ctrl+shift+b", "唤醒屏幕 / 重置显卡驱动", "黑屏时的救急键,WAIO 不接管");
    }
}
