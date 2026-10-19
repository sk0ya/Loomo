namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: 開いているエディタのファイルがエディタの外で書き換わったときの追従（§24.57）。
/// <para>
/// 見張るのはいまのワークスペースの実体化済みタブと、切り離した単独のエディタ（<see cref="DetachKind.EditorMove"/>）。
/// ファイルを1つずつ <see cref="EditorFileWatchSet"/> で見張り、変わったら未保存の編集が無ければ黙って読み直し、
/// あればエディタ上端のバーで選んでもらう。Editor 自身の監視は <c>WatchFileOnDisk = false</c> で切ってある。
/// </para>
/// <para>
/// 読み直しの入口はすべて <see cref="CheckEditorAgainstDiskAsync"/>——監視の通知も、開き直し・Git の変化・
/// 一括置換・Claude のターン終わり（<c>ReloadExistingTabIfChangedAsync</c>）も同じ判定を通る。
/// 「外で変わったか」はディスクのバイト列の指紋で決める（<see cref="ExternalFileChangePolicy"/>）。指紋は
/// <b>コントロールごと</b>に読み込み・保存のたびに覚えるので、裏のワークスペースに回っていた間も残り、
/// 戻ってきたとき（<see cref="OnEditorWorkspaceAttached"/>）の確かめにもそのまま使える。
/// </para>
/// </summary>
public partial class ShellWindow {
    private EditorFileWatchSet? _editorFileWatches;
    private bool _externalFileChangesHooked;
    private bool _editorFileWatchSyncPending;
    /// <summary>Loomo が最後に読み込んだ／保存したときのディスクの指紋。計算は背景なので、判定はこれを待つ
    /// ——保存直後に Git の変化で確かめに来ても、古い指紋と比べて「外で変わった」と取り違えない。</summary>
    private readonly ConditionalWeakTable<VimEditorControl, Task<string?>> _diskFingerprints = new();
    /// <summary>未保存の編集を抱えたまま外で書き換わったエディタ。保存かバーの操作まで残る。
    /// コントロールに結ぶので、別のワークスペースのタブを閉じても消えない。</summary>
    private readonly ConditionalWeakTable<VimEditorControl, object> _externallyChangedDirty = new();

    private void HookExternalFileChanges() {
        if (_externalFileChangesHooked)
            return;
        _externalFileChangesHooked = true;
        _editorFileWatches = new EditorFileWatchSet(path => _ = OnEditorFileChangedOnDiskAsync(path));
        _editorDocumentEvents.Loaded += control => {
            RememberDiskFingerprint(control);
            ScheduleEditorFileWatchSync();
        };
        _editorDocumentEvents.Saved += control => {
            // 同じファイルを映している他のエディタ（複製から保存した等）も、いまのディスクが基準になる。
            RememberDiskFingerprint(control);
            foreach (var (other, _) in WatchedEditors())
                if (!ReferenceEquals(other, control) && FilePathRelations.AreEqual(other.FilePath, control.FilePath))
                    RememberDiskFingerprint(other);
            // 保存した＝人が「この本文で上書きする」と選んだ。競合の印は役目を終える。
            ClearExternalChangeConflict(control.FilePath);
            ScheduleEditorFileWatchSync();   // 名前を付けて保存でパスが変わる
        };
        _editorDocumentEvents.Closed += control => {
            _externallyChangedDirty.Remove(control);
            ScheduleEditorFileWatchSync();
            UpdateExternalChangeBar();
        };
        var bar = _vm.ExternalFileChange;
        bar.ReloadRequested += () => _ = ReloadActiveTabFromDiskAsync();
        bar.CompareRequested += CompareActiveTabWithDisk;
        bar.KeepRequested += () => {
            if (_activeEditorTab is { IsRealized: true } tab)
                _externallyChangedDirty.Remove(tab.Control);
            UpdateExternalChangeBar();
        };
    }

