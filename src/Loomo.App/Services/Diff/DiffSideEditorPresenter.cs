using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Editor.Controls;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 左右並び差分の本文を、左右2つの <see cref="VimEditorControl"/> で出す。左（旧側）は読み取り専用、
/// 右（新側）は作業ツリーのファイルそのものなら<b>その場で編集して保存できる</b>——ヤンク・検索・
/// テキストオブジェクトといったエディタの操作が差分の上でそのまま使える。
///
/// <para>右は<b>自分でファイルを持たない</b>。Editor ペインのタブの本文を映す仮想文書で、打てば
/// タブにも入り、タブで打てば右にも入る（<see cref="EditorTextMirror"/>）。まだ Editor で開いていない
/// ファイルは、右で打ち始めた時点で Editor にタブを足す。だから未保存の編集は Editor のタブが持ち、
/// Diff を閉じても・別のファイルへ移っても「保存しますか」とは聞かない。右がファイルを持つと、
/// 同じファイルを2つのエディタが別々のバッファで持つことになり、片方の保存がもう片方に届かない。</para>
///
/// <para>行の対応は <see cref="DiffEditorAlignment"/> が決める：片側にしか無い行のぶん反対側へ空き行を
/// 挿すので、行 i はどちらのエディタでも表示行 i に来る。だからスクロールは画素位置をそのまま写せばよく、
/// 中央の帯・次/前の変更も「行の添字 × 行の高さ」で済む。</para>
///
/// <para>右を編集すると、保存を待たずに手元で差分を取り直して（<see cref="DiffEditorAlignment.Recompute"/>）
/// VM の行へ流し込む。行の正本は VM の <see cref="DiffSessionViewModel.SideRows"/> のまま——次/前の変更も
/// 中央の帯も、いま見えている行を数える。保存すると git の読み直しが同じ本文の行を返すので、画面は動かない。</para>
/// </summary>
internal sealed class DiffSideEditorPresenter : IDisposable
{
    /// <summary>打鍵が止まってから差分を取り直すまでの間。打つたびに LCS を回さない。</summary>
    private static readonly TimeSpan RediffDelay = TimeSpan.FromMilliseconds(250);

    private readonly Func<DiffSessionViewModel?> _viewModel;
    private readonly Decorator _leftHost;
    private readonly Decorator _rightHost;
    private readonly VimStatusBar _statusBar;
    private readonly DispatcherTimer _rediffTimer;
    private Func<VimEditorControl> _factory = () => new VimEditorControl(new VimEditorControlOptions());
    private Action<VimEditorControl> _applyAppearance = _ => { };
    private VimEditorControl? _left;
    private VimEditorControl? _right;
    private IDiffWorkingDocuments? _documents;
    /// <summary>右と、右が映している Editor のタブとの本文の同期（まだ映していなければ null）。</summary>
    private EditorTextMirror? _link;
    /// <summary>右へ本文を入れている最中（その BufferChanged を「ユーザーが打った」と取り違えない）。</summary>
    private bool _loadingRight;
    /// <summary>右へ出したときの本文。タブを映していないうちに Editor 側でそのファイルが編集されていたら、
    /// 右で打った1文字でタブを丸ごと置き換えないよう、これと比べて気づく。</summary>
    private string _rightBaseText = "";
    /// <summary>右で打ったのでタブを開いている最中（その読み込みの知らせで右を出し直さない）。</summary>
    private bool _adopting;
    private IReadOnlyList<DiffSideRowVm> _rows = [];
    private IReadOnlyList<string> _leftLines = [];
    private string? _leftLanguageKey;
    private string? _rightLanguageKey;
    /// <summary>右のエディタが編集用に開いているファイル（読み取り専用の表示なら null）。</summary>
    private string? _editablePath;
    private bool _syncingScroll;
    private int _rediffGeneration;
    private bool _disposed;

