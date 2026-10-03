using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>一覧の先頭に足す、タブではない行（検索ペインの「現在の検索」）。</summary>
internal sealed record PaneTabOverflowLeadingRow(string Title, bool IsActive, Action Activate);

/// <summary>ペインタブの全件一覧ポップアップを組み立て、選択タブをShellへ返す。</summary>
internal sealed class PaneTabOverflowPresenter
{
    private readonly Popup _popup;
    private readonly Panel _rows;
    private readonly TabsViewModel _tabs;
    private readonly Action<TabEntryViewModel> _activate;

    public PaneTabOverflowPresenter(
        Popup popup,
        Panel rows,
        TabsViewModel tabs,
        Action<TabEntryViewModel> activate)
    {
        _popup = popup;
        _rows = rows;
        _tabs = tabs;
        _activate = activate;
    }

    public void Show(
        FrameworkElement button, string kind, Style rowStyle, Brush dimForeground, Brush activeForeground,
        double fontSize, PaneTabOverflowLeadingRow? leading = null)
    {
        var tabs = kind switch
        {
            "Terminal" => _tabs.TerminalTabs,
            "Editor" => _tabs.EditorTabs,
            "Browser" => _tabs.BrowserTabs,
            "Search" => _tabs.SearchTabs,
            _ => null,
        };
        if (tabs is null)
            return;

        _rows.Children.Clear();
        if (leading is not null)
            _rows.Children.Add(CreateRow(leading.Title, leading.IsActive, null, rowStyle, activeForeground, fontSize,
                leading.Activate));
        if (tabs.Count == 0 && leading is null)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = "タブがありません",
                FontSize = fontSize,
                Margin = new Thickness(10, 6, 10, 6),
                Foreground = dimForeground,
            });
        }
        else
        {
            foreach (var tab in tabs)
                _rows.Children.Add(CreateRow(tab.Title, tab.IsActive, tab.FilePath ?? tab.ToolTip ?? tab.Title,
                    rowStyle, activeForeground, fontSize, () => _activate(tab)));
        }

        _popup.PlacementTarget = button;
        _popup.IsOpen = true;
    }

    private Button CreateRow(string title, bool isActive, string? toolTip, Style rowStyle, Brush activeForeground,
        double fontSize, Action activate)
    {
        var content = new TextBlock
        {
            Text = title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
        };
        // いま見ているタブは名前をアクセント色にする（タブ帯・サイドバーの TABS と同じ言い切り方）。
        // 太字だけだと一覧の中では差が出ない。非アクティブ側は既定の色を継がせたいので触らない。
        if (isActive)
            content.Foreground = activeForeground;
        var row = new Button
        {
            Style = rowStyle,
            FontSize = fontSize,
            ToolTip = toolTip ?? title,
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        row.Click += (_, _) =>
        {
            _popup.IsOpen = false;
            activate();
        };
        return row;
    }
}
