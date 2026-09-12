namespace sk0ya.Loomo.App.Views;

/// <summary>
/// コマンドパレットの箱まわり（大きさ・一覧の表示と選択移動・プレビュー欄の開閉）を受け持つ。
/// 何を出すか（コマンドか検索結果か）は <see cref="ShellWindow"/> が決め、ここは描き方だけを知る。
/// </summary>
internal sealed class CommandPaletteViewController
{
    /// <summary>PaletteBox の上端余白（XAML の Margin="0,46,0,0" と対）。</summary>
    private const double TopMargin = 46;

    private readonly ListBox _list;
    private readonly FrameworkElement _box;
    private readonly PalettePreviewView _preview;
    private readonly ColumnDefinition _listColumn;
    private readonly ColumnDefinition _previewColumn;
    private readonly DataTemplate _commandRow;
    private readonly DataTemplate _navigationRow;
    private double _width;
    private double _height;

    public CommandPaletteViewController(
        ListBox list, FrameworkElement box, PalettePreviewView preview,
        ColumnDefinition listColumn, ColumnDefinition previewColumn,
        DataTemplate commandRow, DataTemplate navigationRow)
    {
        _list = list; _box = box; _preview = preview;
        _listColumn = listColumn; _previewColumn = previewColumn;
        _commandRow = commandRow; _navigationRow = navigationRow;
    }

    /// <summary>プレビュー欄を出しているか（＝ナビゲーション中）。箱の大きさもこれで変わる。</summary>
    public bool IsPreviewVisible { get; private set; }

    /// <summary>被せている領域（オーバーレイ）の実寸から箱の大きさを決める。ウィンドウ全体の寸法で
    /// 計算すると、袖やドックが出ている分だけ箱が領域からはみ出して端が切れるので、必ず領域内へ収める
    /// （下限より領域が狭い場合は領域優先）。上端の余白 <c>TopMargin</c> は XAML の Margin と対。
    /// プレビューを出しているときは一覧と本文を横に並べるので、箱そのものを広く・高く取る。</summary>
    public void UpdateSize(double width, double height)
    {
        if (width > 0 && height > 0)
        {
            _width = width; _height = height;
        }
        if (_width <= 0 || _height <= 0) return;

        var (ratio, min, max) = IsPreviewVisible ? (0.86, 1000d, 1900d) : (0.72, 760d, 1600d);
        var heightRatio = IsPreviewVisible ? 0.9 : 0.82;
        var minHeight = IsPreviewVisible ? 560d : 440d;
        _box.Width = Math.Min(Math.Clamp(_width * ratio, min, max), Math.Max(240, _width - 32));
        _box.MaxHeight = Math.Max(240, Math.Min(Math.Max(minHeight, _height * heightRatio), _height - TopMargin - 24));
    }

    /// <summary>プレビュー欄の開閉。閉じるときは中身も捨てる（次に開いた別の項目の残像を出さない）。</summary>
    public void SetPreviewVisible(bool visible)
    {
        if (IsPreviewVisible == visible)
            return;

        IsPreviewVisible = visible;
        _preview.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        // 行の形も切り替える：探して飛ぶ行は「名前・一致した行」を1段目で丸ごと使い、場所は2段目。
        _list.ItemTemplate = visible ? _navigationRow : _commandRow;
        _listColumn.Width = visible ? new GridLength(2, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        _previewColumn.Width = visible ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
        if (!visible)
            _preview.Clear();
        UpdateSize(0, 0);
    }

    public void ShowPreview(PalettePreviewContent content) => _preview.Show(content);

    public void ShowPreviewMessage(string header, string message) => _preview.ShowMessage(header, message);

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
