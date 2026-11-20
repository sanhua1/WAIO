using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Waio.Feature.Clipboard.Infrastructure;
using Windows.Storage.Streams;

namespace Waio.Feature.Clipboard.Views;

/// <summary>
/// 图片:列表缩略图(行进入可视区域时才加载,解码成小图并缓存)和预览大图。
/// 图片数据可能有几十 MB(DIB 不压缩),所以读库和转换放后台,解码时直接按目标尺寸缩小。
/// </summary>
internal sealed partial class ClipboardPanelWindow
{
    /// <summary>缩略图解码高度(逻辑像素,和行里的图标框一致;按显示器缩放自动换算)。</summary>
    private const int ThumbnailDip = 28;

    /// <summary>缓存上限:超过就整个清掉重来,防止长时间使用后占用越来越多内存。</summary>
    private const int ThumbnailCacheLimit = 200;

    private readonly Dictionary<long, ImageSource> _thumbnails = [];
    private readonly HashSet<long> _loadingThumbnails = [];

    private void ApplyCachedThumbnail(ClipRow row)
    {
        if (row.HasImage && _thumbnails.TryGetValue(row.Item.Id, out var cached))
        {
            row.Thumbnail = cached;
        }
    }

    // 列表虚拟化:只有真正要显示的行才会走到这里,滚到哪加载到哪
    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Index >= 0 && args.Index < Rows.Count && Rows[args.Index] is { HasImage: true, Thumbnail: null } row)
        {
            _ = LoadThumbnailAsync(row.Item.Id);
        }
    }

    private async Task LoadThumbnailAsync(long id)
    {
        if (_presenter is null || !_loadingThumbnails.Add(id))
        {
            return;
        }

        try
        {
            var dib = await _presenter.LoadImageAsync(id);
            var bmp = dib is null ? null : await Task.Run(() => DibImage.ToBmp(dib));
            if (bmp is null)
            {
                return; // 没有图片数据或格式不支持:保留类型图标
            }

            var image = await DecodeAsync(bmp, ThumbnailDip);
            if (_thumbnails.Count >= ThumbnailCacheLimit)
            {
                _thumbnails.Clear();
            }

            _thumbnails[id] = image;

            // 加载期间列表可能已重建(新的行对象),按 id 找当前的行
            foreach (var row in Rows)
            {
                if (row.Item.Id == id)
                {
                    row.Thumbnail = image;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载剪贴板缩略图失败(条目 {Id})", id);
        }
        finally
        {
            _loadingThumbnails.Remove(id);
        }
    }

    private async Task ShowPreviewImageAsync(long id, byte[] dib)
    {
        try
        {
            var bmp = await Task.Run(() => DibImage.ToBmp(dib));
            if (bmp is null)
            {
                StatusText.Text += "(无法显示这种图片格式)";
                return;
            }

            var image = await DecodeAsync(bmp, decodeHeightDip: 0);
            if (_presenter?.PreviewingId != id)
            {
                return; // 解码期间预览已关闭或换了条目
            }

            PreviewImage.Source = image;
            PreviewImageFrame.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "显示图片预览失败");
        }
    }

    /// <param name="decodeHeightDip">按这个高度解码(省内存);0 = 原尺寸。</param>
    private static async Task<BitmapImage> DecodeAsync(byte[] bmp, int decodeHeightDip)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bmp.AsBuffer());
        stream.Seek(0);
        var image = new BitmapImage();
        if (decodeHeightDip > 0)
        {
            image.DecodePixelType = DecodePixelType.Logical;
            image.DecodePixelHeight = decodeHeightDip;
        }

        await image.SetSourceAsync(stream);
        return image;
    }
}
