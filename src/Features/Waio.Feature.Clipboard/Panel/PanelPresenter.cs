using Microsoft.Extensions.Logging;
using Waio.Feature.Clipboard.Domain;
using Waio.Kit.Input;
using Waio.Kit.Services;

namespace Waio.Feature.Clipboard.Panel;

/// <summary>
/// 面板的全部交互流程(参考 MAIO 的 Picker):显示/隐藏、搜索、选择、预览、固定、删除、粘贴回原窗口。
/// 视图和系统相关的部分都在接口后面,所以整套流程可以脱离真实窗口单测。
/// <para>
/// 线程约定:公开方法由 UI 线程调用;数据库工作用 <c>Task.Run</c> 挪到后台,结果回来后在原上下文
/// (UI 线程的同步上下文)更新视图。较新的加载请求会让较旧的结果作废(<see cref="_version"/>)。
/// </para>
/// </summary>
internal sealed class PanelPresenter(
    ClipHistory history,
    Func<ClipboardSettings> settings,
    IClipboardService clipboard,
    IInputService input,
    IOverlayService overlay,
    IPanelView view,
    IWindowFocus focus,
    IPlacementProvider placement,
    ILogger logger)
{
    private static readonly KeyChord PasteChord = KeyChord.Parse("ctrl+v");

    private readonly PickerState _state = new();
    private string _query = "";
    private bool _searchFocused;
    private bool _composing;
    private long? _previewId;
    private nint _target;
    private int _version;

    public PickerState State => _state;

    public bool IsVisible => view.IsVisible;

    /// <summary>正在预览的条目;没有预览为 null。</summary>
    public long? PreviewingId => _previewId;

    /// <summary>弹出面板。先加载好列表再显示,避免先闪一个空窗口。</summary>
    public async Task ShowAsync()
    {
        var options = settings();
        _target = focus.Foreground();
        // 取位置和加载列表同时进行;位置用的是弹出前记下的原窗口
        var locating = placement.LocateAsync(_target, options);
        _query = "";
        _previewId = null;
        _composing = false;
        _searchFocused = options.SearchMode == SearchMode.TypeToSearch; // 直接打字模式下搜索框一直有焦点

        view.SetQuery("");
        view.ClosePreview();
        await ReloadAsync(keepSelection: false);
        view.Show(await locating, focusSearch: _searchFocused);
    }

    public void Hide()
    {
        _previewId = null;
        view.Hide();
    }

    /// <summary>
    /// 快捷键再按一次关闭(MAIO:已打开时再按一次关闭)。只有面板确实在前台时才算"已打开";
    /// 显示着却没被激活(被别的程序抢了前台)时重新弹出——否则用户得按两次才看得到面板。
    /// </summary>
    public Task ToggleAsync()
    {
        if (view.IsVisible && view.IsActive)
        {
            Hide();
            return Task.CompletedTask;
        }

        if (view.IsVisible)
        {
            logger.LogInformation("剪贴板面板显示着但不在前台,重新弹出");
            view.Hide();
        }

        return ShowAsync();
    }

    /// <summary>剪贴板有新内容:面板开着就刷新列表(尽量保持选中项)。</summary>
    public Task NotifyHistoryChangedAsync() => view.IsVisible ? ReloadAsync(keepSelection: true) : Task.CompletedTask;

    public void SetComposing(bool composing) => _composing = composing;

    public void SetSearchFocused(bool focused) => _searchFocused = focused;

    public Task OnQueryChangedAsync(string text)
    {
        if (text == _query)
        {
            return Task.CompletedTask;
        }

        _query = text;
        return ReloadAsync(keepSelection: false);
    }

    public PanelAction Resolve(PanelKeyInput key) => PanelKeys.Resolve(key, Context());

    public PanelAction ResolveKeyUp(PanelKeyInput key) => PanelKeys.ResolveKeyUp(key, Context());

    /// <summary>列表缩略图用:后台读出图片数据(DIB)。</summary>
    public Task<byte[]?> LoadImageAsync(long id) => Task.Run(() => history.Image(id));

    public async Task ExecuteAsync(PanelAction action)
    {
        switch (action.Kind)
        {
            case PanelActionKind.MoveUp:
                await MoveAsync(-1);
                break;
            case PanelActionKind.MoveDown:
                await MoveAsync(1);
                break;
            case PanelActionKind.EnterList:
                _searchFocused = false;
                _state.Select(0);
                view.Render(_state);
                view.FocusList();
                break;
            case PanelActionKind.OpenPreview when _previewId is null && _state.Selected is { } item:
                await PreviewAsync(item);
                break;
            case PanelActionKind.Paste when _state.Selected is { } item:
                await PasteAsync(item, plain: false);
                break;
            case PanelActionKind.PastePlain when _state.Selected is { } item:
                await PasteAsync(item, plain: true);
                break;
            case PanelActionKind.QuickPaste when _state.QuickItem(action.Slot) is { } item:
                await PasteAsync(item, plain: false);
                break;
            case PanelActionKind.TogglePreview:
                await TogglePreviewAsync();
                break;
            case PanelActionKind.ClosePreview:
                ClosePreview();
                break;
            case PanelActionKind.Delete when _state.Selected is { } item:
                await DeleteAsync(item.Id);
                break;
            case PanelActionKind.TogglePin when _state.Selected is { } item:
                await SetPinnedAsync(item.Id, !item.IsPinned);
                break;
            case PanelActionKind.StartSearch:
                _searchFocused = true;
                view.FocusSearch();
                break;
            case PanelActionKind.EndSearch:
                _searchFocused = false;
                view.FocusList();
                break;
            case PanelActionKind.ClearSearch:
                await ClearSearchAsync();
                break;
            case PanelActionKind.Close:
                Hide();
                break;
        }
    }

    // ---- 单条操作(键盘和右键菜单共用) ----

    public async Task PasteAsync(ClipItem item, bool plain)
    {
        var options = settings();
        view.Hide();
        _previewId = null;
        try
        {
            var content = await Task.Run(() => history.Content(item.Id));
            if (content is null)
            {
                await overlay.ShowHudAsync("这条内容已经丢失,无法粘贴");
                return;
            }

            if (plain)
            {
                content = ToPlainText(content);
                if (content.IsEmpty)
                {
                    await overlay.ShowHudAsync("这条内容没有文字,无法粘贴为纯文本");
                    return;
                }
            }

            if (!await clipboard.SetAsync(content))
            {
                await overlay.ShowHudAsync("剪贴板被其他程序占用,粘贴失败");
                return;
            }

            if (options.MoveToTopOnPaste)
            {
                await Task.Run(() => history.MarkUsed(item.Id));
            }

            if (!options.PasteDirectly)
            {
                await overlay.ShowHudAsync("已复制到剪贴板");
                return;
            }

            // 先把焦点还给原窗口并确认它真的在前台,再发 Ctrl+V;否则按键会落到别处(MAIO 同样的做法)
            if (_target == 0 || !await focus.RestoreAsync(_target))
            {
                await overlay.ShowHudAsync("已复制到剪贴板(无法切回原窗口,请手动粘贴)");
                return;
            }

            input.SendKeys(PasteChord);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "粘贴剪贴板条目 {Id} 失败", item.Id);
            await overlay.ShowHudAsync("粘贴失败,详见日志");
        }
    }

    /// <summary>只放进剪贴板,不粘贴(右键菜单"复制到剪贴板")。</summary>
    public async Task CopyOnlyAsync(ClipItem item)
    {
        var content = await Task.Run(() => history.Content(item.Id));
        if (content is not null && await clipboard.SetAsync(content))
        {
            await overlay.ShowHudAsync("已复制到剪贴板");
        }
        else
        {
            await overlay.ShowHudAsync("复制失败");
        }
    }

    public async Task DeleteAsync(long id)
    {
        await Task.Run(() => history.Delete(id));
        _state.Remove(id);
        if (_previewId == id)
        {
            ClosePreview();
        }

        view.Render(_state);
    }

    public async Task SetPinnedAsync(long id, bool pinned)
    {
        await Task.Run(() => history.SetPinned(id, pinned));
        await ReloadAsync(keepSelection: true);
    }

    public async Task PreviewAsync(ClipItem item)
    {
        _previewId = item.Id;
        view.ShowPreview(item, null);
        var content = await Task.Run(() => history.Content(item.Id));
        if (_previewId == item.Id)
        {
            view.ShowPreview(item, content);
        }
    }

    // ---- 内部 ----

    private PanelContext Context()
    {
        var options = settings();
        return new PanelContext(
            options.SearchMode, _searchFocused, _composing, _previewId is not null, _query.Length == 0, options.PreviewMode);
    }

    /// <summary>移动选中;预览开着时预览跟着换成新选中的条目。</summary>
    private async Task MoveAsync(int offset)
    {
        _state.Move(offset);
        view.Render(_state);
        if (_previewId is not null && _state.Selected is { } item && item.Id != _previewId)
        {
            await PreviewAsync(item);
        }
    }

    private Task TogglePreviewAsync()
    {
        if (_previewId is not null)
        {
            ClosePreview();
            return Task.CompletedTask;
        }

        return _state.Selected is { } item ? PreviewAsync(item) : Task.CompletedTask;
    }

    private void ClosePreview()
    {
        _previewId = null;
        view.ClosePreview();
    }

    private async Task ClearSearchAsync()
    {
        _query = "";
        _searchFocused = false;
        view.SetQuery("");
        view.FocusList();
        await ReloadAsync(keepSelection: false);
    }

    private async Task ReloadAsync(bool keepSelection)
    {
        var version = Interlocked.Increment(ref _version);
        var query = _query;
        var items = await Task.Run(() => history.Load(query));
        if (version != _version)
        {
            return; // 期间又发起了更新的加载,这次的结果已过时
        }

        _state.SetItems(items, keepSelection);
        view.Render(_state);
    }

    /// <summary>纯文本粘贴:文件列表退化成换行分隔的路径文本。</summary>
    private static ClipboardContent ToPlainText(ClipboardContent content) =>
        content.Files.Count > 0 && string.IsNullOrEmpty(content.Text)
            ? ClipboardContent.FromText(string.Join("\r\n", content.Files))
            : content.AsPlainText();
}
