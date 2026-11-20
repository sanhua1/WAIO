using Microsoft.Graphics.Canvas;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Feature.Screenshot.Infrastructure.Rendering;
using Rect = Windows.Foundation.Rect;
using static Waio.Feature.Screenshot.UI.PinNative;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 贴图上选择识别出的文字(同 PixPin):贴图后后台识别,鼠标在文字上时是 I 形光标,拖动选择、双击选中一段,
/// Ctrl+A 全选,Ctrl+C 复制选中的文字;在没有文字的地方拖动仍然是移动贴图。
/// 文字上的命中测试返回 HTCLIENT(由这里处理鼠标),其余地方仍是 HTCAPTION(系统拖动)。
/// </summary>
internal sealed partial class PinWindow
{
    private PinTextLayout? _text;
    private TextPosition _anchor;
    private TextPosition _focus;
    private bool _selecting;

    /// <summary>识别出的文字(还没识别完、没开这个功能或没有文字时为 null)。</summary>
    public PinTextLayout? Text => _text;

    public bool HasSelection => _text is not null && _anchor != _focus;

    public string SelectedText => HasSelection ? _text!.Text(_anchor, _focus) : "";

    /// <summary>后台识别完成后交给贴图。图已经换掉(重新编辑过)时丢弃。</summary>
    public void SetText(PinTextLayout layout, PixelBuffer source)
    {
        if (!ReferenceEquals(source, Image) || _hwnd == 0)
        {
            return;
        }

        _text = layout.IsEmpty ? null : layout;
        ClearSelection();
    }

    private void ResetText()
    {
        _text = null;
        _selecting = false;
        _anchor = _focus = default;
    }

    private void ClearSelection()
    {
        if (HasSelection)
        {
            _anchor = _focus = default;
            Redraw();
        }
    }

    private void SelectAll()
    {
        if (_text is { } text)
        {
            (_anchor, _focus) = (text.Start, text.End);
            Redraw();
        }
    }

    /// <summary>屏幕上的点(全局物理像素)是不是在文字上。</summary>
    private bool IsOverText(int screenX, int screenY)
    {
        if (_text is not { } text)
        {
            return false;
        }

        var rect = ContentRect;
        var (x, y) = State.ImagePoint(screenX - rect.X, screenY - rect.Y);
        return text.Contains(x, y);
    }

    /// <summary>客户区坐标(相对窗口左上角,含阴影外边距)换成原图坐标。</summary>
    private (double X, double Y) ImagePointFromClient(nint lParam)
    {
        var (x, y) = PointFromLParam(lParam);
        return State.ImagePoint(x - State.Margin, y - State.Margin);
    }

    /// <summary>文字相关的鼠标消息;不是文字相关的返回 null。</summary>
    private nint? HandleText(uint message, nint lParam)
    {
        switch (message)
        {
            case WmSetCursor when (lParam & 0xFFFF) == HtClient:
                SetCursor(LoadCursorW(0, 32513 /* IDC_IBEAM */));
                return 1;
            case WmLButtonDown when _text is { } text:
                var (x, y) = ImagePointFromClient(lParam);
                _anchor = _focus = text.PositionAt(x, y);
                _selecting = true;
                SetCapture(_hwnd);
                Redraw();
                return 0;
            case WmMouseMove:
                TrackHover(nonClient: false);
                if (_selecting && _text is { } dragged)
                {
                    var (mx, my) = ImagePointFromClient(lParam);
                    var focus = dragged.PositionAt(mx, my);
                    if (focus != _focus)
                    {
                        _focus = focus;
                        Redraw();
                    }
                }

                return 0;
            case WmLButtonUp when _selecting:
                _selecting = false;
                ReleaseCapture();
                return 0;
            case WmCaptureChanged:
                _selecting = false;
                return 0;
            case WmLButtonDblClk when _text is { } clicked:
                var (dx, dy) = ImagePointFromClient(lParam);
                (_anchor, _focus) = clicked.SegmentRange(clicked.PositionAt(dx, dy).Segment);
                Redraw();
                return 0;
            case WmMouseLeave:
                _hovered = false;
                Redraw();
                return 0;
        }

        return null;
    }

    /// <summary>开始跟踪鼠标离开(客户区和非客户区各自要单独登记)。</summary>
    private void TrackHover(bool nonClient)
    {
        if (_hovered && _trackingNonClient == nonClient)
        {
            return;
        }

        _hovered = true;
        _trackingNonClient = nonClient;
        var track = new TrackMouseEventInfo
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<TrackMouseEventInfo>(),
            Flags = TmeLeave | (nonClient ? TmeNonClient : 0),
            Hwnd = _hwnd,
        };
        TrackMouseEvent(ref track);
        Redraw();
    }

    private bool _trackingNonClient;

    /// <summary>在图片的坐标系里(和图片同一个变换)画选中文字的高亮。</summary>
    private void DrawSelection(CanvasDrawingSession ds, double imageWidth, double imageHeight)
    {
        if (!HasSelection)
        {
            return;
        }

        var color = AnnotationRenderer.Rgba(96, _accent.R, _accent.G, _accent.B);
        var zoom = State.Zoom;
        foreach (var r in _text!.Highlights(_anchor, _focus))
        {
            ds.FillRectangle(new Rect(r.X * zoom - imageWidth / 2, r.Y * zoom - imageHeight / 2, r.Width * zoom, r.Height * zoom), color);
        }
    }
}