    /// <summary>見張る相手：いまのワークスペースの実体化済みタブと、切り離した単独のエディタ。
    /// 複製（<see cref="DetachKind.EditorMirror"/>）は追従元のタブが見張るので入れない。</summary>
    private List<(VimEditorControl Control, EditorTab? Tab)> WatchedEditors() {
        var list = new List<(VimEditorControl, EditorTab?)>();
        foreach (var tab in _editorTabs)
            if (tab.IsRealized && !tab.Control.IsVirtualDocument)
                list.Add((tab.Control, tab));
        if (_detached is { } detached)
            foreach (var item in detached.AllItems)
                if (item.Kind == DetachKind.EditorMove && item.Content is VimEditorControl { IsVirtualDocument: false } control
                    && !list.Any(e => ReferenceEquals(e.Item1, control)))
                    list.Add((control, null));
        return list;
    }

    /// <summary>見張りをいまの相手に合わせる。冪等で、張り替えは差分だけ。</summary>
    private void SyncEditorFileWatches() {
        _editorFileWatchSyncPending = false;
        _editorFileWatches?.Sync(WatchedEditors().Select(e => e.Control.FilePath));
    }

    /// <summary>今の処理が終わってから張り替える。切り離しの窓は読み込みの<b>後</b>に登録されるので、
    /// 読み込みの通知の中で数えるとまだ居ない。</summary>
    private void ScheduleEditorFileWatchSync() {
        if (_editorFileWatchSyncPending)
            return;
        _editorFileWatchSyncPending = true;
        Dispatcher.BeginInvoke(SyncEditorFileWatches, DispatcherPriority.Background);
    }

    /// <summary>ワークスペースを切り替えて戻ってきた：見張りを張り替え、居なかった間の外部変更を拾う
    /// （未保存のタブも確かめる——指紋はコントロールと一緒に残っているので競合として印が付く）。</summary>
    private void OnEditorWorkspaceAttached() {
        SyncEditorFileWatches();
        _ = RefreshOpenEditorTabsFromDiskAsync();
    }

    private void RememberDiskFingerprint(VimEditorControl control) {
        if (control.IsVirtualDocument || control.FilePath is not { Length: > 0 } path) {
            _diskFingerprints.Remove(control);
            return;
        }
        _diskFingerprints.AddOrUpdate(control, Task.Run(() => ExternalFileChangePolicy.Fingerprint(path)));
    }

    private async Task<string?> KnownDiskFingerprintAsync(VimEditorControl control)
        => _diskFingerprints.TryGetValue(control, out var known) ? await known : null;

    /// <summary>見張っているファイルが変わった（UI スレッド・デバウンス済み）。</summary>
    private async Task OnEditorFileChangedOnDiskAsync(string changedPath) {
        if (_workspaceTransition.IsSwitching)
            return;   // 切替が落ち着いたら OnEditorWorkspaceAttached がまとめて確かめる
        foreach (var (control, tab) in WatchedEditors())
            if (FilePathRelations.AreEqual(control.FilePath, changedPath))
                await CheckEditorAgainstDiskAsync(control, tab);
    }

    /// <summary>
    /// 1つのエディタをディスクと突き合わせる。<b>外部変更の読み直しはすべてここを通る</b>。
    /// 指紋が覚えたものと同じなら何もしない（自分の保存・中身の変わらない書き込み）。違えば、未保存の編集が
    /// 無ければ読み直し、あれば競合の印を付ける。指紋で比べるので、Shift_JIS 等の文書が本文比較で
    /// 「いつも違う」と見えて読み直され続けることもない。
    /// </summary>
    private async Task CheckEditorAgainstDiskAsync(VimEditorControl control, EditorTab? tab) {
        if (control.IsVirtualDocument || control.FilePath is not { Length: > 0 } path)
            return;
        var known = await KnownDiskFingerprintAsync(control);
        var current = await Task.Run(() => ExternalFileChangePolicy.Fingerprint(path));
        if (!IsStillWatched(control, tab, path))
            return;
        if (known is null) {
            // 基準が無い（読み込みの指紋を取り損ねた）：比べようがないので、いまを基準にするだけ。
            if (current is not null)
                _diskFingerprints.AddOrUpdate(control, Task.FromResult<string?>(current));
            return;
        }
        switch (ExternalFileChangePolicy.Decide(control.IsModified, known, current)) {
            case ExternalFileChangeAction.Reload:
                await ReloadEditorFromDiskAsync(control, tab, path);
                break;
            case ExternalFileChangeAction.Conflict:
                _externallyChangedDirty.AddOrUpdate(control, control);
                _diskFingerprints.AddOrUpdate(control, Task.FromResult(current));
                if (tab is null)   // 切り離しの窓にはバーが無い
                    control.ShowStatusMessage($"{Path.GetFileName(path)} がエディタの外で変更されました（未保存の編集あり。このまま保存すると外の変更を上書きします）");
                break;
        }
        UpdateExternalChangeBar();
    }

