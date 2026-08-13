namespace sk0ya.Loomo.App.Views;

/// <summary>ファイル一覧ペインの容れ物。<see cref="FilesColumnView"/> を 1／2／4 枚並べる。
///
/// <para>カラムの View は<b>作り直さず使い回す</b>（VM が常に4つ持っているのと対）。1↔4 を往復する
/// たびに作り直すと、スクロール位置・選択・ListBox の実体化がその都度失われ、「戻ってきたら
/// さっきの場所」という約束（§24.4）が画面側で破れる。ここでは親から外して並べ直すだけ。</para></summary>
public partial class FilesPaneView : UserControl
{
    private const double SplitterThickness = 4;

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
        ColumnHost.Children.Clear();
        ColumnHost.ColumnDefinitions.Clear();
        ColumnHost.RowDefinitions.Clear();
        if (Vm is null)
            return;

        var count = Math.Clamp(Vm.ColumnCount, 1, FilesPaneViewModel.MaxColumns);
        var columns = count >= 2 ? 2 : 1;
        var rows = count == 4 ? 2 : 1;

        for (var c = 0; c < columns; c++)
        {
            if (c > 0)
                ColumnHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SplitterThickness) });
            ColumnHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
        }
        for (var r = 0; r < rows; r++)
        {
            if (r > 0)
                ColumnHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SplitterThickness) });
            ColumnHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
        }

        for (var i = 0; i < _columnViews.Count; i++)
        {
            var view = _columnViews[i];
            view.DataContext = i < Vm.AllColumns.Count ? Vm.AllColumns[i] : null;
            if (i >= count)
                continue;
            Grid.SetColumn(view, (i % columns) * 2);
            Grid.SetRow(view, (i / columns) * 2);
            ColumnHost.Children.Add(view);
        }

        var border = (Brush)FindResource("Border");
        if (columns == 2)
            for (var r = 0; r < rows; r++)
                ColumnHost.Children.Add(NewSplitter(border, column: 1, row: r * 2, vertical: true));
        if (rows == 2)
            ColumnHost.Children.Add(NewSplitter(border, column: 0, row: 1, vertical: false, span: columns * 2 - 1));
    }

    private GridSplitter NewSplitter(Brush border, int column, int row, bool vertical, int span = 1)
    {
        var accent = (Brush)FindResource("Accent");
        var splitter = new GridSplitter
        {
            Background = border,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = vertical ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Cursor = vertical ? Cursors.SizeWE : Cursors.SizeNS,
            ToolTip = "ドラッグでカラムの幅を変える",
        };
        splitter.MouseEnter += (_, _) => splitter.Background = accent;
        splitter.MouseLeave += (_, _) => splitter.Background = border;
        Grid.SetColumn(splitter, column);
        Grid.SetRow(splitter, row);
        if (!vertical)
            Grid.SetColumnSpan(splitter, Math.Max(1, span));
        return splitter;
    }
}
