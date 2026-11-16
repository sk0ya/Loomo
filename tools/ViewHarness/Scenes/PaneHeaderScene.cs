using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ViewHarness.Scenes;

/// <summary>ペインの見出し帯（PaneHeaderBar）とサイドバーの見出し帯（SidebarHeaderBand）に、ヘッダーの部品を実際の並びで置く。
/// 帯の高さは PaneHeaderH（文字サイズに追随）なので、文字サイズを上げたとき部品の文字が帯や部品自身に切られないかを見る
/// （2026-11-16、固定の Height で 22px のとき上下が切れていた）。</summary>
public sealed class PaneHeaderScene : IScene
{
    public string Name => "pane-header";
    public string Description => "ペイン／サイドバーの見出し帯とヘッダーの部品（ボタン・トグル・セグメント・札）";
    public Size Size => new(1400, 260);

    public FrameworkElement Build()
    {
        var root = new StackPanel();

        // ペインの見出し帯（Diff のヘッダーに近い並び）
        var bar = Styled(new DockPanel { LastChildFill = false }, "PaneHeaderBar");
        bar.Children.Add(Styled(new TextBlock { Text = "DIFF", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) }, "PanelHeader"));
        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        seg.Children.Add(Styled(new RadioButton { Content = "Git", GroupName = "src", IsChecked = true }, "SourceToggle"));
        seg.Children.Add(Styled(new RadioButton { Content = "比較", GroupName = "src" }, "SourceToggle"));
        seg.Children.Add(Styled(new ToggleButton { Content = "空白無視" }, "SegmentToggle"));
        bar.Children.Add(Styled(new Border { Child = seg }, "SegmentGroup"));
        bar.Children.Add(Styled(new ToggleButton { Content = "折り返し", Tag = Res("DiffIcon.Next"), IsChecked = true }, "PaneHeaderGhostToggle"));
        var chip = new StackPanel { Orientation = Orientation.Horizontal };
        chip.Children.Add(Styled(new Button { Content = "コミット 48613996", Tag = Res("DiffIcon.OpenInEditor") }, "PaneHeaderChipButton"));
        chip.Children.Add(Styled(new Button { ToolTip = "解除", Tag = Res("DiffIcon.Prev") }, "PaneHeaderChipButton"));
        bar.Children.Add(Styled(new Border { Child = chip }, "PaneHeaderContextChip"));
        bar.Children.Add(Styled(new Button { Content = "前へ", Tag = Res("DiffIcon.Prev") }, "PaneHeaderGhostButton"));
        bar.Children.Add(Styled(new Button { Content = "次へ", Tag = Res("DiffIcon.Next") }, "PaneHeaderGhostButton"));
        bar.Children.Add(Styled(new Border(), "PaneHeaderSeparator"));
        bar.Children.Add(Styled(new Button { Content = "変更を破棄" }, "PaneHeaderDangerButton"));
        bar.Children.Add(Styled(new Button { Content = "エディタで開く" }, "PaneHeaderToolButton"));
        var paneHeader = new DockPanel();
        paneHeader.Children.Add(bar);
        paneHeader.Children.Add(Styled(new Border(), "PaneHeaderRule"));
        root.Children.Add(paneHeader);

        root.Children.Add(new Border { Height = 24 });

        // サイドバーの見出し帯（TODO パネルと同じ：見出し＋記号だけのボタン、MinHeight=20 の上書き）
        var band = Styled(new Border { Width = 360, HorizontalAlignment = HorizontalAlignment.Left }, "SidebarHeaderBand");
        var dock = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        foreach (var (glyph, tip) in new[] { ("⌕", "絞り込み"), ("↻", "読み直す"), ("⊟", "すべて畳む"), ("⋯", "その他") })
            buttons.Children.Add(Styled(new Button { Content = glyph, ToolTip = tip, MinHeight = 20 }, "PaneHeaderGhostButton"));
        dock.Children.Add(buttons);
        dock.Children.Add(Styled(new TextBlock { Text = "TODO" }, "SidebarHeaderText"));
        band.Child = dock;
        root.Children.Add(band);

        return root;
    }

    private static object Res(string key) => Application.Current.FindResource(key);

    private static T Styled<T>(T element, string key) where T : FrameworkElement
    {
        element.Style = (Style)Application.Current.FindResource(key);
        return element;
    }
}
