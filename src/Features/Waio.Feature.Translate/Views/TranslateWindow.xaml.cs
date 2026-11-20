using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Waio.Feature.Translate.Domain;
using Waio.UI;
using Waio.UI.Theming;
using Windows.System;

namespace Waio.Feature.Translate.Views;

/// <summary>
/// 翻译窗口(MAIO <c>TranslateView</c> + <c>TranslateWindowController</c>)。状态全部在 <see cref="TranslateSession"/> 里,
/// 本类只负责显示和把用户操作转给它。拆成三个文件:本文件(内容与交互)、<c>.Cards.cs</c>(结果卡片)、
/// <c>.Layout.cs</c>(显示、定位、跟随内容改变高度、失去焦点关闭、把焦点还给原窗口)。
/// </summary>
internal sealed partial class TranslateWindow : Window
{
    private readonly TranslateSession _session;
    private bool _rendering;
    private bool _tornDown;

    public TranslateWindow(TranslateSession session, Func<TranslateSettings> settings, Action<TranslateSettings> saveSettings)
    {
        _session = session;
        _settings = settings;
        _saveSettings = saveSettings;
        InitializeComponent();
        WindowChrome.MakeOverlay(this, activatable: true);
        WindowChrome.MakePopupSurface(this, dark: Glass.IsDark());
        Glass.Attach(this, popup: true); // 磨砂玻璃,参数跟随外观设置(ADR-0026)

        HeaderIcon.Glyph = "";
        CloseIcon.Glyph = "";
        SwapIcon.Glyph = "";
        LineBreaksIcon.Glyph = "";
        ToolTipService.SetToolTip(CloseButton, "关闭(Esc)");
        ToolTipService.SetToolTip(SwapButton, "交换源语言和目标语言");
        ToolTipService.SetToolTip(LineBreaksButton, "删除原文中的换行后重新翻译");

        foreach (var language in Languages.All)
        {
            SourceLanguageBox.Items.Add(language.DisplayName());
        }

        _session.Changed += Render;
        _session.CloseRequested += Dismiss;
        AppWindow.Closing += (_, e) =>
        {
            if (_tornDown)
            {
                return;
            }

            e.Cancel = true; // Alt+F4 等:只隐藏,窗口留着下次复用
            Dismiss();
        };
        Root.SizeChanged += (_, _) => FitToContent();
        WindowDrag.Attach(this, Header); // 只有顶部这一栏能拖动窗口(其余地方可以选择文字)
        _dismissTimer = CreateDismissTimer();
        Render();
    }

    /// <summary>功能停用:真正关闭窗口(之后不能再用)。</summary>
    public void TearDown()
    {
        Dismiss();
        _session.Changed -= Render;
        _session.CloseRequested -= Dismiss;
        _tornDown = true;
        Close();
    }

    /// <summary>窗口在前台(焦点在翻译窗口里)。</summary>
    public bool IsFocused => AppWindow.IsVisible && WindowChrome.IsForeground(this);

    private void Render()
    {
        _rendering = true;
        try
        {
            if (SourceBox.Text != _session.SourceText)
            {
                SourceBox.Text = _session.SourceText;
                SourceBox.SelectionStart = SourceBox.Text.Length;
            }

            SourceBox.FontSize = _session.FontSize;
            PinIcon.Glyph = _session.IsPinned ? "" : "";
            ToolTipService.SetToolTip(PinButton, _session.IsPinned ? "取消固定(失去焦点时会自动关闭)" : "固定窗口(失去焦点时不关闭)");
            RenderPinState();

            // 源语言:"自动"一项显示检测结果
            SourceLanguageBox.Items[0] = _session.Detected is { } detected ? $"自动({detected.DisplayName()})" : Language.Auto.DisplayName();
            SourceLanguageBox.SelectedIndex = Array.IndexOf(Languages.All.ToArray(), _session.SourceLanguage);

            // 目标语言:第一项"自动(实际目标)",其余是全部目标语言
            TargetLanguageBox.Items.Clear();
            TargetLanguageBox.Items.Add($"自动({_session.EffectiveTarget.DisplayName()})");
            foreach (var language in Languages.Targets)
            {
                TargetLanguageBox.Items.Add(language.DisplayName());
            }

            TargetLanguageBox.SelectedIndex = _session.TargetOverride is { } target ? Languages.Targets.ToList().IndexOf(target) + 1 : 0;
            RenderCards();
        }
        finally
        {
            _rendering = false;
        }
    }

    // ---- 用户操作 ----

    private void OnSourceKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!shift)
        {
            e.Handled = true; // 回车翻译;Shift+回车交给文本框换行
            _session.Translate();
        }
    }

    private void OnSourceChanged(object sender, TextChangedEventArgs e)
    {
        if (!_rendering)
        {
            _session.SourceText = SourceBox.Text;
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Escape || (ctrl && e.Key == VirtualKey.W))
        {
            e.Handled = true; // Esc 或 Ctrl+W 关闭(MAIO:Esc / ⌘W)
            Dismiss();
        }
    }

    private void OnSourceLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || SourceLanguageBox.SelectedIndex < 0)
        {
            return;
        }

        _session.SourceLanguage = Languages.All[SourceLanguageBox.SelectedIndex];
        _session.Translate();
    }

    private void OnTargetLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || TargetLanguageBox.SelectedIndex < 0)
        {
            return;
        }

        _session.TargetOverride = TargetLanguageBox.SelectedIndex == 0 ? null : Languages.Targets[TargetLanguageBox.SelectedIndex - 1];
        _session.Translate();
    }

    private void OnSwapClick(object sender, RoutedEventArgs e) => _session.SwapLanguages();

    private void OnRemoveLineBreaksClick(object sender, RoutedEventArgs e) => _session.RemoveLineBreaks();

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _session.IsPinned = !_session.IsPinned;
        Render();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Dismiss();

    /// <summary>固定时按钮显示为强调色底(和 MAIO 的图钉按下状态一样一眼能看出来);只换图标形状不够明显。</summary>
    private void RenderPinState()
    {
        var resources = Application.Current.Resources;
        if (_session.IsPinned)
        {
            PinButton.Background = (Brush)resources["AccentFillColorDefaultBrush"];
            PinIcon.Foreground = (Brush)resources["TextOnAccentFillColorPrimaryBrush"];
        }
        else
        {
            PinButton.ClearValue(Control.BackgroundProperty);
            PinIcon.ClearValue(IconElement.ForegroundProperty);
        }
    }
}
