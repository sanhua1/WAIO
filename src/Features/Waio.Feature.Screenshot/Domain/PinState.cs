namespace Waio.Feature.Screenshot.Domain;

/// <summary>
/// 贴图的缩放、旋转、透明度(MAIO PinState,spec 3.3)。尺寸单位是物理像素:MAIO 按 pt,
/// Windows 各显示器缩放可以不同,这里贴图按截图时的像素 1:1 显示(100% 缩放时)。
/// </summary>
public sealed record PinState
{
    public const double MinZoom = 0.1;
    public const double MaxZoom = 5;
    public const double MinOpacity = 0.1;

    /// <summary>滚轮、- = 每一步的缩放和透明度变化(MAIO:用户要求 5%)。</summary>
    public const double Step = 0.05;

    /// <summary>阴影留出的外边距(pt,乘以缩放比例)。</summary>
    public const double MarginPt = 14;

    /// <summary>原图大小(像素,未旋转)。</summary>
    public required (int Width, int Height) ImageSize { get; init; }

    /// <summary>截图时的缩放比例(外边距、提示文字按 pt 换算用)。</summary>
    public double Scale { get; init; } = 1;

    public double Zoom { get; init; } = 1;

    /// <summary>顺时针旋转的 90° 次数(0~3)。</summary>
    public int QuarterTurns { get; init; }

    public double Opacity { get; init; } = 1;

    public bool ShowsShadow { get; init; } = true;

    public int Margin => (int)Math.Round(MarginPt * Scale);

    /// <summary>旋转、缩放后的内容大小。</summary>
    public (int Width, int Height) ContentSize
    {
        get
        {
            var (w, h) = QuarterTurns % 2 == 0 ? ImageSize : (ImageSize.Height, ImageSize.Width);
            return (Math.Max(1, (int)Math.Round(w * Zoom)), Math.Max(1, (int)Math.Round(h * Zoom)));
        }
    }

    /// <summary>窗口大小(内容 + 四周外边距)。</summary>
    public (int Width, int Height) WindowSize => (ContentSize.Width + Margin * 2, ContentSize.Height + Margin * 2);

    /// <summary>
    /// 实际能放大到多少:不超过 500%,内容最长边不超过 8192 像素、总像素不超过 4000 万
    /// (贴图整张画在一个分层窗口里,太大会超出显卡纹理上限、占用几百 MB 内存)。
    /// </summary>
    public double ZoomLimit
    {
        get
        {
            var (w, h) = ImageSize;
            var limit = Math.Min(MaxZoom, Math.Min(8192.0 / Math.Max(1, Math.Max(w, h)), Math.Sqrt(40_000_000.0 / Math.Max(1.0, (double)w * h))));
            return Math.Max(MinZoom, limit);
        }
    }

    public PinState Rotated(bool clockwise) => this with { QuarterTurns = (QuarterTurns + (clockwise ? 1 : 3)) % 4 };

    /// <summary>
    /// 贴图内容上的一点(相对内容左上角,屏幕像素)对应原图(未旋转、未缩放)的哪个像素。
    /// 和绘制一致:原图按 <see cref="Zoom"/> 缩放后绕内容中心顺时针转 <see cref="QuarterTurns"/> × 90°。
    /// </summary>
    public (double X, double Y) ImagePoint(double x, double y)
    {
        var (cw, ch) = ContentSize;
        var (dx, dy) = (x - cw / 2.0, y - ch / 2.0);
        var (ux, uy) = QuarterTurns switch // 逆着转回去
        {
            1 => (dy, -dx),
            2 => (-dx, -dy),
            3 => (-dy, dx),
            _ => (dx, dy),
        };
        return (ux / Zoom + ImageSize.Width / 2.0, uy / Zoom + ImageSize.Height / 2.0);
    }

