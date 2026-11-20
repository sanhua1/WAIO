namespace Waio.Feature.Screenshot.Domain;

/// <summary>
/// 屏幕上的点。<b>全局坐标、物理像素</b>(Windows 虚拟屏幕坐标,原点在主屏左上角)。
/// MAIO 用 pt(macOS 全局坐标统一按 pt);Windows 多显示器可以各自缩放,没有统一的"逻辑坐标",所以这里统一用物理像素,
/// 需要按"多少 pt"换算的距离(手柄容差、拖动阈值、间距)乘上所在显示器的缩放比例。
/// </summary>
public readonly record struct PxPoint(int X, int Y);

/// <summary>矩形(全局物理像素)。包含左、上边,不含右、下边(与 CGRect.contains 一致)。</summary>
public readonly record struct PxRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public int CenterX => X + Width / 2;

    public int CenterY => Y + Height / 2;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PxRect FromEdges(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);

    public bool Contains(PxPoint p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    /// <summary>整个 <paramref name="other"/> 都在里面。</summary>
    public bool Contains(PxRect other) => other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    public bool Intersects(PxRect other) => other.X < Right && X < other.Right && other.Y < Bottom && Y < other.Bottom;

    /// <summary>交集;不相交时返回空矩形(宽高为 0)。</summary>
    public PxRect Intersect(PxRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? new PxRect(left, top, 0, 0) : FromEdges(left, top, right, bottom);
    }

    public PxRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    public override string ToString() => $"({X},{Y} {Width}×{Height})";
}

/// <summary>选区的 8 个手柄(MAIO SelectionHandle)。</summary>
public enum SelectionHandle
{
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}

public static class SelectionHandles
{
    public static readonly SelectionHandle[] All = Enum.GetValues<SelectionHandle>();

    private static readonly SelectionHandle[] Corners =
        [SelectionHandle.TopLeft, SelectionHandle.TopRight, SelectionHandle.BottomRight, SelectionHandle.BottomLeft];

    public static PxPoint Point(this SelectionHandle handle, PxRect rect) => handle switch
    {
        SelectionHandle.TopLeft => new(rect.X, rect.Y),
        SelectionHandle.Top => new(rect.CenterX, rect.Y),
        SelectionHandle.TopRight => new(rect.Right, rect.Y),
        SelectionHandle.Right => new(rect.Right, rect.CenterY),
        SelectionHandle.BottomRight => new(rect.Right, rect.Bottom),
        SelectionHandle.Bottom => new(rect.CenterX, rect.Bottom),
        SelectionHandle.BottomLeft => new(rect.X, rect.Bottom),
        _ => new(rect.X, rect.CenterY),
    };

    public static bool MovesLeft(this SelectionHandle h) => h is SelectionHandle.TopLeft or SelectionHandle.Left or SelectionHandle.BottomLeft;

    public static bool MovesRight(this SelectionHandle h) => h is SelectionHandle.TopRight or SelectionHandle.Right or SelectionHandle.BottomRight;

    public static bool MovesTop(this SelectionHandle h) => h is SelectionHandle.TopLeft or SelectionHandle.Top or SelectionHandle.TopRight;

    public static bool MovesBottom(this SelectionHandle h) => h is SelectionHandle.BottomLeft or SelectionHandle.Bottom or SelectionHandle.BottomRight;

    /// <summary>选区太小时只判定四个角,避免手柄互相重叠(MAIO)。</summary>
    internal static IReadOnlyList<SelectionHandle> Candidates(PxRect rect, int tolerance) =>
        rect.Width < tolerance * 4 || rect.Height < tolerance * 4 ? Corners : All;
}

/// <summary>选区的几何运算(MAIO SelectionGeometry):手柄判定、调整大小、移动、限制范围。全部是物理像素,天然按像素对齐。</summary>
public static class SelectionGeometry
{
    /// <summary>手柄的点击容差(pt,用时乘缩放比例)。</summary>
    public const double HandleToleranceDip = 7;

    public static PxRect Rect(PxPoint start, PxPoint end) =>
        PxRect.FromEdges(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));

    public static SelectionHandle? Handle(PxPoint point, PxRect rect, int tolerance)
    {
        foreach (var handle in SelectionHandles.Candidates(rect, tolerance))
        {
            var center = handle.Point(rect);
            if (Math.Abs(center.X - point.X) <= tolerance && Math.Abs(center.Y - point.Y) <= tolerance)
            {
                return handle;
            }
        }

        return null;
    }

    /// <summary>拖动手柄后的选区。始终从按下时的原始选区计算,拖过对边时自动翻转。</summary>
    public static PxRect Resize(PxRect original, SelectionHandle handle, PxPoint point)
    {
        var (minX, maxX, minY, maxY) = (original.X, original.Right, original.Y, original.Bottom);
        if (handle.MovesLeft())
        {
            minX = point.X;
        }

        if (handle.MovesRight())
        {
            maxX = point.X;
        }

        if (handle.MovesTop())
        {
            minY = point.Y;
        }

        if (handle.MovesBottom())
        {
            maxY = point.Y;
        }

        return Rect(new PxPoint(minX, minY), new PxPoint(maxX, maxY));
    }

    /// <summary>平移选区,整体保持在 bounds 内(不改变大小)。</summary>
    public static PxRect Move(PxRect rect, int dx, int dy, PxRect bounds)
    {
        var x = Math.Min(Math.Max(rect.X + dx, bounds.X), Math.Max(bounds.X, bounds.Right - rect.Width));
        var y = Math.Min(Math.Max(rect.Y + dy, bounds.Y), Math.Max(bounds.Y, bounds.Bottom - rect.Height));
        return rect with { X = x, Y = y };
    }

    public static PxRect Clamp(PxRect rect, PxRect bounds)
    {
        var clipped = rect.Intersect(bounds);
        return clipped.IsEmpty ? new PxRect(bounds.X, bounds.Y, 0, 0) : clipped;
    }

    /// <summary>点限制在范围内(可以落在右、下边上:选区的右下角可以到屏幕边缘)。</summary>
    public static PxPoint Clamp(PxPoint point, PxRect bounds) =>
        new(Math.Min(Math.Max(point.X, bounds.X), bounds.Right), Math.Min(Math.Max(point.Y, bounds.Y), bounds.Bottom));

    /// <summary>选区在某个显示器截图里对应的像素矩形(截图左上角为原点)。</summary>
    public static PxRect PixelRect(PxRect rect, PxRect displayBounds) => rect.Offset(-displayBounds.X, -displayBounds.Y);

    /// <summary>方向键微调:移动,或者(resize 为 true 时)调整右边和下边(MAIO:每次 1 个物理像素)。</summary>
    public static PxRect Nudge(PxRect rect, int dx, int dy, bool resize, PxRect bounds)
    {
        if (!resize)
        {
            return Move(rect, dx, dy, bounds);
        }

        var resized = rect with { Width = Math.Max(1, rect.Width + dx), Height = Math.Max(1, rect.Height + dy) };
        return Clamp(resized, bounds);
    }
}