    internal DiffSideEditorPresenter(
        Func<DiffSessionViewModel?> viewModel, Decorator leftHost, Decorator rightHost, VimStatusBar statusBar)
    {
        _viewModel = viewModel;
        _leftHost = leftHost;
        _rightHost = rightHost;
        _statusBar = statusBar;
        _rediffTimer = new DispatcherTimer(DispatcherPriority.Background, leftHost.Dispatcher) { Interval = RediffDelay };
        _rediffTimer.Tick += async (_, _) =>
        {
            _rediffTimer.Stop();
            await RediffAsync();
        };
    }

    /// <summary>行の位置が動いた（スクロール・装飾の差し替え）。中央の帯を描き直す合図。</summary>
    internal event Action? LayoutChanged;

    /// <summary>左右どちらかの本文で右クリックメニューを組み立てている（ホストが項目を足す）。</summary>
    internal event Action<bool, VimEditorControl, EditorContextMenuBuildingEventArgs>? ContextMenuBuilding;

    /// <summary>ユーザーが本文を触った（自動ジャンプの取り下げに使う）。</summary>
    internal event Action? UserInteracted;

    internal VimEditorControl? Left => _left;
    internal VimEditorControl? Right => _right;

    /// <summary>右が映している文書に、まだ保存していない編集があるか。あるうちは作業ツリーを書き換える操作
    /// （範囲の破棄）を止める——ディスクの行番号で作ったパッチが、見えている本文とずれるため。
    /// まだ映していないタブ（Diff を開いた後に Editor で開いて編集した）も数える。</summary>
    internal bool HasUnsavedEdits
        => _editablePath is { } path && (LiveLink()?.Source ?? _documents?.Find(path))?.IsModified == true;

    /// <summary>
    /// エディタの作り方と見た目の当て方を部屋から受け取る（テーマ・フォント・構文の登録・Vim の有無）。
    /// エディタを作る前に呼ぶ。後から呼ばれたら、今あるエディタに見た目だけ当て直す。
    /// <paramref name="documents"/> が無ければ右も読み取り専用で出す（編集の持ち主が居ない）。
    /// </summary>
    internal void Configure(
        Func<VimEditorControl> factory, Action<VimEditorControl> applyAppearance, IDiffWorkingDocuments? documents)
    {
        _factory = factory;
        _applyAppearance = applyAppearance;
        if (_documents is not null)
        {
            _documents.Events.Loaded -= OnDocumentLoaded;
            _documents.Events.Closed -= OnDocumentClosed;
        }
        _documents = documents;
        if (_documents is not null)
        {
            _documents.Events.Loaded += OnDocumentLoaded;
            _documents.Events.Closed += OnDocumentClosed;
        }
        ReapplyAppearance();
    }

    /// <summary>設定（テーマ・フォント）が変わったので当て直す。</summary>
    internal void ReapplyAppearance()
    {
        if (_left is not null) ApplyAppearance(_left, left: true);
        if (_right is not null) ApplyAppearance(_right, left: false);
    }