    /// <summary>await の間にタブが閉じられた・ワークスペースが変わった・別のファイルを載せた、を弾く。</summary>
    private bool IsStillWatched(VimEditorControl control, EditorTab? tab, string path)
        => FilePathRelations.AreEqual(control.FilePath, path)
           && (tab is null
               ? WatchedEditors().Any(e => ReferenceEquals(e.Control, control))
               : _editorTabs.Contains(tab) && tab.IsRealized && ReferenceEquals(tab.Control, control));

    /// <summary>
    /// ディスクの本文を入れ直す。<c>LoadFile</c> は使えない——同じパスのバッファを既に持っていると Editor は
    /// そのバッファへ切り替えるだけで<b>ディスクを読まない</b>。<c>ReloadFromDisk</c>（Editor 1.0.104）は
    /// 1回の取り消せる編集として入れ直すので、元に戻す履歴もカーソルも残る（<c>:e!</c> は両方捨てる）。
    /// 読み込みとデコードは背景で済ませ、UI スレッドは載せるだけ（§31.15）。
    /// </summary>
    private async Task ReloadEditorFromDiskAsync(VimEditorControl control, EditorTab? tab, string path) {
        var prepared = await Task.Run(() => Editor.Core.Buffer.PreparedFileLoad.Prepare(path));
        if (!IsStillWatched(control, tab, path))
            return;
        if (!control.ReloadFromDisk(prepared))
            return;
        _externallyChangedDirty.Remove(control);
        _editorDocumentEvents.RaiseLoaded(control);   // 指紋の取り直し・映し側（複製・抜粋）への知らせ
        if (tab is not null) {
            UpdateEditorTab(tab);
            if (ReferenceEquals(_editorSupport.Source, tab))
                InvalidateEditorSupport();
        }
    }

    /// <summary>エディタ上端のバーを、アクティブタブが競合を抱えているかに合わせる。</summary>
    private void UpdateExternalChangeBar() {
        var bar = _vm.ExternalFileChange;
        if (_activeEditorTab is { IsRealized: true } tab
            && _externallyChangedDirty.TryGetValue(tab.Control, out _)
            && tab.Control.FilePath is { Length: > 0 } path)
            bar.Show(path);
        else
            bar.Hide();
    }

    private void ClearExternalChangeConflict(string? path) {
        if (string.IsNullOrEmpty(path))
            return;
        foreach (var (control, _) in WatchedEditors())
            if (FilePathRelations.AreEqual(control.FilePath, path))
                _externallyChangedDirty.Remove(control);
        UpdateExternalChangeBar();
    }

    /// <summary>バーの「ディスクの内容で読み直す」。未保存の編集は元に戻す履歴に残る（Ctrl+Z で戻せる）。</summary>
    private async Task ReloadActiveTabFromDiskAsync() {
        if (_activeEditorTab is not { IsRealized: true } tab || tab.Control.FilePath is not { Length: > 0 } path)
            return;
        _externallyChangedDirty.Remove(tab.Control);
        UpdateExternalChangeBar();
        if (File.Exists(path))
            await ReloadEditorFromDiskAsync(tab.Control, tab, path);
    }

    /// <summary>バーの「差分を見る」：ディスクの本文（左）と編集中の本文（右）を比べる。</summary>
    private void CompareActiveTabWithDisk() {
        if (_activeEditorTab is not { IsRealized: true } tab || tab.Control.FilePath is not { Length: > 0 } path)
            return;
        var resolution = DiffComparisonSourceResolver.FromSavedFile(path, Path.GetFileName(path), tab.Control.Text);
        if (resolution.Comparison is { } comparison)
            ShowDiff(new DiffOpenTarget.Comparison(comparison));
        else if (resolution.ErrorMessage is { } error)
            tab.Control.ShowStatusMessage(error);
    }
}
