using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ViewHarness.Scenes;

/// <summary>Controls.xaml の既存スタイルの見本。新しい画面で「どのスタイルを当てるか」を選ぶ物差しで、
/// シーンの書き方の例でもある（実物の VM を使うシーンは、設定・テーマ等を %APPDATA% へ書かないよう
/// 一時フォルダーの SettingsStore などを渡すこと）。</summary>
public sealed class StyleGalleryScene : IScene
{
    public string Name => "style-gallery";
    public string Description => "Controls.xaml の既存スタイル見本（ボタン・見出し・入力・トグル）";
    public Size Size => new(900, 620);

    public FrameworkElement Build()
    {
        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(Styled(new TextBlock { Text = "見出し（SectionTitle）" }, "SectionTitle"));
        panel.Children.Add(Styled(new TextBlock { Text = "欄の名前（FieldLabel）" }, "FieldLabel"));
        panel.Children.Add(Styled(new TextBlock { Text = "欄の説明（FieldHint）：この設定で何が変わるかを 1 行で書く。", TextWrapping = TextWrapping.Wrap }, "FieldHint"));

        panel.Children.Add(Row("ボタン",
            Styled(new Button { Content = "主の操作（FlatButton）" }, "FlatButton"),
            Styled(new Button { Content = "従の操作（SecondaryButton）" }, "SecondaryButton"),
            Styled(new Button { Content = "＋", ToolTip = "IconButton（文字が無いならツールチップ必須）" }, "IconButton")));

        panel.Children.Add(Row("幅広ボタン",
            Styled(new Button { Content = "WideButton" }, "WideButton"),
            Styled(new Button { Content = "WideSecondaryButton" }, "WideSecondaryButton")));

        panel.Children.Add(Row("ペインヘッダー",
            Styled(new Button { Content = "ツール（PaneHeaderToolButton）" }, "PaneHeaderToolButton"),
            Styled(new Button { Content = "控えめ（PaneHeaderGhostButton）" }, "PaneHeaderGhostButton"),
            Styled(new Button { Content = "危険（PaneHeaderDangerButton）" }, "PaneHeaderDangerButton")));

        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        seg.Children.Add(Styled(new ToggleButton { Content = "左", IsChecked = true }, "SegmentToggle"));
        seg.Children.Add(Styled(new ToggleButton { Content = "右" }, "SegmentToggle"));
        panel.Children.Add(Row("切り替え",
            Styled(new Border { Child = seg }, "SegmentGroup"),
            Styled(new ToggleButton { Content = "ToolbarToggleButton", IsChecked = true }, "ToolbarToggleButton"),
            new CheckBox { Content = "チェック（暗黙のスタイル）", IsChecked = true, VerticalAlignment = VerticalAlignment.Center }));

        var combo = new ComboBox { MinWidth = 220, ItemsSource = new[] { "コンボ（暗黙のスタイル）", "2 つ目" }, SelectedIndex = 0 };
        panel.Children.Add(Row("入力",
            new TextBox { Text = "テキストボックス（暗黙のスタイル）", MinWidth = 300 },
            combo));

        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static T Styled<T>(T element, string key) where T : FrameworkElement
    {
        element.Style = (Style)Application.Current.FindResource(key);
        return element;
    }

    private static FrameworkElement Row(string label, params FrameworkElement[] items)
    {
        var row = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var head = Styled(new TextBlock { Text = label, MinWidth = 120, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }, "FieldLabel");
        DockPanel.SetDock(head, Dock.Left);
        row.Children.Add(head);
        var wrap = new WrapPanel();
        foreach (var item in items)
        {
            item.Margin = new Thickness(0, 0, 8, 0);
            wrap.Children.Add(item);
        }
        row.Children.Add(wrap);
        return row;
    }
}