    /// <summary>
    /// VM の行（<see cref="DiffSessionViewModel.SideRows"/>）に左右のエディタを合わせる。本文が同じなら
    /// 読み込み直さない（キャレット・スクロール・Undo を守る）。何度呼んでも同じ結果になる。
    /// </summary>
    internal void Sync()
    {
        if (_disposed || _viewModel() is not { } vm) return;
        EnsureEditors();
        var left = _left!;
        var right = _right!;
        var rows = vm.SideRows.ToList();
        var (leftLines, rightLines) = DiffEditorAlignment.SideLines(rows);
        _leftLines = leftLines;
        // 選択ではなく「いま出ている行の出どころ」で決める（選択が移ってから新しい行が届くまでの間に、
        // 前のファイルの本文を次のファイルの鍵で覚えると、次に届いた行で前のキャレットが復元される）。
        var path = vm.SideRowsItem?.FullPath;
        var language = DiffEditorLanguage.For(path);

        var leftText = string.Join("\n", leftLines);
        if (!left.IsVirtualDocument || left.Text != leftText || _leftLanguageKey != path)
        {
            ShowReadOnly(left, vm.SideLeftTitle, leftText, language, keepView: _leftLanguageKey == path);
            _leftLanguageKey = path;
        }

        // 行が1つも無い差分（「差分はありません」の知らせ・バイナリ）は編集させない。開いてしまうと、
        // 空の旧側と比べ直して「全行追加」に化ける。
        var hasLines = rows.Any(row => row.LeftLine.Length > 0 || row.RightLine.Length > 0);
        if (hasLines && _documents is not null && vm.EditableSidePath is { } editablePath)
        {
            // 映していたタブが閉じられた：未保存の編集は Editor 側で保存か破棄が済んでいる。ディスクから出し直す。
            var linkClosed = _link is not null && LiveLink() is null;
            if (!PathEquals(_editablePath, editablePath) || linkClosed)
            {
                _editablePath = Path.GetFullPath(editablePath);
                OpenEditable(right, _editablePath, keepView: linkClosed);
                _rightLanguageKey = null;
            }
            else if (_link is null && _editablePath is { } openedPath
                     && (_documents.Find(openedPath) is not null
                         || !DiffEditorAlignment.SameText(rightLines, right.Text)))
            {
                // まだタブを映していない：その間に Editor でタブが開かれた（その本文を映す）か、
                // 外でディスクが書き換えられた（出し直す）。
                OpenEditable(right, openedPath, keepView: true);
            }
        }
        else
        {
            Unlink();
            _editablePath = null;
            var rightText = string.Join("\n", rightLines);
            if (!right.IsVirtualDocument || right.Text != rightText || _rightLanguageKey != path)
            {
                ShowReadOnly(right, vm.SideRightTitle, rightText, language,
                    keepView: _rightLanguageKey == path && right.IsVirtualDocument);
                _rightLanguageKey = path;
            }
            right.IsReadOnly = true;
        }

        if (_editablePath is not null && !DiffEditorAlignment.SameText(rightLines, right.Text))
        {
            // 保存していない編集がある：ディスクの行ではなく、見えている本文で取り直す。
            ApplyLayout(rows);
            _ = RediffAsync();
            return;
        }
        ApplyLayout(rows);
    }

    /// <summary>行 <paramref name="rowIndex"/> を画面の上から 35% あたりへ出し、左右のキャレットをその行へ置く。</summary>
    internal void ScrollToRow(int rowIndex)
    {
        if (_left is null || _right is null || _rows.Count == 0) return;
        rowIndex = Math.Clamp(rowIndex, 0, _rows.Count - 1);
        _left.NavigateTo(DiffEditorAlignment.LineOfRow(_rows, rowIndex, left: true), 0);
        _right.NavigateTo(DiffEditorAlignment.LineOfRow(_rows, rowIndex, left: false), 0);
        var lineHeight = _left.LineHeight;
        if (lineHeight <= 0) return;
        var target = Math.Max(0, rowIndex * lineHeight - _left.TextViewportHeight * 0.35);
        _left.ScrollToOffset(target, _left.HorizontalOffset);
    }

    /// <summary>中央の帯を置くための縦位置（左エディタ基準：本文の上端・スクロール・行の高さ）。</summary>
    internal (double TextTop, double VerticalOffset, double LineHeight)? Geometry
        => _left is { LineHeight: > 0 } left ? (left.TextAreaTop, left.VerticalOffset, left.LineHeight) : null;

    /// <summary>右クリックされた（＝キャレットのある）行の添字。</summary>
    internal int RowAtCaret(VimEditorControl editor)
        => DiffEditorAlignment.RowOfLine(_rows, editor.Caret.Line, left: ReferenceEquals(editor, _left));

    /// <summary>エディタで選んでいる行の添字の範囲。選択が無ければキャレットの行（<c>HasSelection</c> が false）。
    /// その側に無い行（編集で行がずれた直後など）は -1。</summary>
    internal (int StartRow, int EndRow, bool HasSelection) SelectedRows(VimEditorControl editor)
    {
        var left = ReferenceEquals(editor, _left);
        if (!editor.HasSelection || editor.Selection is not { } selection)
        {
            var row = RowAtCaret(editor);
            return (row, row, false);
        }
        var start = Math.Min(selection.StartLine, selection.EndLine);
        var end = Math.Max(selection.StartLine, selection.EndLine);
        return (DiffEditorAlignment.RowOfLine(_rows, start, left), DiffEditorAlignment.RowOfLine(_rows, end, left), true);
    }

