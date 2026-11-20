namespace Waio.Feature.Translate.Domain;

/// <summary>屏幕矩形(物理像素,左上角为原点,y 向下)。</summary>
internal readonly record struct Box(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public int CenterX => (Left + Right) / 2;

    public int CenterY => (Top + Bottom) / 2;
}

/// <summary>窗口是怎么打开的(MAIO <c>TranslateWindowController.Trigger</c>)。</summary>
/// <param name="Selection">翻译选中文字时为 true;<paramref name="SelectionRect"/> 是选中文字(光标)的位置,拿不到为 null。</param>
internal readonly record struct ShowTrigger(bool Selection, Box? SelectionRect = null)
{
    public static ShowTrigger Input { get; } = new(false);
}

internal enum PlacementKind
{
    Center,
    Mouse,
    Remembered,

    /// <summary>选中文字的下方,放不下时放上方。</summary>
    Below,
}

/// <summary>
/// 翻译窗口放哪(MAIO <c>TranslateWindowController.placement/position</c>,规则一致,换成 Windows 坐标:y 向下)。
/// </summary>
internal static class WindowPlacement
{
    /// <summary>MAIO:按展开后的高度预留空间,至少 420(DIP)。</summary>
    public const double ReserveDip = 420;

    public const int MarginDip = 8;

    public static PlacementKind Kind(ShowTrigger trigger, TranslateSettings settings) => trigger.Selection
        ? settings.SelectionWindowPosition switch
        {
            SelectionPosition.Center => PlacementKind.Center,
            SelectionPosition.Mouse => PlacementKind.Mouse,
            _ => trigger.SelectionRect is null ? PlacementKind.Mouse : PlacementKind.Below,
        }
        : settings.WindowPosition switch
        {
            WindowPosition.Center => PlacementKind.Center,
            WindowPosition.FollowMouse => PlacementKind.Mouse,
            _ => PlacementKind.Remembered,
        };

    /// <summary>
    /// 窗口左上角。<paramref name="width"/>/<paramref name="height"/> 是当前大小,<paramref name="reserve"/> 是为结果预留的高度,
    /// <paramref name="margin"/> 是离屏幕边缘的距离(都是物理像素)。保证整个窗口在工作区内。
    /// </summary>
    public static (int X, int Y) TopLeft(
        PlacementKind kind, Box workArea, int width, int height, int reserve, int margin,
        (int X, int Y) mouse, Box? selection, IReadOnlyList<int>? remembered)
    {
        reserve = Math.Min(workArea.Height, Math.Max(height, reserve));
        (int X, int Y) point;
        switch (kind)
        {
            case PlacementKind.Center:
                return (workArea.CenterX - width / 2, Math.Max(workArea.Top, workArea.CenterY - height / 2));
            case PlacementKind.Mouse:
                point = (mouse.X - 30, mouse.Y + 12);
                break;
            case PlacementKind.Remembered:
                point = remembered is { Count: 2 }
                    ? (remembered[0], remembered[1])
                    : (workArea.CenterX - width / 2, workArea.Top + workArea.Height / 5);
                break;
            default:
                var rect = selection!.Value;
                var fitsBelow = rect.Bottom + margin + reserve <= workArea.Bottom;
                point = (rect.Left, fitsBelow ? rect.Bottom + margin : rect.Top - margin - reserve);
                break;
        }

        var x = Math.Min(Math.Max(point.X, workArea.Left + margin), workArea.Right - width - margin);
        var y = Math.Min(Math.Max(point.Y, workArea.Top + margin), workArea.Bottom - reserve);
        return (x, y);
    }

    /// <summary>
    /// 内容变高后的新位置(MAIO <c>didResize</c>):"屏幕正中间"模式保持中心不动(上下同时扩展,不超出工作区);
    /// 其他模式保持左上角不动,但不让窗口底部超出工作区。
    /// </summary>
    public static (int X, int Y) AfterResize((int X, int Y) topLeft, (int X, int Y)? center, int width, int height, Box workArea)
    {
        if (center is { } c)
        {
            var y = Math.Min(Math.Max(c.Y - height / 2, workArea.Top), workArea.Bottom - height);
            return (c.X - width / 2, Math.Max(workArea.Top, y));
        }

        return (topLeft.X, Math.Max(workArea.Top, Math.Min(topLeft.Y, workArea.Bottom - height)));
    }
}
