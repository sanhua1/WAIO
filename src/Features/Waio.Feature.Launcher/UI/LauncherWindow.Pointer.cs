using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Waio.Feature.Launcher.Domain;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Waio.Feature.Launcher.UI;

/// <summary>
/// 鼠标:单击 = 选中(预览跟着变,键盘焦点留在搜索框,↑↓ 从这一项接着走);双击 = 打开;
/// 按住文件 / 文件夹拖出去 = 拖文件本身(进文件夹、聊天软件的发送框),见 <see cref="OnRowDragStarting"/>。
/// </summary>
internal sealed partial class LauncherWindow
{
    /// <summary>
    /// 行对应的数据。必须用 Tag(x:Bind 直接给的):踩坑(单击 / 拖动"没反应"),
    /// 这里的 ItemsRepeater 不给行设 DataContext,以前按 DataContext 取永远是 null,点击事件到了也什么都不做。
    /// </summary>
    private static LauncherRow? RowOf(object sender) =>
        sender is FrameworkElement element ? element.Tag as LauncherRow ?? element.DataContext as LauncherRow : null;

    private void OnRowPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_presenter is null || RowOf(sender) is not { Item: not null } row)
        {
            return;
        }

        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
        {
            return;
        }

        _presenter.SelectAt(row.ItemIndex); // 右键也先选中:菜单针对的就是高亮的这一项
        if (!ReferenceEquals(FocusManager.GetFocusedElement(Content.XamlRoot), SearchBox))
        {
            SearchBox.Focus(FocusState.Programmatic);
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            PrefetchDragItem(row.Item);
        }
    }

    // ---- 拖出 ----

    /// <summary>按下时就去取文件对象,拖动开始时直接用(拖动开始那一刻不能慢慢等)。</summary>
    private (string Path, Task<IStorageItem> Item)? _dragItem;

    private void PrefetchDragItem(LauncherItem item)
    {
        if (item is not { Kind: LauncherItemKind.File or LauncherItemKind.Folder, Path: { } path } || _dragItem?.Path == path)
        {
            return;
        }

        _dragItem = (path, LoadStorageItemAsync(path, item.Kind == LauncherItemKind.Folder));
    }

    private static async Task<IStorageItem> LoadStorageItemAsync(string path, bool folder) =>
        folder ? await StorageFolder.GetFolderFromPathAsync(path) : await StorageFile.GetFileFromPathAsync(path);

    private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RowOf(sender) is { Item: not null } row && _presenter is not null)
        {
            e.Handled = true;
            Fire(() => _presenter.OpenAtAsync(row.ItemIndex));
        }
    }

    /// <summary>
    /// 拖出文件 / 文件夹:用 WinUI 自己的拖放(<c>CanDrag</c>),数据是文件对象(<c>SetStorageItems</c>),
    /// 系统转换成资源管理器、聊天软件认识的格式(CF_HDROP 等)。允许复制 / 移动 / 快捷方式,由接收方和修饰键决定。
    /// <para>
    /// 踩坑:先前用 Win32 的 SHDoDragDrop,系统默认拖放源按 UI 线程消息队列里的按键状态判断,
    /// WinUI 3 的鼠标输入不走那个队列,拖放一开始就被当成"已放下"(日志 80 ms 结束);
    /// 自己读物理按键状态又违反"只有 Core/Input 能碰按键 API"的架构规则。WinUI 的拖放和它的输入是一套,没有这个问题。
    /// </para>
    /// </summary>
    private async void OnRowDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (RowOf(sender) is not { Item: { Kind: LauncherItemKind.File or LauncherItemKind.Folder, Path: { } path } item })
        {
            args.Cancel = true; // 应用、网页搜索不能拖
            return;
        }

        RememberSearchSelection();
        PrefetchDragItem(item);
        var pending = _dragItem!.Value.Item;
        var step = "取文件";
        Microsoft.UI.Xaml.DragOperationDeferral? deferral = null;
        try
        {
            IStorageItem storage;
            if (pending.IsCompletedSuccessfully)
            {
                storage = pending.Result; // 通常按下时就取好了,不用等
            }
            else
            {
                deferral = args.GetDeferral();
                storage = await pending;
            }

            step = "装进数据包";
            args.Data.SetStorageItems([storage], readOnly: false);
            args.Data.Properties.Title = item.Title;
            step = "设置允许的操作";
            args.AllowedOperations = DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link;
            TrySetSystemDragIcon(args);
            _logger.LogInformation("启动台:开始拖动 {Path}(文件{Ready})", path, deferral is null ? "已预先取好" : "现取");
        }
        catch (Exception ex)
        {
            _dragItem = null; // 下次重新取
            _logger.LogWarning(
                "启动台拖动失败({Step}):{Path}:{Type} 0x{HResult:X8} {Detail}",
                step, path, ex.GetType().Name, ex.HResult, ex.ToString());
            args.Cancel = true;
        }
        finally
        {
            deferral?.Complete();
        }
    }

    // "每次尝试拖动,输入框的文字都会被全选"——拖动开始时搜索框失去焦点,
    // 结束后 WinUI 把焦点还回来并全选。拖动前记下光标 / 选区,焦点回来后恢复。
    private (int Start, int Length)? _searchSelection;

    private void RememberSearchSelection()
    {
        _searchSelection = (SearchBox.SelectionStart, SearchBox.SelectionLength);
        SearchBox.GotFocus -= RestoreSearchSelection;
        SearchBox.GotFocus += RestoreSearchSelection;
    }

    private void RestoreSearchSelection(object sender, RoutedEventArgs e)
    {
        SearchBox.GotFocus -= RestoreSearchSelection;
        if (_searchSelection is { } saved)
        {
            _searchSelection = null;
            // 等 TextBox 自己处理完获得焦点(全选)之后再恢复
            DispatcherQueue.TryEnqueue(() => SearchBox.Select(saved.Start, saved.Length));
        }
    }

    /// <summary>拖动时显示系统的文件图标(和资源管理器一样);不行就用默认的(整行的样子),不影响拖动本身。</summary>
    private void TrySetSystemDragIcon(DragStartingEventArgs args)
    {
        try
        {
            args.DragUI.SetContentFromDataPackage();
        }
        catch (Exception ex)
        {
            _logger.LogInformation("启动台:拖动图标用默认的({Type} 0x{HResult:X8})", ex.GetType().Name, ex.HResult);
        }
    }

    private void OnRowDropCompleted(UIElement sender, DropCompletedEventArgs args) =>
        _logger.LogInformation("启动台:拖动结束,结果 = {Result}", args.DropResult switch
        {
            DataPackageOperation.Copy => "复制",
            DataPackageOperation.Move => "移动",
            DataPackageOperation.Link => "快捷方式",
            _ => "无(对方没有接收,或资源管理器在同一个盘里直接移动了文件——那种情况系统也报告为无)",
        });

    // ---- 预览框:单击文件名 / 路径复制 ----

    private void OnPreviewNameTapped(object sender, TappedRoutedEventArgs e) => CopyPreview(PreviewName.Text, e);

    private void OnPreviewPathTapped(object sender, TappedRoutedEventArgs e) => CopyPreview(PreviewPath.Text, e);

    private void CopyPreview(string text, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_presenter is not null)
        {
            Fire(() => _presenter.CopyTextAsync(text));
        }
    }

    private void OnPreviewCopyPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Border border)
        {
            border.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        }
    }

    private void OnPreviewCopyPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Border border)
        {
            border.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }
}