    /// <summary>右が映している文書を保存する（Ctrl+S）。編集用に開いていなければ何もしない。</summary>
    internal bool SaveRight()
    {
        if (_editablePath is null || _right is null) return false;
        _ = SaveAsync();
        return true;
    }

    /// <summary>Editor のタブを、Editor ペインと同じ経路で保存する。まだ打っていなければ（タブを映して
    /// いなければ）保存するものが無い。書けなければ例外を UI スレッドへ投げずにペインへ出す。</summary>
    private async Task SaveAsync()
    {
        if (LiveLink() is not { } link || _documents is null) return;
        try
        {
            // 右の「保存済み」は同期が揃える（EditorTextMirror.OnSaved）。
            await _documents.SaveAsync(link.Source);
        }
        catch (Exception ex)
        {
            _viewModel()?.SetStatusMessage($"保存できませんでした: {ex.Message}", isError: true);
        }
    }

    public void Dispose()
    {
        // 未保存の編集は Editor のタブが持っている。ここで聞くことは何も無い。
        Unlink();
        if (_documents is not null)
        {
            _documents.Events.Loaded -= OnDocumentLoaded;
            _documents.Events.Closed -= OnDocumentClosed;
        }
        _disposed = true;
        _rediffTimer.Stop();
        _left?.Dispose();
        _right?.Dispose();
    }

    private void EnsureEditors()
    {
        if (_left is not null) return;
        _left = CreateEditor(left: true);
        _right = CreateEditor(left: false);
        _left.IsReadOnly = true;
        _right.IsReadOnly = true;
        _right.BufferChanged += (_, _) =>
        {
            if (_editablePath is null || _loadingRight) return;
            EnsureLinkForEdit();
            _rediffTimer.Stop();
            _rediffTimer.Start();
        };
        // 右は仮想文書なので :w は書かずに知らせてくるだけ。映している Editor のタブを保存する。
        _right.SaveRequested += (_, _) =>
        {
            if (_editablePath is not null) _ = SaveAsync();
        };
        _leftHost.Child = _left;
        _rightHost.Child = _right;
    }

    private VimEditorControl CreateEditor(bool left)
    {
        var editor = _factory();
        ApplyAppearance(editor, left);
        editor.SetSharedStatusBar(_statusBar);
        editor.ViewportScrolled += OnViewportScrolled;
        editor.ContextMenuBuilding += (_, e) => ContextMenuBuilding?.Invoke(left, editor, e);
        editor.PreviewMouseDown += (_, _) => UserInteracted?.Invoke();
        editor.PreviewMouseWheel += (_, _) => UserInteracted?.Invoke();
        editor.PreviewKeyDown += (_, _) => UserInteracted?.Invoke();
        return editor;
    }

