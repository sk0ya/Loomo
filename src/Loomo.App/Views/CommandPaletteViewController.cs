namespace sk0ya.Loomo.App.Views;

internal sealed class CommandPaletteViewController
{
    /// <summary>PaletteBox の上端余白（XAML の Margin="0,46,0,0" と対）。</summary>
    private const double TopMargin = 46;

    private readonly ListBox _list;
    private readonly FrameworkElement _box;

    public CommandPaletteViewController(ListBox list, FrameworkElement box)
    {
        _list = list; _box = box;
    }

    /// <summary>被せている領域（オーバーレイ）の実寸から箱の大きさを決める。ウィンドウ全体の寸法で
    /// 計算すると、袖やドックが出ている分だけ箱が領域からはみ出して端が切れるので、必ず領域内へ収める
    /// （下限 760 より領域が狭い場合は領域優先）。上端の余白 <c>TopMargin</c> は XAML の Margin と対。</summary>
    public void UpdateSize(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        _box.Width = Math.Min(Math.Clamp(width * 0.72, 760, 1600), Math.Max(240, width - 32));
        _box.MaxHeight = Math.Max(240, Math.Min(Math.Max(440, height * 0.82), height - TopMargin - 24));
    }

    public void ShowItems(IReadOnlyList<PaletteCommand> items, string query)
    {
        foreach (var item in items) item.TitleMatch = query;
        _list.ItemsSource = items;
        if (_list.Items.Count > 0)
        {
            _list.SelectedIndex = 0;
            _list.ScrollIntoView(_list.SelectedItem);
        }
    }

    public void MoveSelection(int delta)
    {
        var count = _list.Items.Count;
        if (count == 0) return;
        _list.SelectedIndex = ((_list.SelectedIndex < 0 ? 0 : _list.SelectedIndex) + delta + count) % count;
        _list.ScrollIntoView(_list.SelectedItem);
    }
}
