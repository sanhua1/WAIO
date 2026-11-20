using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Annotation;
using Waio.Feature.Screenshot.Domain.Recording;

namespace Waio.Feature.Screenshot;

/// <summary>
/// 截图设置(features/screenshot.json;字段、默认值与 MAIO <c>ScreenshotSettings</c> 一致)。字段只增不删。
/// 字段随阶段增加(第 2 阶段加了 ToolStyle,第 3 阶段加了贴图的三项,第 6 阶段加了录屏)。
/// </summary>
internal sealed class ScreenshotSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>总是识别控件(关闭时按住 Ctrl 才识别控件,平时只识别窗口)。MAIO 是按住 ⌘。</summary>
    public bool AlwaysDetectControls { get; set; }

    public bool ShowMagnifier { get; set; } = true;

    /// <summary>取色的格式,截图时单独按一下 Shift 切换后会记住。</summary>
    public ColorFormat ColorFormat { get; set; } = ColorFormat.Hex;

    /// <summary>保存文件夹,空表示默认的"图片\WAIO"。</summary>
    public string SaveDirectory { get; set; } = "";

    /// <summary>Ctrl+S 时弹出"另存为"对话框。</summary>
    public bool AskSaveLocation { get; set; }

    /// <summary>复制截图时也自动保存一份到保存文件夹。</summary>
    public bool AutoSave { get; set; }

    /// <summary>截图历史的存储上限(用户选择默认 2 GB)。</summary>
    public HistoryLimit HistoryLimit { get; set; } = HistoryLimit.Gb2;

    /// <summary>标注工具上次用的颜色(荧光笔单独记)、每个工具的大小(pt)、实心 / 圆角 / 虚线 / 粗体 / 文字外观、文字背景色。</summary>
    public ToolStyle ToolStyle { get; set; } = new();

    /// <summary>贴图四周显示系统主题色的发光。</summary>
    public bool PinShadow { get; set; } = true;

    /// <summary>贴图缩放:修饰键 + 滚轮 / - =(MAIO 默认不按修饰键)。</summary>
    public PinModifier PinZoomModifier { get; set; } = PinModifier.None;

    /// <summary>贴图透明度:修饰键 + 滚轮 / - =(MAIO 默认 ⌘,这里是 Ctrl)。</summary>
    public PinModifier PinOpacityModifier { get; set; } = PinModifier.Ctrl;

    /// <summary>贴图后在后台识别文字,鼠标在文字上可以直接拖动选择、复制(同 PixPin;需要 PP-OCR 模型,ADR-0019)。</summary>
    public bool PinSelectableText { get; set; } = true;

    /// <summary>录屏格式:控制条上上次选的(MAIO 同,记住上次的选择)。</summary>
    public RecordingFormat RecordingFormat { get; set; } = RecordingFormat.Mp4;

    /// <summary>MP4 录系统声音(控制条上的开关,记住上次的选择)。</summary>
    public bool RecordSystemAudio { get; set; }

    /// <summary>MP4 录麦克风(控制条上的开关,记住上次的选择)。</summary>
    public bool RecordMicrophone { get; set; }

    /// <summary>MP4 帧率(15 / 30 / 60)。</summary>
    public int RecordingFps { get; set; } = 30;

    /// <summary>GIF 帧率(10 / 15 / 20 / 30,MAIO 默认 15)。</summary>
    public int GifFps { get; set; } = 15;

    /// <summary>GIF 按物理像素录(默认按逻辑像素,150% 缩放时文件小很多)。</summary>
    public bool GifHighQuality { get; set; }

    /// <summary>录进鼠标指针。</summary>
    public bool RecordCursor { get; set; } = true;

    /// <summary>录屏保存文件夹,空表示默认的"视频\WAIO"。</summary>
    public string RecordingDirectory { get; set; } = "";

    /// <summary>方法而不是属性:设置按 JSON 存,只读属性也会被写进文件。</summary>
    public PinControls PinControls() => new(PinZoomModifier, PinOpacityModifier);
}