    /// <summary>
    /// 部屋の見た目を当てたうえで、差分に要る形へ固める。折り返すと1行が複数の表示行になって左右の高さが
    /// ずれるので折り返さない。畳むのも同じ理由で行の揃いを崩すので、行番号の右の折りたたみ印の列も要らない。
    /// ミニマップ・パンくず・インレイヒント・ゴースト補完は差分では幅と行を取るだけ。
    /// 縦スクロールは左右で連動するので、スクロールバーは右の1本で足りる（左は外して本文に回す）。
    /// ステータスバーは Vim のモードとコマンド行のためのもので、Vim を使わないなら仮想文書の名前と
    /// 文字コードしか出ない——左右で共有する1本ごと畳む。
    /// </summary>
    private void ApplyAppearance(VimEditorControl editor, bool left)
    {
        _applyAppearance(editor);
        editor.ExecuteCommand("set nowrap");
        editor.ExecuteCommand("set nominimap");
        editor.ExecuteCommand("set nofoldcolumn");
        editor.ExecuteCommand("set nobreadcrumb");
        editor.ExecuteCommand("set noinlayhints");
        editor.ExecuteCommand("set noinlinesuggest");
        editor.ExecuteCommand(left ? "set noscrollbar" : "set scrollbar");
        _statusBar.Visibility = editor.VimEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>読み取り専用の文書として出す。<paramref name="keepView"/> は同じファイルの差分の読み直し
    /// （保存・リポジトリの変化）で、読んでいた位置とキャレットを残す。</summary>
    private static void ShowReadOnly(
        VimEditorControl editor, string title, string text, string? language, bool keepView)
    {
        var caret = editor.Caret;
        var (vertical, horizontal) = (editor.VerticalOffset, editor.HorizontalOffset);
        editor.OpenVirtualDocument(title, text, language);
        editor.IsReadOnly = true;
        if (keepView)
            RestoreView(editor, caret, vertical, horizontal);
    }

    private static void RestoreView(VimEditorControl editor, CaretInfo caret, double vertical, double horizontal)
    {
        editor.NavigateTo(caret.Line, caret.Column);
        editor.ScrollToOffset(vertical, horizontal);
    }

    /// <summary>
    /// 右へ編集用のファイルを出す。Editor で開いていればそのタブの本文（未保存の編集ごと）を映し、
    /// 開いていなければディスクの本文を出す（タブは打ち始めたときに足す）。どちらも仮想文書として持つ
    /// ——右がファイルを持つと、その監視が Editor 側の保存を「外部の変更」と見て読み直しを聞いてくる。
    /// </summary>
    private void OpenEditable(VimEditorControl right, string path, bool keepView)
    {
        Unlink();
        var caret = right.Caret;
        var (vertical, horizontal) = (right.VerticalOffset, right.HorizontalOffset);
        _loadingRight = true;
        try
        {
            var source = _documents!.Find(path);
            string text;
            if (source is not null)
            {
                text = source.Text;
            }
            else
            {
                // 文字コードの判定はエディタの読み込みに任せる（BOM 無しの Shift_JIS なども同じに読める）。
                right.LoadFile(path);
                if (right.IsModified) right.ExecuteCommand("e!");
                text = right.Text;
            }
            right.OpenVirtualDocument(Path.GetFileName(path), text, DiffEditorLanguage.For(path));
            right.IsReadOnly = false;
            _rightBaseText = right.Text;
            if (source is not null)
                _link = new EditorTextMirror(source, right, _documents.Events);
            if (keepView)
                RestoreView(right, caret, vertical, horizontal);
        }
        finally { _loadingRight = false; }
    }

    /// <summary>右で打った：まだ Editor のタブを映していなければ、ここで開いて（あれば見つけて）本文を渡す。</summary>
    private void EnsureLinkForEdit()
    {
        if (_documents is null || _right is null || _editablePath is null || LiveLink() is not null) return;
        Unlink();
        if (!_right.IsModified) return;   // 打ったのではなく、全部取り消して元に戻った
        var existing = _documents.Find(_editablePath);
        if (existing is not null && !string.Equals(existing.Text, _rightBaseText, StringComparison.Ordinal))
        {
            // 右が知らない本文をタブが持っている（右に出した後で Editor で編集された）。右の本文で置き換えると
            // その編集が消えるので、打った分を諦めてタブの本文を映す。
            OpenEditable(_right, _editablePath, keepView: true);
            _viewModel()?.SetStatusMessage(
                "Editor で編集中の本文に合わせました。もう一度入力してください。", isError: false);
            return;
        }
        VimEditorControl? source;
        _adopting = true;
        try { source = existing ?? _documents.Open(_editablePath); }
        finally { _adopting = false; }
        if (source is null) return;
        _link = new EditorTextMirror(source, _right, _documents.Events);
        EditorTextMirror.ApplyAsEdit(source, _right.Text);
    }

    /// <summary>
    /// タブにディスクから本文が入った（Editor で開いた・ブランチ切替や一括置換で読み直した）。<c>LoadFile</c> は
    /// <c>BufferChanged</c> を出さないので同期では届かない。右をタブの本文で出し直す——出し直さずに右で打つと、
    /// 読み直した本文を右の古い本文で上書きする。
    /// </summary>
    private void OnDocumentLoaded(VimEditorControl control)
    {
        if (_adopting || _disposed || _right is null || _editablePath is not { } path) return;
        if (!PathEquals(control.FilePath, path)) return;
        if (_link is not null && !ReferenceEquals(_link.Source, control)) return;   // 別のビュー（分割）の読み込み
        OpenEditable(_right, path, keepView: true);
        _ = RediffAsync();
    }

    /// <summary>映していたタブが閉じられた。未保存の編集は Editor 側で保存か破棄が済んでいるので、
    /// 右はディスクの本文へ戻す（破棄した編集を右に残さない）。</summary>
    private void OnDocumentClosed(VimEditorControl control)
    {
        if (_disposed || _right is null || _link is null || !ReferenceEquals(_link.Source, control)) return;
        Unlink();
        if (_editablePath is not { } path) return;
        OpenEditable(_right, path, keepView: true);
        _ = RediffAsync();
    }

    /// <summary>映しているタブがまだ開いていればその同期、閉じられていれば null。</summary>
    private EditorTextMirror? LiveLink()
        => _link is { } link && _documents?.IsOpen(link.Source) == true ? link : null;

    private void Unlink()
    {
        _link?.Dispose();
        _link = null;
    }

    private void ApplyLayout(IReadOnlyList<DiffSideRowVm> rows)
    {
        _rows = rows;
        var (left, right) = DiffEditorAlignment.Build(
            rows, _left!.Engine.CurrentBuffer.Text.LineCount, _right!.Engine.CurrentBuffer.Text.LineCount);
        _left.SetDiffDecorations(left.Decorations);
        _right.SetDiffDecorations(right.Decorations);
        // 空き行が変わると片側だけ行が増減する。右を左の位置へ揃え直す。
        Mirror(_left, _right);
        LayoutChanged?.Invoke();
    }

    /// <summary>保存していない右の本文で差分を取り直し、VM の行へ流し込む（→ 行の変化で <see cref="Sync"/> が走る）。</summary>
    private async Task RediffAsync()
    {
        if (_editablePath is null || _right is null || _viewModel() is not { } vm) return;
        var generation = ++_rediffGeneration;
        var item = vm.SideRowsItem;
        var path = _editablePath;
        var leftLines = _leftLines;
        var text = _right.Text;
        var (_, currentRight) = DiffEditorAlignment.SideLines(_rows);
        if (DiffEditorAlignment.SameText(currentRight, text)) return;   // 読み込み・保存で本文は変わっていない
        var rows = await Task.Run(() => DiffEditorAlignment.Recompute(leftLines, text));
        // 取り直している間に別のファイルへ移った（または読み直した）なら捨てる。世代だけでは、
        // 未保存の確認ダイアログの間に割り込んだ継続を止められない。
        if (generation != _rediffGeneration || _disposed
            || !ReferenceEquals(vm.SideRowsItem, item) || !PathEquals(_editablePath, path)
            || _right.Text != text)
            return;
        vm.ApplyLiveSideRows(rows);
    }

    private void OnViewportScrolled(object? sender, EventArgs e)
    {
        if (_syncingScroll || sender is not VimEditorControl source) return;
        var other = ReferenceEquals(source, _left) ? _right : _left;
        if (other is not null) Mirror(source, other);
        LayoutChanged?.Invoke();
    }

    private void Mirror(VimEditorControl source, VimEditorControl target)
    {
        if (Math.Abs(target.VerticalOffset - source.VerticalOffset) < 0.5
            && Math.Abs(target.HorizontalOffset - source.HorizontalOffset) < 0.5)
            return;
        _syncingScroll = true;
        try { target.ScrollToOffset(source.VerticalOffset, source.HorizontalOffset); }
        finally { _syncingScroll = false; }
    }

    private static bool PathEquals(string? a, string? b)
        => a is not null && b is not null
           && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
