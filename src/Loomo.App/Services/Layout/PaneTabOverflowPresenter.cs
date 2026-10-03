using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>TABS のタブ（<see cref="TabEntryViewModel"/>）ではない一覧の1行（検索ペインのタブ）。</summary>
internal sealed record PaneTabOverflowRow(string Title, bool IsActive, string? ToolTip, Action Activate);

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
        double fontSize)
    {
        var tabs = kind switch
        {
            "Terminal" => _tabs.TerminalTabs,
            "Editor" => _tabs.EditorTabs,
            "Browser" => _tabs.BrowserTabs,
            _ => null,
        };
        if (tabs is null)
            return;

        _rows.Children.Clear();
        if (tabs.Count == 0)
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
                _rows.Children.Add(CreateRow(tab.Title, tab.IsActive, tab.FilePath ?? tab.Title,
                    rowStyle, activeForeground, fontSize, () => _activate(tab)));
        }

        _popup.PlacementTarget = button;
        _popup.IsOpen = true;
    }

    /// <summary>呼び出し側が組んだ行で一覧を出す（検索ペインのタブ）。</summary>
    public void ShowRows(FrameworkElement button, IReadOnlyList<PaneTabOverflowRow> rows, Style rowStyle,
        Brush activeForeground, double fontSize)
    {
        _rows.Children.Clear();
        foreach (var row in rows)
            _rows.Children.Add(CreateRow(row.Title, row.IsActive, row.ToolTip, rowStyle, activeForeground, fontSize,
                row.Activate));
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
