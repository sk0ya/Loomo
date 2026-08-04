namespace sk0ya.Loomo.App.Views;

/// <summary>EditorSupport の追従元、ピン留め、ファイル履歴を所有する機能コントローラー。</summary>
internal sealed class EditorSupportController
{
    private FrameworkElement? _visual;
    private DispatcherTimer? _caretTimer;
    private DispatcherTimer? _readyTimer;

    internal EditorSupportController()
    {
        WebView = null!;
        Visuals = new EditorSupportVisualHost();
    }
    public EditorSupportController(
        EditorSupportWebViewController webView,
        EventHandler<EditorSupportContentEdited> contentEdited)
    {
        WebView = webView;
        Visuals = new EditorSupportVisualHost(contentEdited);
    }

    public EditorSupportWebViewController WebView { get; }

    /// <summary>このペインが持つビジュアル表示インスタンス群（提供者ごとに1つ）。</summary>
    public EditorSupportVisualHost Visuals { get; }
    public EditorSupportPipeline Pipeline { get; } = new();
    public EditorTab? Source { get; private set; }
    public bool IsPinned { get; set; }
    public bool IsNavigating { get; set; }
    public EditorSupportHistory History { get; } = new();
    public IReadOnlyList<OutlineNode>? OutlineRoots { get; private set; }
    public EditorTab? OutlineSource { get; private set; }
    public LspRange? CurrentSymbolRange { get; set; }
    public (int Line, int Col)? CurrentCaret { get; set; }
    public int ReadyAttempts { get; private set; }
    public Stopwatch? DiagnosticStopwatch { get; set; }
    public CodeOutlineView? OutlineView { get; set; }

    public void SetOutline(EditorTab source, IReadOnlyList<OutlineNode> roots)
    {
        OutlineSource = source;
        OutlineRoots = roots;
    }

    public void ClearOutline()
    {
        OutlineRoots = null;
        OutlineSource = null;
        CurrentSymbolRange = null;
        CurrentCaret = null;
    }

    public bool ShouldRefreshCallPanels(EditorTab source, CaretInfo caret)
    {
        if (OutlineRoots is null || !ReferenceEquals(OutlineSource, source))
            return false;
        if (CurrentSymbolRange is { } range
            && CodeEditorSupportAnalysis.CaretInRange(range, caret.Line, caret.Column))
            return false;
        return CurrentSymbolRange is not null
            || CurrentCaret is not { } previous
            || previous.Line != caret.Line
            || previous.Col != caret.Column;
    }

    /// <summary>キャレット移動をデバウンスして再描画要求へ変える。要求を投げるだけで自分では描かない。</summary>
    public void ScheduleCaretRefresh(Action requestRefresh)
    {
        if (OutlineRoots is null)
            return;
        if (_caretTimer is null)
        {
            _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _caretTimer.Tick += (_, _) =>
            {
                _caretTimer.Stop();
                requestRefresh();
            };
        }
        _caretTimer.Stop();
        _caretTimer.Start();
    }

    public void ScheduleReadyRetry(TimeSpan interval, EventHandler tick)
    {
        if (_readyTimer is null)
        {
            _readyTimer = new DispatcherTimer { Interval = interval };
            _readyTimer.Tick += tick;
        }
        if (!_readyTimer.IsEnabled)
        {
            ReadyAttempts = 0;
            _readyTimer.Start();
        }
    }

    public int AdvanceReadyAttempt() => ++ReadyAttempts;

    public void StopReadyRetry()
    {
        _readyTimer?.Stop();
        ReadyAttempts = 0;
    }

    /// <summary>
    /// ビジュアル提供者の中身を先に整える。<b>ホストへは載せない</b>——載せてから読み込みを待つと、
    /// その間ペインには新しいファイルの題名で前のファイルの中身が出てしまう。
    /// 戻り値は UI へ反映する関数で、<see cref="MountVisual"/> の直後に同期で呼ぶ。
    /// </summary>
    public async Task<(IEditorSupportVisual Visual, Action Apply)> PrepareVisualAsync(
        IEditorSupportVisualProvider provider,
        string filePath,
        string text,
        CancellationToken ct)
    {
        var visual = Visuals.GetOrCreate(provider);
        var apply = await visual.PrepareAsync(
            filePath, provider.UsesEditorText ? text : string.Empty, ct);
        return (visual, apply);
    }

    /// <summary>整え終わった表示インスタンスをペインへ載せる（同期・フレーム適用の一部）。</summary>
    public void MountVisual(Panel host, IEditorSupportVisual visual)
        => ShowVisual(host, visual.View);

    public void ShowVisual(Panel host, FrameworkElement view)
    {
        if (!ReferenceEquals(_visual, view))
        {
            if (_visual is not null)
                host.Children.Remove(_visual);
            host.Children.Add(view);
            _visual = view;
        }
        view.Visibility = Visibility.Visible;
        if (WebView.View is not null)
            WebView.View.Visibility = Visibility.Collapsed;
    }

    public void ShowWebView()
    {
        if (_visual is not null)
            _visual.Visibility = Visibility.Collapsed;
        if (WebView.View is not null)
            WebView.View.Visibility = Visibility.Visible;
    }

    public bool TryChangeSource(EditorTab source, bool force, out EditorTab? previous)
    {
        previous = Source;
        if (ReferenceEquals(Source, source))
            return false;
        if (IsPinned && !force && Source is not null)
            return false;
        Source = source;
        if (!IsNavigating)
            History.Navigate(source.PeekFilePath);
        return true;
    }

    public EditorTab? DetachSource()
    {
        var previous = Source;
        Source = null;
        return previous;
    }

    public async Task NavigateHistoryAsync(
        bool back,
        IReadOnlyList<EditorTab> openTabs,
        Action<EditorTab> activate,
        Func<string, Task> openFile)
    {
        IsNavigating = true;
        try
        {
            while ((back ? History.GoBack() : History.GoForward()) is { } path)
            {
                var open = openTabs.FirstOrDefault(tab =>
                    string.Equals(tab.PeekFilePath, path, StringComparison.OrdinalIgnoreCase));
                if (open is not null)
                {
                    activate(open);
                    return;
                }
                if (File.Exists(path))
                {
                    await openFile(path);
                    return;
                }
            }
        }
        finally
        {
            IsNavigating = false;
        }
    }

    public void Reset()
    {
        Source = null;
        IsPinned = false;
        IsNavigating = false;
    }

}
