using Editor.Core.Buffer;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 抜粋タブ1枚と、元ファイルのタブとの結線（設計書 §35.3）。判断は <see cref="ExcerptDocument"/>（純粋なモデル）が持ち、
/// ここはエディタの差分（<see cref="VimEditorControl.TextEdited"/>）を両方向へ運ぶだけ。
/// <list type="bullet">
/// <item>抜粋タブで打った変更は、元ファイルのタブへ<b>その場で</b>入れる（ファイルの持ち主は引き続きタブ）。
/// 元ファイルが開いていなければ、裏でタブを開いてから入れる（前面には出さない）。</item>
/// <item>元ファイルのタブで抜粋の中が変わったら、抜粋タブへ写す。外への変更はアンカーを動かすだけ。</item>
/// <item>見出し行は <see cref="VimEditorControl.EditGuard"/> で守る。断った編集は差分として流れてこない。</item>
/// </list>
/// 自分で入れた編集の通知は <see cref="_applying"/> で捨てる——元ファイル側はホストの編集を行単位の差分で
/// 報告し直すので、それでアンカーを動かすと抜粋の境界の行の所属が曖昧になる（モデルは正確な編集で動かし済み）。
/// </summary>
internal sealed class ExcerptTabController : IDisposable
{
    private readonly Func<string, VimEditorControl?> _findSource;
    private readonly Func<string, VimEditorControl?> _openSource;
    private readonly Func<string, IReadOnlyList<string>?> _readDisk;
    private readonly Action<string> _showStatus;
    private readonly Dictionary<VimEditorControl, (string Path, EventHandler<EditorTextEditedEventArgs> Handler)> _sources
        = new(ReferenceEqualityComparer.Instance);
    private bool _applying;
    private bool _rebuildScheduled;

    /// <param name="findSource">開いているタブのうち、そのファイルのエディタ（無ければ null。開きはしない）。</param>
    /// <param name="openSource">そのファイルを裏のタブで開いてエディタを返す（開けなければ null）。</param>
    /// <param name="readDisk">ディスク上のそのファイルの全行（読めなければ null）。元タブが閉じられたときの写し直しに使う。</param>
    public ExcerptTabController(
        ExcerptDocument document, VimEditorControl view,
        Func<string, VimEditorControl?> findSource,
        Func<string, VimEditorControl?> openSource,
        Func<string, IReadOnlyList<string>?> readDisk,
        Action<string> showStatus)
    {
        Document = document;
        View = view;
        _findSource = findSource;
        _openSource = openSource;
        _readDisk = readDisk;
        _showStatus = showStatus;
        View.EditGuard = (before, after) => Document.Guard(before, after);
        RefreshGutter();
        View.TextEdited += OnViewEdited;
        AttachOpenSources();
    }

    public ExcerptDocument Document { get; }
    public VimEditorControl View { get; }

    /// <summary>結び付けている元ファイルのエディタ（保存の対象）。</summary>
    public IEnumerable<VimEditorControl> Sources => _sources.Keys;

    /// <summary>開いている元ファイルのタブを結び付け、ずれていれば抜粋タブを写し直す。</summary>
    public void AttachOpenSources()
    {
        foreach (var path in Document.Paths.ToList())
            if (_findSource(path) is { } control)
                AttachAndResync(path, control);
    }

