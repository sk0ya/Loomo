using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

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
        FrameworkElement button, string kind, Style rowStyle, Brush dimForeground, double fontSize)
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
            {
                var content = new TextBlock
                {
                    Text = tab.Title,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontWeight = tab.IsActive ? FontWeights.SemiBold : FontWeights.Normal,
                };
                var row = new Button
                {
                    Style = rowStyle,
                    FontSize = fontSize,
                    ToolTip = tab.FilePath ?? tab.Title,
                    Content = content,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                row.Click += (_, _) =>
                {
                    _popup.IsOpen = false;
                    _activate(tab);
                };
                _rows.Children.Add(row);
            }
        }

        _popup.PlacementTarget = button;
        _popup.IsOpen = true;
    }
}
