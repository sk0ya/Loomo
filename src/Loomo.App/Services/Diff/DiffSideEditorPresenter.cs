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

    /// <summary>右のエディタに、まだ保存していない編集があるか。あるうちは作業ツリーを書き換える操作
    /// （範囲の破棄）を止める——ディスクの行番号で作ったパッチが、見えている本文とずれるため。</summary>
    internal bool HasUnsavedEdits => _editablePath is not null && _right?.IsModified == true;

    /// <summary>
    /// エディタの作り方と見た目の当て方を部屋から受け取る（テーマ・フォント・構文の登録・Vim の有無）。
    /// エディタを作る前に呼ぶ。後から呼ばれたら、今あるエディタに見た目だけ当て直す。
    /// </summary>
    internal void Configure(Func<VimEditorControl> factory, Action<VimEditorControl> applyAppearance)
    {
        _factory = factory;
        _applyAppearance = applyAppearance;
        ReapplyAppearance();
    }

    /// <summary>設定（テーマ・フォント）が変わったので当て直す。</summary>
    internal void ReapplyAppearance()
    {
        if (_left is not null) ApplyAppearance(_left);
        if (_right is not null) ApplyAppearance(_right);
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
        if (hasLines && vm.EditableSidePath is { } editablePath)
        {
            if (!PathEquals(_editablePath, editablePath) || !PathEquals(right.FilePath, editablePath))
            {
                ConfirmLeavingEditedFile();
                right.IsReadOnly = false;
                right.LoadFile(editablePath);
                // 前に開いたことのあるファイルは、エディタが持っていたバッファ（そのときの本文）が出る。
                // 編集が残っていなければディスクから読み直す。以後の外部変更はエディタの監視が読み直す。
                if (!right.IsModified)
                    right.ExecuteCommand("e!");
                _editablePath = editablePath;
                _rightLanguageKey = null;
            }
        }
        else
        {
            ConfirmLeavingEditedFile();
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

    /// <summary>右のエディタを保存する（Ctrl+S）。編集用に開いていなければ何もしない。</summary>
    internal bool SaveRight()
    {
        if (_editablePath is null || _right is null) return false;
        TrySaveRight();
        return true;
    }

    /// <summary>保存する。読み取り専用・ロック中などで書けなければ、例外を UI スレッドへ投げずにペインへ出す。</summary>
    private bool TrySaveRight()
    {
        try
        {
            _right!.Save();
            return true;
        }
        catch (Exception ex)
        {
            _viewModel()?.SetStatusMessage($"保存できませんでした: {ex.Message}", isError: true);
            return false;
        }
    }

    public void Dispose()
    {
        // ペイン・切り離しウィンドウを閉じるときも、右で編集した内容を黙って捨てない。
        if (!_disposed)
        {
            try { ConfirmLeavingEditedFile(); }
            catch { /* 終了処理の途中でダイアログが出せないこともある。落とさない。 */ }
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
            if (_editablePath is null) return;
            _rediffTimer.Stop();
            _rediffTimer.Start();
        };
        _leftHost.Child = _left;
        _rightHost.Child = _right;
    }

    private VimEditorControl CreateEditor(bool left)
    {
        var editor = _factory();
        ApplyAppearance(editor);
        editor.SetSharedStatusBar(_statusBar);
        editor.ViewportScrolled += OnViewportScrolled;
        editor.ContextMenuBuilding += (_, e) => ContextMenuBuilding?.Invoke(left, editor, e);
        editor.PreviewMouseDown += (_, _) => UserInteracted?.Invoke();
        editor.PreviewMouseWheel += (_, _) => UserInteracted?.Invoke();
        editor.PreviewKeyDown += (_, _) => UserInteracted?.Invoke();
        return editor;
    }

    /// <summary>
    /// 部屋の見た目を当てたうえで、左右の行を揃えるのに要る設定だけは固定する：折り返すと1行が
    /// 複数の表示行になって左右の高さがずれるので折り返さない。ミニマップは差分では幅を取るだけ。
    /// </summary>
    private void ApplyAppearance(VimEditorControl editor)
    {
        _applyAppearance(editor);
        editor.ExecuteCommand("set nowrap");
        editor.ExecuteCommand("set nominimap");
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
    /// 右で編集していたファイルから離れる前に、未保存の編集をどうするか聞く。取り消しは用意しない
    /// ——離れる原因（一覧の選択・リポジトリの変化）はもう起きていて、戻す先が無い。
    /// </summary>
    private void ConfirmLeavingEditedFile()
    {
        if (!HasUnsavedEdits) return;
        var name = Path.GetFileName(_editablePath);
        var answer = MessageBox.Show(
            Application.Current?.MainWindow!,
            $"{name} の差分で編集した内容が保存されていません。保存しますか？\n「いいえ」を選ぶと編集は破棄されます。",
            "未保存の編集", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            TrySaveRight();
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
