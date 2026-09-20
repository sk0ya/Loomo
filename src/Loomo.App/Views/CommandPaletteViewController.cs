namespace sk0ya.Loomo.App.Views;

/// <summary>
/// コマンドパレットの箱まわり（大きさ・一覧の表示と選択移動・右の詳細欄）を受け持つ。
/// 何を出すか（コマンドか検索結果か）は <see cref="ShellWindow"/> が決め、ここは描き方だけを知る。
/// </summary>
internal sealed class CommandPaletteViewController
{
    /// <summary>PaletteBox の上端余白（XAML の Margin="0,46,0,0" と対）。</summary>
    private const double TopMargin = 46;

    private readonly ListBox _list;
    private readonly FrameworkElement _box;
    private readonly PalettePreviewView _preview;
    private readonly DataTemplate _commandRow;
    private readonly DataTemplate _navigationRow;
    private double _width;
    private double _height;

    public CommandPaletteViewController(
        ListBox list, FrameworkElement box, PalettePreviewView preview,
        DataTemplate commandRow, DataTemplate navigationRow)
    {
        _list = list; _box = box; _preview = preview;
        _commandRow = commandRow; _navigationRow = navigationRow;
    }

    /// <summary>「探して飛ぶ」側（/ # @ :）を出しているか。右の欄に出す中身がこれで変わる。</summary>
    public bool IsNavigation { get; private set; }

    /// <summary>被せている領域（オーバーレイ）の実寸から箱の大きさを決める。ウィンドウ全体の寸法で
    /// 計算すると、袖やドックが出ている分だけ箱が領域からはみ出して端が切れるので、必ず領域内へ収める
    /// （下限より領域が狭い場合は領域優先）。上端の余白 <c>TopMargin</c> は XAML の Margin と対。
    /// <b>大きさは探し方で変えない</b>——モードを回すたびに箱が伸び縮みすると、目が追っている行が
    /// 横へ流れて読み直しになる。常に「一覧＋詳細」を置ける広さで開く。</summary>
    public void UpdateSize(double width, double height)
    {
        if (width > 0 && height > 0)
        {
            _width = width; _height = height;
        }
        if (_width <= 0 || _height <= 0) return;

        _box.Width = Math.Min(Math.Clamp(_width * 0.86, 1000d, 1900d), Math.Max(240, _width - 32));
        _box.MaxHeight = Math.Max(240, Math.Min(Math.Max(560d, _height * 0.9), _height - TopMargin - 24));
    }

    /// <summary>探し方（コマンド／探して飛ぶ）の切替。行の形だけを変え、箱と列の幅はそのまま。
    /// 前の項目の中身は捨てる（次に選んだものの残像を出さない）。</summary>
    public void SetNavigation(bool navigation)
    {
        if (IsNavigation == navigation)
            return;

        IsNavigation = navigation;
        // 行の形も切り替える：探して飛ぶ行は「名前・一致した行」を1段目で丸ごと使い、場所は2段目。
        _list.ItemTemplate = navigation ? _navigationRow : _commandRow;
        _preview.Clear();
    }

    public void ShowPreview(PalettePreviewContent content) => _preview.Show(content);

    public void ShowPreviewMessage(string header, string message) => _preview.ShowMessage(header, message);

    public void ShowPreviewDetail(PalettePreviewContent content) => _preview.ShowDetail(content);

    public void ClearPreview() => _preview.Clear();

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
