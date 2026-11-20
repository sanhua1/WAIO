using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Waio.Feature.Launcher.Domain;
using Waio.Feature.Launcher.Infrastructure;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Waio.Feature.Launcher.UI;

/// <summary>
/// 右侧预览框:上面是完整文件名、完整路径(选中项一变就立即更新),下面是内容——
/// 文本显示开头,图片按预览框大小解码,其他显示系统类型名 + "没有预览"。规则在 <see cref="PreviewRules"/>,见 ADR-0028。
/// </summary>
internal sealed partial class LauncherWindow
{
    /// <summary>选中项停留这么久才读内容:按住 ↓ 快速划过时不去逐个读文件。</summary>
    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(70);

    private const int PreviewIconDip = 64;

    private bool _previewVisible = true;
    private string? _previewKey;
    private int _previewVersion;
    private IRandomAccessStream? _previewStream;

    private void SetPreviewVisible(bool visible)
    {
        _previewVisible = visible;
        PreviewColumn.Width = visible ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        PreviewDividerColumn.Width = new GridLength(visible ? 1 : 0);
        PreviewPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            ClearPreview();
        }
    }

    /// <summary>选中项变了就换预览;同一项(重新搜索后又是它)不重读。</summary>
    private void UpdatePreview(LauncherItem? item)
    {
        var key = item is null ? null : item.Id + "\n" + item.Target;
        if (!_previewVisible || key == _previewKey)
        {
            return;
        }

        _previewKey = key;
        var version = ++_previewVersion;
        ResetPreviewBody();
        PreviewName.Text = item?.Title ?? "";
        PreviewPath.Text = item is null ? "" : PathLine(item);
        if (item is not null)
        {
            Fire(() => LoadPreviewAsync(item, version));
        }
    }

    private void ClearPreview()
    {
        _previewKey = null;
        _previewVersion++;
        PreviewName.Text = "";
        PreviewPath.Text = "";
        ResetPreviewBody();
    }

    private static string PathLine(LauncherItem item) => item.Kind switch
    {
        LauncherItemKind.File or LauncherItemKind.Folder => item.Path ?? item.Target,
        LauncherItemKind.Web => item.Target,
        _ => item.Path ?? item.Subtitle,
    };

    private void ResetPreviewBody()
    {
        PreviewTextScroller.Visibility = Visibility.Collapsed;
        PreviewText.Text = "";
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewNone.Visibility = Visibility.Collapsed;
        PreviewIcon.Source = null;
        PreviewGlyph.Visibility = Visibility.Visible;
        _previewStream?.Dispose();
        _previewStream = null;
    }

    private async Task LoadPreviewAsync(LauncherItem item, int version)
    {
        await Task.Delay(PreviewDelay);
        if (version != _previewVersion)
        {
            return;
        }

        if (item.Kind is not (LauncherItemKind.File or LauncherItemKind.Folder) || item.Path is not { } path)
        {
            if (item.Kind != LauncherItemKind.MoreFiles)
            {
                await ShowNoPreviewAsync(item, item.Kind == LauncherItemKind.Web ? "网页搜索" : "应用", "", version);
            }

            return;
        }

        var isDirectory = item.Kind == LauncherItemKind.Folder;
        var data = await Task.Run(() => FilePreviewLoader.Load(path, isDirectory));
        if (version != _previewVersion)
        {
            return;
        }

        var type = data.Size >= 0 ? $"{data.TypeName} · {PreviewRules.FormatSize(data.Size)}" : data.TypeName;
        switch (data.Kind)
        {
            case PreviewKind.Text:
                PreviewText.Text = data.Text!.Length == 0 ? "(空文件)" : data.Text;
                PreviewTextScroller.ChangeView(null, 0, null, disableAnimation: true);
                PreviewTextScroller.Visibility = Visibility.Visible;
                break;
            case PreviewKind.Image when await ShowImageAsync(path, version):
                break;
            case PreviewKind.Image:
                await ShowNoPreviewAsync(item, type, "无法预览这张图片", version);
                break;
            default:
                await ShowNoPreviewAsync(item, type, "没有预览", version);
                break;
        }
    }

    /// <summary>
    /// 图片:先读尺寸,按预览框的物理像素大小解码(4K 屏上清楚,大图不占内存);小图不放大(按原始大小显示)。
    /// 整个文件读进内存再解码:不占着文件,GIF 动画也能播放。
    /// </summary>
    private async Task<bool> ShowImageAsync(string path, int version)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            if (version != _previewVersion)
            {
                return true;
            }

            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            int width = (int)decoder.OrientedPixelWidth, height = (int)decoder.OrientedPixelHeight;
            var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
            var fit = Math.Min(1.0, Math.Min(PreviewBody.ActualWidth * scale / width, PreviewBody.ActualHeight * scale / height));

            stream.Seek(0);
            var image = new BitmapImage();
            if (fit is > 0 and < 1)
            {
                image.DecodePixelWidth = Math.Max(1, (int)Math.Round(width * fit));
            }

            await image.SetSourceAsync(stream);
            if (version != _previewVersion)
            {
                stream.Dispose();
                return true;
            }

            _previewStream = stream;
            PreviewImage.MaxWidth = width / scale;
            PreviewImage.MaxHeight = height / scale;
            PreviewImage.Source = image;
            PreviewImage.Visibility = Visibility.Visible;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            _logger.LogDebug(ex, "启动台预览图片失败:{Path}", path);
            return false;
        }
    }

    private async Task ShowNoPreviewAsync(LauncherItem item, string type, string note, int version)
    {
        PreviewGlyph.Glyph = LauncherRow.GlyphFor(item.Kind);
        PreviewType.Text = type;
        PreviewNoneText.Text = note;
        PreviewNoneText.Visibility = note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PreviewNone.Visibility = Visibility.Visible;
        if (!item.HasShellIcon)
        {
            return;
        }

        var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
        var pixels = await _loadIcon(item.Target, (int)Math.Round(PreviewIconDip * scale));
        if (pixels is not null && version == _previewVersion)
        {
            PreviewIcon.Source = await ToBitmapAsync(pixels);
            PreviewGlyph.Visibility = Visibility.Collapsed;
        }
    }

    private static async Task<WriteableBitmap> ToBitmapAsync(IconPixels pixels)
    {
        var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            await stream.WriteAsync(pixels.Bgra);
        }

        bitmap.Invalidate();
        return bitmap;
    }
}
