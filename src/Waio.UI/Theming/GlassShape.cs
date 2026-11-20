using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Waio.UI.Theming;

/// <summary>
/// 圆角(ADR-0026 修订):不靠 XAML 资源查找(运行中替换应用级 CornerRadius 资源,已经创建的元素不会变——
/// 表现为"圆角设置不生效"),而是直接给元素设值,外观改圆角时立即改掉。
/// <list type="bullet">
/// <item>卡片、分组等 WAIO 自己的容器:创建时调 <see cref="Card"/> / <see cref="Control"/> 登记。</item>
/// <item>按钮、输入框等系统控件:<see cref="Conform"/> 遍历可视树,把仍是旧圆角(或 Fluent 默认 4)的改成当前值。</item>
/// </list>
/// 只在 UI 线程使用。
/// </summary>
public static class GlassShape
{
    private static readonly List<(WeakReference<FrameworkElement> Element, bool IsCard)> Registered = [];

    /// <summary>卡片级圆角(设置分组、卡片、侧栏面板)。</summary>
    public static T Card<T>(T element)
        where T : FrameworkElement => Register(element, isCard: true);

    /// <summary>控件级圆角(键帽等 WAIO 自己画的小块)。</summary>
    public static T Control<T>(T element)
        where T : FrameworkElement => Register(element, isCard: false);

    /// <summary>不再管这个元素的圆角(如卡片被合进分组后不要圆角)。</summary>
    public static void Release(FrameworkElement element) =>
        Registered.RemoveAll(r => !r.Element.TryGetTarget(out var e) || ReferenceEquals(e, element));

    /// <summary>
    /// 把一棵可视树里系统控件(按钮、输入框、下拉框等)的圆角改成当前值:原值是 Fluent 默认 4、
    /// 或者任一档的控件圆角(说明是按旧设置建的)时才改,其他刻意设的圆角不动。页面加载后、圆角设置变化后调用。
    /// </summary>
    public static void Conform(DependencyObject root)
    {
        var target = new CornerRadius(Glass.Current.ControlRadius);
        Walk(root, element =>
        {
            if (element is Microsoft.UI.Xaml.Controls.Control control && IsInputControl(control)
                && IsUniform(control.CornerRadius, out var radius) && IsControlRadius(radius) && control.CornerRadius != target)
            {
                control.CornerRadius = target;
            }
        });
    }

    /// <summary>圆角设置变了:登记的元素全部改,打开的窗口里的系统控件也改。</summary>
    internal static void Update(IEnumerable<UIElement> roots)
    {
        Registered.RemoveAll(r => !r.Element.TryGetTarget(out _));
        foreach (var (reference, isCard) in Registered)
        {
            if (reference.TryGetTarget(out var element))
            {
                Set(element, isCard);
            }
        }

        foreach (var root in roots)
        {
            Conform(root);
        }
    }

    private static T Register<T>(T element, bool isCard)
        where T : FrameworkElement
    {
        Set(element, isCard);
        Registered.Add((new WeakReference<FrameworkElement>(element), isCard));
        return element;
    }

    private static void Set(FrameworkElement element, bool isCard)
    {
        var radius = new CornerRadius(isCard ? Glass.Current.CardRadius : Glass.Current.ControlRadius);
        switch (element)
        {
            case Border border:
                border.CornerRadius = radius;
                break;
            case Grid grid:
                grid.CornerRadius = radius;
                break;
            case StackPanel stack:
                stack.CornerRadius = radius;
                break;
            case Microsoft.UI.Xaml.Controls.Control control:
                control.CornerRadius = radius;
                break;
        }
    }

    private static bool IsInputControl(Microsoft.UI.Xaml.Controls.Control control) => control is
        Button or Microsoft.UI.Xaml.Controls.Primitives.ToggleButton or TextBox or PasswordBox or ComboBox
        or NumberBox or AutoSuggestBox or SplitButton or DropDownButton or HyperlinkButton;

    private static bool IsControlRadius(double radius) => radius is 4 or 6 or 8;

    private static bool IsUniform(CornerRadius r, out double radius)
    {
        radius = r.TopLeft;
        return r.TopLeft == r.TopRight && r.TopLeft == r.BottomLeft && r.TopLeft == r.BottomRight;
    }

    private static void Walk(DependencyObject node, Action<DependencyObject> visit)
    {
        visit(node);
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            Walk(VisualTreeHelper.GetChild(node, i), visit);
        }
    }
}
