using sk0ya.Loomo.App.Services.Infrastructure;

namespace sk0ya.Loomo.App.Views;

/// <summary>ファイル一覧ペインの容れ物。<see cref="FilesColumnView"/> を 1／2／4 枚並べる。
///
/// <para>カラムの View は<b>作り直さず使い回す</b>（VM が常に4つ持っているのと対）。1↔4 を往復する
/// たびに作り直すと、スクロール位置・選択・ListBox の実体化がその都度失われ、「戻ってきたら
/// さっきの場所」という約束（§24.4）が画面側で破れる。ここでは親から外して並べ直すだけ。</para></summary>
public partial class FilesPaneView : UserControl
{
    private readonly List<FilesColumnView> _columnViews = new();
    private FilesPaneViewModel? _boundVm;

    public FilesPaneView()
    {
        InitializeComponent();
        for (var i = 0; i < FilesPaneViewModel.MaxColumns; i++)
            _columnViews.Add(new FilesColumnView());
        DataContextChanged += OnDataContextChanged;
    }

    private FilesPaneViewModel? Vm => DataContext as FilesPaneViewModel;

    /// <summary>ペインがフォーカスされたときの入り口（<c>ShellWindow.FocusPane</c>）。
    /// 操作対象のカラムへ渡す。</summary>
    public void FocusList()
    {
        var index = Vm?.ActiveColumn is { } active ? Vm.AllColumns.IndexOf(active) : 0;
        if (index >= 0 && index < _columnViews.Count && _columnViews[index].IsVisible)
            _columnViews[index].FocusList();
        else
            _columnViews[0].FocusList();
    }

    /// <summary>マウスの戻る／進むボタンを、ポインタ下のカラムへ渡す。
    /// ウィンドウの PreviewMouseDown はこの View より先に処理されるため、通常のクリック時の
    /// 「カラムをアクティブにする」イベントには任せず、ここで明示的に同期する。</summary>
    public void NavigateHistory(DependencyObject? source, bool back)
    {
        var column = FindColumnView(source)?.DataContext as FilesColumnViewModel;
        if (column is not null)
            Vm?.SetActiveColumn(column);
        Vm?.NavigateHistory(back);
    }

    private static FilesColumnView? FindColumnView(DependencyObject? source)
        => WpfTreeTraversal.FindAncestor<FilesColumnView>(source);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundVm is not null)
            _boundVm.PropertyChanged -= OnVmPropertyChanged;
        _boundVm = Vm;
        if (_boundVm is not null)
            _boundVm.PropertyChanged += OnVmPropertyChanged;
        Rebuild();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilesPaneViewModel.ColumnCount))
            Rebuild();
    }

    private void Rebuild()
    {
        if (Vm is not { } vm)
        {
            FilesPaneLayoutPresenter.Clear(ColumnHost);
            return;
        }

        FilesPaneLayoutPresenter.Rebuild(
            ColumnHost, _columnViews, vm,
            (Brush)FindResource("Border"), (Brush)FindResource("Accent"));
    }
}