    /// <summary>
    /// 以 <paramref name="anchor"/>(内容里的相对位置 0~1)为中心缩放到 <paramref name="value"/>(10%~500%),
    /// 返回新状态和新的内容左上角(MAIO zoom(to:anchor:contentOrigin:))。
    /// </summary>
    public (PinState State, PxPoint Origin) ZoomedTo(double value, (double X, double Y) anchor, PxPoint contentOrigin)
    {
        var old = ContentSize;
        var next = this with { Zoom = Math.Clamp(value, MinZoom, ZoomLimit) };
        var size = next.ContentSize;
        var origin = new PxPoint(
            contentOrigin.X + (int)Math.Round((old.Width - size.Width) * anchor.X),
            contentOrigin.Y + (int)Math.Round((old.Height - size.Height) * anchor.Y));
        return (next, origin);
    }

    /// <summary>按步缩放:每步 ±5 个百分点,并对齐到 5% 的整数倍(100% → 105% → 110%)。</summary>
    public (PinState State, PxPoint Origin) ZoomedBy(int steps, (double X, double Y) anchor, PxPoint contentOrigin) =>
        ZoomedTo((Math.Round(Zoom / Step) + steps) * Step, anchor, contentOrigin);

    public PinState WithOpacity(double value) => this with { Opacity = Math.Clamp(value, MinOpacity, 1) };

    public PinState OpacityStepped(int steps) => WithOpacity((Math.Round(Opacity / Step) + steps) * Step);

    /// <summary>数字键:1~9 是 10%~90%,0 是 100%。</summary>
    public static double? OpacityForDigit(int digit) => digit switch
    {
        0 => 1,
        >= 1 and <= 9 => digit / 10.0,
        _ => null,
    };

    /// <summary>
    /// 没有原始位置时(贴剪贴板、从历史贴)放在显示器中间,太大时缩小到显示器的 80%(MAIO PinManager.centeredRect)。
    /// 返回内容左上角和缩放比例。
    /// </summary>
    public static (PxPoint Origin, double Zoom) Centered((int Width, int Height) size, PxRect display)
    {
        var zoom = Math.Min(1, Math.Min(display.Width * 0.8 / Math.Max(size.Width, 1), display.Height * 0.8 / Math.Max(size.Height, 1)));
        var w = (int)Math.Round(size.Width * zoom);
        var h = (int)Math.Round(size.Height * zoom);
        return (new PxPoint(display.X + (display.Width - w) / 2, display.Y + (display.Height - h) / 2), zoom);
    }
}

/// <summary>贴图上调整缩放、透明度用的修饰键(配合滚轮或 - / = 键,MAIO PinModifier)。MAIO 的 ⌘ 对应 Ctrl,⌥ 对应 Alt。</summary>
public enum PinModifier
{
    /// <summary>不按修饰键,直接滚轮 / 直接按 - =。</summary>
    None,
    Ctrl,
    Alt,
    Shift,
}

public static class PinModifiers
{
    public static string Title(this PinModifier modifier) => modifier switch
    {
        PinModifier.None => "不按修饰键",
        PinModifier.Ctrl => "Ctrl",
        PinModifier.Alt => "Alt",
        _ => "Shift",
    };

    /// <summary>贴图上滚轮 / - = 调的是什么。</summary>
    public enum Target
    {
        Zoom,
        Opacity,
    }

    /// <summary>按下的修饰键对应调什么;两个设置成同一个键时缩放优先(MAIO PinView.target)。</summary>
    public static Target? TargetFor(bool ctrl, bool alt, bool shift, PinModifier zoom, PinModifier opacity)
    {
        var pressed = (ctrl ? 1 : 0) + (alt ? 1 : 0) + (shift ? 1 : 0) > 1 ? (PinModifier?)null
            : ctrl ? PinModifier.Ctrl : alt ? PinModifier.Alt : shift ? PinModifier.Shift : PinModifier.None;
        if (pressed is null)
        {
            return null;
        }

        if (pressed == zoom)
        {
            return Target.Zoom;
        }

        return pressed == opacity ? Target.Opacity : null;
    }
}

/// <summary>贴图的操作设置(MAIO PinControls):缩放默认直接滚轮,透明度默认 Ctrl + 滚轮(MAIO ⌘)。</summary>
public sealed record PinControls(PinModifier Zoom = PinModifier.None, PinModifier Opacity = PinModifier.Ctrl);