    /// <summary>あとから開かれた（読み直された）元ファイルのタブを結び付ける。関係ないファイルなら何もしない。</summary>
    public void OnSourceLoaded(VimEditorControl control)
    {
        if (control.FilePath is not { Length: > 0 } path ||
            !Document.Paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            return;
        AttachAndResync(path, control);
    }

    /// <summary>
    /// 元ファイルのタブが閉じられた。未保存の変更を捨てて閉じた場合、抜粋に残っているのは捨てられた本文なので、
    /// ディスクの内容で写し直す（写し直さないと、次の打鍵でディスクから開き直した元ファイルへ、捨てた本文の
    /// 行・桁で編集を入れてしまう）。以後の編集は、必要になった時点でまた裏で開く。
    /// </summary>
    public void OnSourceClosed(VimEditorControl control)
    {
        if (!_sources.Remove(control, out var entry)) return;
        control.TextEdited -= entry.Handler;
        if (_readDisk(entry.Path) is { } lines)
            ApplyToView(Document.Resync(entry.Path, lines));
    }

    public void Dispose()
    {
        View.TextEdited -= OnViewEdited;
        View.EditGuard = null;
        View.SetLineNumberLabels(null, 0);
        foreach (var (control, entry) in _sources)
            control.TextEdited -= entry.Handler;
        _sources.Clear();
    }

    private void AttachAndResync(string path, VimEditorControl control)
    {
        Attach(path, control);
        ApplyToView(Document.Resync(path, new BufferLines(control)));
    }

    private void Attach(string path, VimEditorControl control)
    {
        if (_sources.ContainsKey(control)) return;
        EventHandler<EditorTextEditedEventArgs> handler = (_, e) => OnSourceEdited(path, control, e);
        control.TextEdited += handler;
        _sources[control] = (path, handler);
    }

    private void OnViewEdited(object? sender, EditorTextEditedEventArgs e)
    {
        // 読み込み（OpenVirtualDocument）と、ここから入れた写し直しは運ばない。
        if (_applying || !e.IsCurrentBuffer || e.Change.Kind == TextBufferChangeKind.Reload)
            return;

        if (Document.TranslateEdit(e.Change) is not { } edit)
        {
            // ガードをすり抜けた形（undo の行差分が見出しにかかった等）。抜粋タブをモデルから作り直す。
            _showStatus("この編集は元のファイルへ入れられないため、抜粋を作り直しました。");
            ScheduleRebuild();
            return;
        }

        var source = _findSource(edit.Path) ?? _openSource(edit.Path);
        if (source is null)
        {
            _showStatus($"元のファイルを開けないため、編集を入れられませんでした: {edit.Path}");
            ScheduleRebuild();
            return;
        }
        Attach(edit.Path, source);

        bool applied;
        string? error;
        _applying = true;
        try { applied = source.TryApplyLspTextEdits([edit.Edit], expectedVersion: null, out error); }
        finally { _applying = false; }
        if (!applied)
        {
            // モデルは確定していないので、抜粋タブをモデル（＝編集前）へ戻せば元ファイルと揃う。
            _showStatus($"元のファイルへ編集を入れられなかったため、抜粋を元に戻しました: {error}");
            ScheduleRebuild();
            return;
        }
        Document.Commit(edit);
        RefreshGutter();
    }

    private void OnSourceEdited(string path, VimEditorControl control, EditorTextEditedEventArgs e)
    {
        if (_applying || !e.IsCurrentBuffer) return;
        if (!string.Equals(control.FilePath, path, StringComparison.OrdinalIgnoreCase))
        {
            // そのタブで別のファイルを開き直した：もうこの抜粋の元ではない。
            OnSourceClosed(control);
            return;
        }
        // 元ファイルの全文（control.Text）は取らない：抜粋の中で打つたびに文書まるごとの複製になる。
        ApplyToView(Document.SourceChanged(path, e.Change, () => new BufferLines(control)));
    }

    /// <summary>エディタのバッファを行の一覧として見せる（1行ずつ O(1) で引ける。全文を組み立てない）。</summary>
    private sealed class BufferLines(VimEditorControl control) : IReadOnlyList<string>
    {
        private readonly TextBuffer _buffer = control.Engine.CurrentBuffer.Text;
        public int Count => _buffer.LineCount;
        public string this[int index] => _buffer.GetLine(index);
        public IEnumerator<string> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return _buffer.GetLine(i);
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>元ファイル側の変化を抜粋タブへ写す。写した編集は抜粋タブの undo に残さない——残すと、
    /// undo で古い本文に戻したものが元ファイルへ流れ込む（元ファイル側の undo は生きている）。</summary>
    private void ApplyToView(IReadOnlyList<ExcerptViewEdit> edits)
    {
        if (edits.Count == 0) return;
        WithoutGuard(() =>
        {
            if (!View.TryApplyLspTextEdits(edits.Select(e => e.Edit).ToList(), expectedVersion: null, out _))
                View.TryRestoreWorkspaceText(Document.Text, out _);
            View.Engine.CurrentBuffer.Undo.Clear();
        });
        RefreshGutter();
    }

    /// <summary>抜粋タブをモデルの本文へ戻す。抜粋タブ自身の差分通知の最中には書き換えられないので後で行う。</summary>
    private void ScheduleRebuild()
    {
        if (_rebuildScheduled) return;
        _rebuildScheduled = true;
        View.Dispatcher.BeginInvoke(() =>
        {
            _rebuildScheduled = false;
            WithoutGuard(() => View.TryRestoreWorkspaceText(Document.Text, out _));
            RefreshGutter();
        });
    }

    private void WithoutGuard(Action action)
    {
        var guard = View.EditGuard;
        _applying = true;
        View.EditGuard = null;
        try { action(); }
        finally
        {
            View.EditGuard = guard;
            _applying = false;
        }
    }

    private void RefreshGutter() => View.SetLineNumberLabels(Document.LineLabel, Document.LabelWidth);
}
