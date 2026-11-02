namespace sk0ya.Loomo.App.Views;
/// <summary>
/// ShellWindow: Editor ペインのアウトライン（右端の見出し／宣言の一覧・クリックでその行へ）。
/// Markdown は本文の見出しから、それ以外は見ているタブの言語サーバーの documentSymbol から組む。
/// 閉じている間は何もしない（打鍵の経路に本文の写しを足さない・§31.15）。
/// </summary>
public partial class ShellWindow {
    private DispatcherTimer? _editorOutlineTimer;
    /// <summary>組み立ての世代。言語サーバーを待つ間にタブや本文が変わったら古い結果を捨てる。</summary>
    private int _editorOutlineSeq;
    /// <summary>言語サーバーの準備待ちの再試行回数（ファイルが変わると 0 に戻す）。</summary>
    private int _editorOutlineRetries;
    private const int EditorOutlineMaxRetries = 30;   // 1 秒おき

    private bool IsEditorOutlineVisible => _settings.Appearance.EditorOutlineVisible;

    private void InitializeEditorOutline() => ApplyEditorOutlineVisibility();

    private void OnToggleEditorOutline(object sender, RoutedEventArgs e) {
        _settings.Appearance.EditorOutlineVisible = EditorOutlineToggle.IsChecked == true;
        _vm.Appearance.SaveOutsideOverlay();
        ApplyEditorOutlineVisibility();
    }

    private void ApplyEditorOutlineVisibility() {
        var visible = IsEditorOutlineVisible;
        EditorOutlineToggle.IsChecked = visible;
        EditorOutlineHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) {
            _editorOutlineRetries = 0;
            ScheduleEditorOutlineUpdate(TimeSpan.Zero);
        } else {
            _editorOutlineTimer?.Stop();
            _editorOutlineSeq++;
            EditorOutlineList.ItemsSource = null;
        }
    }

    /// <summary>見ているファイルが変わった（タブ切替・読み込み・改名）。</summary>
    private void OnEditorOutlineFileChanged() {
        if (!IsEditorOutlineVisible)
            return;
        _editorOutlineRetries = 0;
        ScheduleEditorOutlineUpdate(TimeSpan.Zero);
    }

    /// <summary>本文が変わった。見ているタブのぶんだけ、打鍵が落ち着いてから組み直す。</summary>
    private void OnEditorOutlineBufferChanged(VimEditorControl control) {
        if (IsEditorOutlineVisible && ReferenceEquals(_activeEditorTab?.Control, control))
            ScheduleEditorOutlineUpdate(TimeSpan.FromMilliseconds(400));
    }

    private void ScheduleEditorOutlineUpdate(TimeSpan delay) {
        if (_editorOutlineTimer is null) {
            _editorOutlineTimer = new DispatcherTimer(DispatcherPriority.Background);
            _editorOutlineTimer.Tick += async (s, _) => {
                ((DispatcherTimer)s!).Stop();
                await UpdateEditorOutlineAsync();
            };
        }
        _editorOutlineTimer.Stop();
        _editorOutlineTimer.Interval = delay;
        _editorOutlineTimer.Start();
    }

    private async Task UpdateEditorOutlineAsync() {
        if (!IsEditorOutlineVisible)
            return;
        var seq = ++_editorOutlineSeq;
        if (_activeEditorTab is not { IsRealized: true } tab || tab.Control.FilePath is not { Length: > 0 } path) {
            ShowEditorOutline(Array.Empty<EditorOutlineEntry>(), "");
            return;
        }
        var control = tab.Control;

        if (EditorOutline.IsMarkdown(path)) {
            ShowEditorOutline(EditorOutline.FromMarkdown(CodeEditorSupportAnalysis.SplitLines(control.Text)),
                "（見出しがありません）");
            return;
        }
        if (control.LspDocument is not { } document) {
            ShowEditorOutline(Array.Empty<EditorOutlineEntry>(), "（このファイルの言語サーバーがありません）");
            return;
        }
        if (!document.IsReady) {
            RetryOrGiveUp("（言語サーバーが準備できませんでした）");
            return;
        }
        var symbols = await CodeEditorSupportAnalysis.RequestDocumentSymbolsSafeAsync(document, CancellationToken.None);
        if (seq != _editorOutlineSeq || !ReferenceEquals(_activeEditorTab?.Control, control))
            return;   // 待っている間にタブか本文が変わった
        if (symbols.Symbols.Count == 0) {
            // 起動直後の言語サーバーはプロジェクトを読み終えるまで空を返すので、少し待って取り直す。
            RetryOrGiveUp(symbols.TimedOut ? "（言語サーバーが応答しませんでした）" : "（宣言がありません）");
            return;
        }
        _editorOutlineRetries = 0;
        ShowEditorOutline(EditorOutline.FromSymbols(symbols.Symbols), "");

        void RetryOrGiveUp(string message) {
            if (_editorOutlineRetries++ < EditorOutlineMaxRetries) {
                if (EditorOutlineList.Items.Count == 0)
                    EditorOutlineEmpty.Text = "（言語サーバーの準備を待っています…）";
                ScheduleEditorOutlineUpdate(TimeSpan.FromSeconds(1));
                return;
            }
            ShowEditorOutline(Array.Empty<EditorOutlineEntry>(), message);
        }
    }

    private void ShowEditorOutline(IReadOnlyList<EditorOutlineEntry> entries, string emptyText) {
        EditorOutlineList.ItemsSource = entries;
        EditorOutlineEmpty.Text = entries.Count == 0 ? emptyText : "";
    }

    /// <summary>一覧の行をクリック：その見出し／宣言へ飛ぶ（その行をエディタの最上段へ）。</summary>
    private void OnEditorOutlineClick(object sender, MouseButtonEventArgs e) {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not EditorOutlineEntry entry
            || _activeEditorTab is not { IsRealized: true } tab)
            return;
        var control = tab.Control;
        control.NavigateTo(entry.Line0, entry.Column0);
        control.ScrollCursorToTop();
        control.Focus();
        _focusedRegion = PaneFocusTarget.Of(PaneKind.Editor);
    }
}
