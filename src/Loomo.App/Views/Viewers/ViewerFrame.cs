namespace sk0ya.Loomo.App.Views;

/// <summary>
/// 切り離した窓に載せる、Viewer のタブ 1 つぶんの入れ物（§44.2）。中身の置き場と、中身を出せないときのカードを重ねる。
/// タブは <see cref="IViewerTab.Rehost"/> でここへ移り、戻すときは <see cref="Release"/> で手放す（捨てない）。
/// </summary>
internal sealed class ViewerFrame : Grid, IDisposable
{
    private readonly Grid _contentHost = new();
    private readonly Border _card = new() { Visibility = Visibility.Collapsed };
    private readonly ViewerTabFactory _factory;
    private IViewerTab? _tab;

    public ViewerFrame(IViewerTab tab, ViewerTabFactory factory)
    {
        _factory = factory;
        SetResourceReference(BackgroundProperty, "Panel");
        _card.SetResourceReference(Border.BackgroundProperty, "Panel");
        Children.Add(_contentHost);
        Children.Add(_card);
        Hold(tab);
        tab.Rehost(_contentHost);
        tab.SetVisible(true);
        if (tab.Status == ViewerTabStatus.Pending)
            _ = tab.RefreshAsync();
        RenderCard();
    }

    public IViewerTab? Tab => _tab;

    private void Hold(IViewerTab tab)
    {
        _tab = tab;
        tab.Changed += OnTabChanged;
        tab.CloseRequested += OnCloseRequested;
        if (tab is ViewerGeneratingTab generating)
            generating.Completed += OnGenerationCompleted;
        if (tab is ViewerTabBase based)
            based.GenerateRequested += OnGenerateRequested;
    }

    private void OnGenerateRequested(object? sender, EventArgs e)
    {
        if (sender is not IViewerTab { TargetPath: { } path } tab || !ReferenceEquals(tab, _tab))
            return;
        Release()?.Dispose();
        var generating = _factory.Generate(path, _contentHost);
        Hold(generating);
        generating.SetVisible(true);
        RenderCard();
        TitleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>作り終えた：その Viewer でそのファイルを開いたタブに差し替える。</summary>
    private void OnGenerationCompleted(object? sender, EventArgs e)
    {
        if (sender is not ViewerGeneratingTab generating || !ReferenceEquals(generating, _tab) || generating.TargetPath is not { } path)
            return;
        Release()?.Dispose();
        var replacement = _factory.ForViewer(generating.ViewerId, _contentHost, path);
        Hold(replacement);
        replacement.SetVisible(true);
        _ = replacement.RefreshAsync();
        RenderCard();
        TitleChanged?.Invoke(this, EventArgs.Empty);
    }
    public Panel ContentHost => _contentHost;

    /// <summary>タイトルが変わった（窓のタブの見出しへ）。</summary>
    public event EventHandler? TitleChanged;

    /// <summary>タブの中から「閉じる」を押した（窓のタブを閉じる）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>別の窓へ移された：中身を作り直す（WebView2 は窓をまたぐと空表示になる）。</summary>
    public void Rebuild()
    {
        if (_tab is null)
            return;
        _tab.Rehost(_contentHost);
        _tab.SetVisible(true);
    }

    /// <summary>タブを手放す（ペインへ戻すとき）。以後この入れ物は空。</summary>
    public IViewerTab? Release()
    {
        var tab = _tab;
        if (tab is null)
            return null;
        tab.Changed -= OnTabChanged;
        tab.CloseRequested -= OnCloseRequested;
        if (tab is ViewerGeneratingTab generating)
            generating.Completed -= OnGenerationCompleted;
        if (tab is ViewerTabBase based)
            based.GenerateRequested -= OnGenerateRequested;
        tab.SetVisible(false);
        _tab = null;
        _card.Child = null;
        return tab;
    }

    private void OnTabChanged(object? sender, EventArgs e)
    {
        RenderCard();
        TitleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCloseRequested(object? sender, EventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void RenderCard()
    {
        var content = _tab?.BuildCard();
        _card.Child = content is null ? null : new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _card.Visibility = content is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Dispose()
    {
        var tab = Release();
        tab?.Dispose();
    }
}
