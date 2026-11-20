using Microsoft.UI.Xaml;
using Waio.UI;

namespace Waio.Shell.Overlay;

/// <summary>屏幕底部短暂出现的提示条:置顶、无边框、不抢焦点。</summary>
internal sealed partial class HudWindow : Window
{
    public HudWindow()
    {
        InitializeComponent();
        WindowChrome.MakeOverlay(this);
        WindowChrome.MakePopupSurface(this, dark: Application.Current.RequestedTheme == ApplicationTheme.Dark); // 去掉残留边框,圆角 + 淡描边
    }

    public void Present(string text)
    {
        MessageText.Text = text;
        WindowChrome.PlaceBottomCenter(this, widthDip: 420, heightDip: 64);
        WindowChrome.ShowWithoutActivation(this);
    }
}
