using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Waio.Feature.Translate.Domain;

namespace Waio.Feature.Translate.Views;

/// <summary>
/// 结果卡片(MAIO <c>ResultCardView</c>):标题栏(图标、名称、进行中、复制、失败时重试、折叠),译文可选中复制,
/// 查单词时显示音标和释义。卡片视图按翻译源 id 复用:大模型逐字输出时只改文字,不重建整张卡片。
/// </summary>
internal sealed partial class TranslateWindow
{
    private readonly Dictionary<string, CardView> _cardViews = [];

    /// <summary>复制译文(写入剪贴板)。由功能接上 IClipboardService。</summary>
    public Func<string, Task>? CopyText { get; set; }

    private void RenderCards()
    {
        var ids = _session.Cards.Select(c => c.Id).ToHashSet();
        foreach (var stale in _cardViews.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _cardViews.Remove(stale);
        }

        var views = _session.Cards.Select(card =>
        {
            if (!_cardViews.TryGetValue(card.Id, out var view))
            {
                view = new CardView(this, card.Id);
                _cardViews[card.Id] = view;
            }

            view.Update(card, _session.FontSize);
            return view.Root;
        }).ToList();

        if (!ResultsPanel.Children.SequenceEqual(views))
        {
            ResultsPanel.Children.Clear();
            foreach (var view in views)
            {
                ResultsPanel.Children.Add(view);
            }
        }

        ResultsScroller.Visibility = views.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private sealed class CardView
    {
        private const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";

        private readonly TranslateWindow _owner;
        private readonly string _id;
        private readonly TextBlock _name = new() { FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
        private readonly ProgressRing _progress = new() { Width = 14, Height = 14, IsActive = true, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _copy;
        private readonly FontIcon _copyIcon = new() { FontSize = 13, Glyph = "" };
        private readonly Button _retry;
        private readonly Button _collapse;
        private readonly FontIcon _collapseIcon = new() { FontSize = 12 };
        private readonly StackPanel _dictionary = new() { Spacing = 3 };
        private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 13 };
        private string? _currentText;
        private DictionaryEntry? _renderedDictionary;

        public CardView(TranslateWindow owner, string id)
        {
            _owner = owner;
            _id = id;
            _copy = IconButton(_copyIcon, "复制译文", OnCopy);
            _retry = IconButton(new FontIcon { FontSize = 13, Glyph = "" }, "重试", () => owner._session.Retry(id));
            _collapse = IconButton(_collapseIcon, "折叠", () => owner._session.ToggleCollapsed(id));
            _error.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];

            var title = new Grid { ColumnSpacing = 8, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Place(title, _name, 0);
            Place(title, _progress, 1);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(_copy);
            actions.Children.Add(_retry);
            actions.Children.Add(_collapse);
            Place(title, actions, 3);
            title.DoubleTapped += (_, _) => owner._session.ToggleCollapsed(id); // 双击标题栏折叠/展开

            Body = new StackPanel { Spacing = 6 };
            Body.Children.Add(_dictionary);
            Body.Children.Add(_text);
            Body.Children.Add(_error);

            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(title);
            content.Children.Add(Body);
            Root = (Border)XamlReader.Load(
                $"<Border {Ns} Padding='10,8,6,10' CornerRadius='8' Background='{{ThemeResource CardBackgroundFillColorDefaultBrush}}' />");
            Root.Child = content;
        }

        public Border Root { get; }

        private StackPanel Body { get; }

        public void Update(ResultCard card, double fontSize)
        {
            _name.Text = card.Name;
            _progress.Visibility = Visible(card.IsWorking);
            _currentText = card.Phase is CardPhase.Streaming or CardPhase.Done ? card.Text : null;
            _copy.Visibility = Visible(!string.IsNullOrEmpty(_currentText));
            _retry.Visibility = Visible(card.Phase == CardPhase.Failed);
            _collapseIcon.Glyph = card.IsCollapsed ? "" : "";
            ToolTipService.SetToolTip(_collapse, card.IsCollapsed ? "展开" : "折叠");

            Body.Visibility = Visible(!card.IsCollapsed && card.Phase != CardPhase.Loading);
            _text.FontSize = fontSize;
            _text.Text = _currentText ?? "";
            _text.Visibility = Visible(_currentText is not null);
            _error.Text = card.Error ?? "";
            _error.Visibility = Visible(card.Phase == CardPhase.Failed);
            RenderDictionary(card.Phase == CardPhase.Done ? card.Dictionary : null, fontSize);
        }

        /// <summary>词典模式:音标 + 按词性分组的释义,下面一条分隔线。</summary>
        private void RenderDictionary(DictionaryEntry? entry, double fontSize)
        {
            if (ReferenceEquals(entry, _renderedDictionary))
            {
                return;
            }

            _renderedDictionary = entry;
            _dictionary.Children.Clear();
            _dictionary.Visibility = Visible(entry is { IsEmpty: false });
            if (entry is not { IsEmpty: false })
            {
                return;
            }

            var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            if (entry.Phonetics.Count > 0)
            {
                _dictionary.Children.Add(new TextBlock
                {
                    Text = string.Join("  ", entry.Phonetics), FontSize = fontSize - 1, Foreground = secondary, IsTextSelectionEnabled = true,
                });
            }

            foreach (var meaning in entry.Meanings)
            {
                var line = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = fontSize };
                if (meaning.PartOfSpeech.Length > 0)
                {
                    line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                    {
                        Text = meaning.PartOfSpeech + "  ", FontStyle = Windows.UI.Text.FontStyle.Italic, FontSize = fontSize - 2, Foreground = secondary,
                    });
                }

                line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = string.Join(";", meaning.Terms) });
                _dictionary.Children.Add(line);
            }

            _dictionary.Children.Add(new Border
            {
                Height = 1, Margin = new Thickness(0, 2, 0, 2),
                Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            });
        }

        private async void OnCopy()
        {
            if (_currentText is not { Length: > 0 } text || _owner.CopyText is not { } copy)
            {
                return;
            }

            await copy(text);
            _copyIcon.Glyph = ""; // 打勾 1.2 秒(MAIO)
            await Task.Delay(1200);
            _copyIcon.Glyph = "";
        }

        private static Button IconButton(FontIcon icon, string tip, Action click)
        {
            var button = new Button
            {
                Content = icon, Padding = new Thickness(6), MinWidth = 0, MinHeight = 0, BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            ToolTipService.SetToolTip(button, tip);
            button.Click += (_, _) => click();
            return button;
        }

        private static void Place(Grid grid, FrameworkElement element, int column)
        {
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }

        private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
