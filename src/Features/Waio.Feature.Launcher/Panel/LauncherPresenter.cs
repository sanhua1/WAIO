using Microsoft.Extensions.Logging;
using Waio.Feature.Launcher.Domain;
using Waio.Kit.Services;
using Waio.Kit.Commands;

namespace Waio.Feature.Launcher.Panel;

/// <summary>
/// 面板的全部交互流程(对应 MAIO 的 LauncherController):显示 / 关闭、输入、选择、打开、复制、切换来源。
/// 打开应用 / 网页都经 <see cref="ILauncher"/>(默认降权,ADR-0006)。视图和系统相关的部分都在接口后面,可以脱离真实窗口单测。
/// <para>
/// 焦点:弹出前记下原来的前台窗口。Esc / 再按快捷键关闭时把焦点还给它;打开了某一项时不还(焦点应当交给被打开的程序),
/// 并在面板还是前台时先"放行前台",否则 Windows 的前台锁会让新程序的窗口躲在后面闪任务栏。
/// </para>
/// 公开方法都在 UI 线程调用。
/// </summary>
internal sealed class LauncherPresenter(
    LauncherState state,
    Func<LauncherSettings> settings,
    ILauncherView view,
    IPanelSystem system,
    ILauncher launcher,
    IClipboardService clipboard,
    IOverlayService overlay,
    Action<UsageBook> saveUsage,
    ILogger logger,
    IFileSearch? files = null,
    Func<Task>? openSettings = null)
{
    private nint _previous;
    private DateTimeOffset? _closedAt;
    private CancellationTokenSource? _fileSearch;
    private (string Text, bool FoldersOnly, int Limit) _requested;

    public LauncherState State => state;

    public bool IsVisible => view.IsVisible;

    /// <summary>
    /// 快捷键:没打开就打开;打开着并且在前台就关闭。显示着却不在前台(被别的程序抢了前台)时重新弹出,
    /// 否则用户得按两次才看得到面板(剪贴板面板踩过的坑)。
    /// </summary>
    public async Task ToggleAsync(LauncherSource source)
    {
        if (view.IsVisible && view.IsActive)
        {
            await CloseAsync(restoreFocus: true);
            return;
        }

        if (view.IsVisible)
        {
            logger.LogInformation("启动台显示着但不在前台,重新弹出");
            view.Hide();
        }

        Show(source);
    }

    public void Show(LauncherSource source)
    {
        _previous = system.Foreground();
        var current = settings();
        state.Settings = current;
        var keep = source == LauncherSource.All && _closedAt is { } closed
            && (state.Clock() - closed).TotalSeconds <= current.KeepQuerySeconds;
        state.PrepareForOpen(keep, source);
        view.SetQuery(state.Query);
        Refresh();
        view.Show(system.Locate() with { WidthPercent = current.PanelWidthPercent, HeightPercent = current.PanelHeightPercent });
    }

    /// <summary>鼠标单击某一行:只选中(预览跟着变),键盘焦点留在搜索框,↑↓ 接着从这一项往下走。双击才打开。</summary>
    public void SelectAt(int index)
    {
        state.Select(index);
        view.Render(state);
    }

    /// <summary>关闭面板。<paramref name="restoreFocus"/>:把焦点还给弹出前的窗口(点到别处关闭时不需要)。</summary>
    public async Task CloseAsync(bool restoreFocus)
    {
        if (!view.IsVisible)
        {
            return;
        }

        _closedAt = state.Clock();
        view.Hide();
        if (restoreFocus && _previous != 0)
        {
            await system.RestoreAsync(_previous);
        }
    }

    /// <summary>应用列表更新了:面板开着就刷新。</summary>
    public void SetApps(IReadOnlyList<LauncherItem> apps)
    {
        state.SetApps(apps);
        if (view.IsVisible)
        {
            Refresh();
        }
    }

    /// <summary>设置变了(引擎、条数、最近使用)。</summary>
    public void ApplySettings(LauncherSettings value)
    {
        state.Settings = value;
        state.Refresh();
        if (view.IsVisible)
        {
            Refresh();
        }
    }

    public void OnQueryChanged(string text)
    {
        if (text == state.Query)
        {
            return;
        }

        state.SetQuery(text);
        Refresh();
    }

    /// <summary>点了来源标签。输入里写了前缀时先去掉前缀(和 Tab 切换一样)。</summary>
    public Task SelectSourceAsync(LauncherSource source)
    {
        if (state.HasPrefix)
        {
            var text = state.Parsed.Text;
            state.SetQuery(text);
            view.SetQuery(text);
        }

        state.SetSource(source);
        Refresh();
        return Task.CompletedTask;
    }

    public LauncherAction Resolve(LauncherKeyInput input, LauncherKeyContext context) => LauncherKeys.Resolve(input, context);

    public async Task ExecuteAsync(LauncherAction action)
    {
        switch (action.Kind)
        {
            case LauncherActionKind.MoveUp:
                state.Move(-1);
                Refresh();
                break;
            case LauncherActionKind.MoveDown:
                state.Move(1);
                Refresh();
                break;
            case LauncherActionKind.Open or LauncherActionKind.Reveal:
                await WaitForFilesAsync();
                if (state.Selected is { } selected)
                {
                    await OpenAsync(selected, reveal: action.Kind == LauncherActionKind.Reveal);
                }

                break;
            case LauncherActionKind.QuickOpen when await WaitForFilesAsync() && state.QuickItem(action.Slot) is { } item:
                state.Select(action.Slot - 1);
                await OpenAsync(item, reveal: false);
                break;
            case LauncherActionKind.Copy when state.Selected is { } item:
                await CopyAsync(item);
                break;
            case LauncherActionKind.NextSource or LauncherActionKind.PreviousSource:
                view.SetQuery(state.CycleSource(forward: action.Kind == LauncherActionKind.NextSource));
                Refresh();
                break;
            case LauncherActionKind.Escape:
                if (state.Escape())
                {
                    await CloseAsync(restoreFocus: true);
                }
                else
                {
                    view.SetQuery(state.Query);
                    Refresh();
                }

                break;
            case LauncherActionKind.BackspaceOnEmpty:
                if (state.BackspaceOnEmpty())
                {
                    Refresh();
                }

                break;
        }
    }

    /// <summary>
    /// 底栏的齿轮:直接打开 WAIO 设置窗口(ADR-0025)。先关面板、不还焦点(焦点交给设置窗口)。
    /// </summary>
    public async Task OpenSettingsAsync()
    {
        if (openSettings is null)
        {
            return;
        }

        await CloseAsync(restoreFocus: false);
        try
        {
            await openSettings();
        }
        catch (CommandException ex)
        {
            logger.LogWarning("启动台:打开设置失败:{Message}", ex.Message);
            await overlay.ShowHudAsync("WAIO 设置暂时打不开");
        }
    }

    /// <summary>鼠标点了某一行。</summary>
    public Task OpenAtAsync(int index)
    {
        state.Select(index);
        return state.Selected is { } item ? OpenAsync(item, reveal: false) : Task.CompletedTask;
    }

    /// <summary>
    /// 打开(或在资源管理器中显示)一项:记常用度 → 放行前台 → 关面板 → 启动。
    /// 文件已经不在了(卸载了、扫描还没更新)就提示并从列表拿掉,不假装打开。
    /// </summary>
    public async Task OpenAsync(LauncherItem item, bool reveal)
    {
        if (item.Kind == LauncherItemKind.MoreFiles)
        {
            await SelectSourceAsync(LauncherSource.Files); // 输入不变,"文件"标签最多列 300 个
            return;
        }

        if (item.Path is { } path && !File.Exists(path) && !Directory.Exists(path))
        {
            state.Remove(item);
            saveUsage(state.Usage);
            Refresh();
            await overlay.ShowHudAsync($"{item.Title} 已经不在了:{path}");
            return;
        }

        state.Record(item);
        saveUsage(state.Usage);

        var request = reveal && item.Path is { } shown
            ? new LaunchRequest("explorer", $"/select,\"{shown}\"")
            : new LaunchRequest(item.Target);

        system.AllowForegroundHandoff();
        await CloseAsync(restoreFocus: false);
        try
        {
            await launcher.LaunchAsync(request);
        }
        catch (LaunchException ex)
        {
            logger.LogWarning("启动台打开失败:{Title}:{Message}", item.Title, ex.Message);
            if (_previous != 0)
            {
                await system.RestoreAsync(_previous);
            }

            await overlay.ShowHudAsync($"{item.Title}:{ex.Message}");
        }
    }

    /// <summary>
    /// ⏎ 时文件结果还没到(刚打完字就按):最多等 <see cref="FileWait"/>,让文件结果先排进来,
    /// 不然选中的是还没被文件挤下去的那一项。总是返回 true(给 switch 的 when 用)。
    /// </summary>
    private async Task<bool> WaitForFilesAsync()
    {
        if (state.FilesPending && !PendingFileSearch.IsCompleted)
        {
            await Task.WhenAny(PendingFileSearch, Task.Delay(FileWait));
        }

        return true;
    }

    private static readonly TimeSpan FileWait = TimeSpan.FromMilliseconds(300);

    /// <summary>最近一次发起的文件搜索(测试用来等它结束)。</summary>
    internal Task PendingFileSearch { get; private set; } = Task.CompletedTask;

    /// <summary>文件索引的状态说明变了("正在建立文件索引 37%"……)。</summary>
    public void SetFileStatus(string? text)
    {
        state.FileStatus = text;
        if (view.IsVisible)
        {
            view.Render(state);
        }
    }

    /// <summary>状态变了:需要的话发起文件搜索(输入或来源和上次不同才发),再刷新视图。</summary>
    private void Refresh()
    {
        RequestFiles();
        view.Render(state);
    }

    /// <summary>
    /// 文件搜索在后台跑,结果回到 UI 线程再显示(MAIO 的 FileSearchClient:新输入来了旧搜索立即作废)。
    /// 在新结果到达之前,列表里先保留上一次的文件结果,不会闪一下变空。
    /// </summary>
    private void RequestFiles()
    {
        var parsed = state.Parsed;
        if (!parsed.WantsFiles)
        {
            _fileSearch?.Cancel();
            _requested = default;
            return;
        }

        if (files is null)
        {
            state.SetFiles(new FileResults(parsed.Text, [], 0)); // 没有文件搜索:当作"结果已到、为空",网页搜索照常出现
            return;
        }

        // 条数也算在内:"全部"只搜 20 条,切到"文件"(手动或自动)要重新搜 300 条。
        // 以前只比较文字和"只要文件夹",从"全部"切到"文件"不会重搜,"文件"里只剩那 20 条
        var foldersOnly = parsed.Source == LauncherSource.Folders;
        var limit = parsed.Source == LauncherSource.All ? Math.Max(1, state.Settings.MaxFilesInAll) : LauncherRanking.MaxFilesInSource;
        if (_requested == (parsed.Text, foldersOnly, limit))
        {
            return;
        }

        _requested = (parsed.Text, foldersOnly, limit);
        _fileSearch?.Cancel();
        var cancellation = _fileSearch = new CancellationTokenSource();
        PendingFileSearch = SearchFilesAsync(parsed.Text, foldersOnly, limit, cancellation.Token);
    }

    private async Task SearchFilesAsync(string text, bool foldersOnly, int limit, CancellationToken cancellation)
    {
        try
        {
            var results = await Task.Run(() => files!.Search(text, foldersOnly, limit, cancellation), cancellation);
            if (!cancellation.IsCancellationRequested && state.SetFiles(results) && view.IsVisible)
            {
                Refresh(); // 结果可能让"全部"自动切到"文件",那就要接着搜 300 条
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "文件搜索失败:{Text}", text);
            if (state.SetFiles(new FileResults(text, [], 0)) && view.IsVisible)
            {
                view.Render(state); // 失败也要"结算",否则网页搜索一直不出现
            }
        }
    }

    /// <summary>复制所选项的路径(应用)或网址(网页搜索);商店应用没有路径,复制它的打开方式。</summary>
    private async Task CopyAsync(LauncherItem item)
    {
        if (item.Kind == LauncherItemKind.MoreFiles)
        {
            return; // 没有可复制的
        }

        await CopyTextAsync(item.Path ?? item.Target);
    }

    /// <summary>复制一段文字并提示(预览框里单击文件名 / 路径)。空文字不复制。</summary>
    public async Task CopyTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (await clipboard.CopyAsync(ClipboardContent.FromText(text)))
        {
            await overlay.ShowHudAsync($"已复制:{text}", TimeSpan.FromSeconds(1.5));
        }
        else
        {
            await overlay.ShowHudAsync("复制失败:剪贴板被别的程序占用");
        }
    }
}
