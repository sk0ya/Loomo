namespace sk0ya.Loomo.App.Views;

internal sealed class CommandPaletteViewController
{
    private readonly ListBox _list;
    private readonly FrameworkElement _box;

    public CommandPaletteViewController(ListBox list, FrameworkElement box)
    {
        _list = list; _box = box;
    }

    public void UpdateSize(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        _box.Width = Math.Clamp(width * 0.72, 760, 1600);
        _box.MaxHeight = Math.Max(440, height * 0.82);
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
