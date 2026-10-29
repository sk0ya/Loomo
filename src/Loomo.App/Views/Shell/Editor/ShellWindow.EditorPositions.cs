namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: タブを閉じたファイルを開き直したら、前回いた行へ戻す（Vim の <c>'"</c>）。
/// 覚えるのは取り消しの履歴（§24.96）と同じく、エディタがファイルを手放すとき——タブを閉じる（棚へ下げるのも含む）・
/// 同じタブへ別のファイルを読む・ウィンドウを閉じる——で、打鍵の道には置かない。戻すのはファイルを開いて読み込んだ直後。
/// 開いたあとに呼び出し元が行へ飛ぶ（定義へ移動・検索結果など）なら、そちらが後から上書きする。
/// </summary>
public partial class ShellWindow
{
    private readonly EditorPositionStore _editorPositions = new(EditorPositionStore.DefaultPath());

    /// <summary>閉じるタブの位置を覚える。</summary>
    private void RememberEditorPosition(EditorTab tab)
    {
        if (CaptureEditorPosition(tab) is { } item)
            _editorPositions.Remember(item.Path, item.Position);
    }

    private void RememberEditorPosition(VimEditorControl control)
    {
        if (CaptureEditorPosition(control) is { } item)
            _editorPositions.Remember(item.Path, item.Position);
    }

    /// <summary>覚える位置。まだ実体化していない（復元後に一度も見ていない）タブは、保存されていた位置をそのまま引き継ぐ。
    /// 未保存の編集があるタブは覚えない——閉じれば捨てる本文の上での行なので、ディスクの本文では別の場所を指す。</summary>
    private (string Path, EditorPosition Position)? CaptureEditorPosition(EditorTab tab)
    {
        if (tab.IsRealized)
            return CaptureEditorPosition(tab.Control);
        if (tab.Pending is not { FilePath: { Length: > 0 } path } pending
            || pending.IsModified || pending.DeferredTextPath is not null || !ShouldTrackEditorPosition(path))
            return null;
        return (path, new EditorPosition(pending.CaretLine, pending.CaretColumn, pending.ScrollRatio));
    }

    private (string Path, EditorPosition Position)? CaptureEditorPosition(VimEditorControl control)
    {
        if (control.FilePath is not { Length: > 0 } path || control.IsModified || !ShouldTrackEditorPosition(path))
            return null;
        return (path, new EditorPosition(control.Caret.Line, control.Caret.Column, control.VerticalScrollRatio));
    }

    /// <summary>
    /// 位置を覚える・戻す対象か。外すのは、開くたびに中身が別物になるファイル:
    /// <list type="bullet">
    /// <item>ライブラリの定義（%TEMP% の読み取り専用の写し）</item>
    /// <item><c>.git</c> の中（<c>COMMIT_EDITMSG</c> 等。毎回作り直されるので、前回の行は今回の文面と関係ない——Vim の
    /// 「前回の位置へ戻る」も gitcommit を外している）</item>
    /// <item>EDITOR として開いたもの（§38.5。git・Claude Code が渡す使い捨ての一時ファイル）</item>
    /// </list>
    /// </summary>
    private bool ShouldTrackEditorPosition(string path)
    {
        if (!Path.IsPathRooted(path) || MetadataSourceFiles.IsMetadataAsSource(path))
            return false;
        if (path.Replace('/', '\\').Split('\\').Any(segment => string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase)))
            return false;
        return !_editWaiters.Keys.Any(waiting => FilePathRelations.AreEqual(waiting, path));
    }

    /// <summary>読み込んだばかりのエディタを前回の位置へ。スクロールはレイアウト後に合わせるが、
    /// それまでに呼び出し元が別の行へ動かしていたら（カーソルが戻した位置に無ければ）触らない。</summary>
    private void RestoreEditorPosition(VimEditorControl control, EditorPosition? position)
    {
        if (position is null || control.FilePath is not { Length: > 0 } path || !ShouldTrackEditorPosition(path))
            return;
        control.NavigateTo(position.Line, position.Column);
        if (position.ScrollRatio is not { } ratio)
            return;
        var caret = control.Caret;
        control.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (string.Equals(control.FilePath, path, StringComparison.OrdinalIgnoreCase)
                && control.Caret.Line == caret.Line && control.Caret.Column == caret.Column)
                control.ScrollToVerticalRatio(Math.Clamp(ratio, 0, 1));
        }), DispatcherPriority.Loaded);
    }

    /// <summary>ウィンドウを閉じる：開いている全部の部屋のエディタの位置をまとめて覚えてから（書き出しは 1 回）、書き込みを書き切る。
    /// 開いたままのタブはワークスペースの復元が戻すが、あとでそのタブを閉じずに別の部屋から開き直す場合のために覚えておく。</summary>
    private void RememberAllEditorPositions()
    {
        var items = _editorTabs.Concat(_editorWorkspaces.Values.SelectMany(w => w.Tabs))
            .Distinct()
            .Select(CaptureEditorPosition)
            .OfType<(string Path, EditorPosition Position)>()
            .ToList();
        _editorPositions.RememberMany(items);
        _editorPositions.Dispose();
    }
}
