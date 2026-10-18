namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: タブの棚（§24.29）。エディタ／ブラウザのタブが種類ごとの上限（既定 10 枚・ピン留めは数えない）を
/// 超えたら、いちばん長く見ていないタブを帯から棚へ下げ、押せば下げたときの場所へ戻す。棚の行は 7 日で消える。
/// 判定は <see cref="TabShelfPolicy"/>、棚そのものは <see cref="TabShelfViewModel"/>。
/// ターミナルのタブは下げない（帯から外すとシェルが死ぬ・§34）。
/// </summary>
public partial class ShellWindow {
    /// <summary>期限切れの見回り。期限は日単位なので粗くてよい。</summary>
    private readonly DispatcherTimer _tabShelfTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(10) };
    private bool _shelveOverflowPending;

    private void InitializeTabShelf() {
        _vm.Tabs.Shelf.RestoreRequested += (_, items) => _ = RestoreShelvedTabsAsync(items);
        _vm.Tabs.Shelf.Changed += (_, _) => SaveActiveWorkspaceSnapshot();
        _vm.Tabs.TabShelveRequested += (_, tab) => _ = ShelveTabsByHandAsync(tab);
        _vm.Tabs.TabPinToggled += (_, tab) => ToggleTabPin(tab);
        _vm.Tabs.ShelveLimitChanged += (_, _) => ScheduleShelveOverflow();
        _tabShelfTimer.Tick += (_, _) => {
            _vm.Tabs.Shelf.PurgeExpired(DateTime.UtcNow);
            _vm.Tabs.Shelf.RefreshTimes();
        };
        _tabShelfTimer.Start();
        Closed += (_, _) => _tabShelfTimer.Stop();
    }

    /// <summary>ワークスペースの棚を読み込んだ直後に、期限切れを落とす。</summary>
    private void PurgeExpiredShelf() => _vm.Tabs.Shelf.PurgeExpired(DateTime.UtcNow);

    /// <summary>
    /// 上限を超えていないかを、いまの操作が終わってから一度だけ見る。タブを開く（＝活性化する）たびに呼ばれるが、
    /// 開く処理の途中（読み込み待ち・復元の並び替え）で帯を縮めないよう、優先度を落として後へ回す。
    /// </summary>
    private void ScheduleShelveOverflow() {
        if (_shelveOverflowPending)
            return;
        _shelveOverflowPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
            _shelveOverflowPending = false;
            _ = ShelveOverflowAsync();
        }));
    }

    private async Task ShelveOverflowAsync() {
        if (!_vm.Tabs.AutoShelve || _activeWorkspace is null || _workspaceTransition.IsSwitching)
            return;
        var now = DateTime.UtcNow;
        // 分割で見えているタブは「いま見ている」——並びの上でも新しくしておく
        foreach (var tab in _editorTabs.Where(t => _editorViews?.IsShown(t.Id) == true))
            tab.LastSeenUtc = now;
        var limit = _vm.Tabs.AutoShelveLimit;
        var editorIds = TabShelfPolicy.SelectOverflow(
            _editorTabs.Select(t => new TabShelfCandidate(t.Id, t.LastSeenUtc, IsEditorTabShelfProtected(t, byHand: false), t.IsPinned)).ToList(),
            limit);
        var browserIds = TabShelfPolicy.SelectOverflow(
            _browserTabs.Select(t => new TabShelfCandidate(t.Id, t.LastSeenUtc, IsBrowserTabShelfProtected(t, byHand: false), t.IsPinned)).ToList(),
            limit);
        await ShelveAsync(editorIds, browserIds);
    }

    /// <summary>TABS の右クリック「棚へ下げる」。見ているタブでも下げられる（人が選んだので）が、
    /// 未保存・名前の無いタブは下げない——棚は場所を覚えるだけで、中身は持たない。</summary>
    private async Task ShelveTabsByHandAsync(TabEntryViewModel entry) {
        switch (entry.Kind) {
            case TabEntryKind.Editor when _editorTabs.FirstOrDefault(t => t.Id == entry.Id) is { } editor:
                if (IsEditorTabShelfProtected(editor, byHand: true)) {
                    ToastService.Info(editor.PeekIsModified
                        ? "未保存のタブは棚へ下げられません（保存してからにしてください）"
                        : "このタブは棚へ下げられません（ファイルに結び付いていません）");
                    return;
                }
                await ShelveAsync([editor.Id], []);
                break;
            case TabEntryKind.Browser when _browserTabs.FirstOrDefault(t => t.Id == entry.Id) is { } browser:
                if (IsBrowserTabShelfProtected(browser, byHand: true)) {
                    ToastService.Info("このタブは棚へ下げられません");
                    return;
                }
                await ShelveAsync([], [browser.Id]);
                break;
        }
    }

    /// <summary>TABS の右クリック「ピン留め／外す」。ピン留めは上限に数えないので、外したら超えた分を見直す。</summary>
    private void ToggleTabPin(TabEntryViewModel entry) {
        bool pinned;
        switch (entry.Kind) {
            case TabEntryKind.Editor when _editorTabs.FirstOrDefault(t => t.Id == entry.Id) is { } editor:
                pinned = editor.IsPinned = !editor.IsPinned;
                break;
            case TabEntryKind.Browser when _browserTabs.FirstOrDefault(t => t.Id == entry.Id) is { } browser:
                pinned = browser.IsPinned = !browser.IsPinned;
                break;
            default:
                return;
        }
        entry.IsPinned = pinned;
        SaveActiveWorkspaceSnapshot();
        if (!pinned)
            ScheduleShelveOverflow();
    }

    /// <summary>自動で棚へ下げてはいけないエディタタブか（ピン留めは数えないので別扱い）。
    /// 自動のときは「見えている」も守る。</summary>
    private bool IsEditorTabShelfProtected(EditorTab tab, bool byHand) {
        if (tab.PeekIsModified || tab.PeekIsVirtual || string.IsNullOrEmpty(tab.PeekFilePath))
            return true;
        // EDITOR として開いたファイル（§38.5）は、閉じると呼び出し元（git commit 等）が終わってしまう
        if (_editWaiters.ContainsKey(tab.PeekFilePath))
            return true;
        if (byHand)
            return false;
        return ReferenceEquals(tab, _activeEditorTab)
               || _editorViews?.IsShown(tab.Id) == true
               || ReferenceEquals(_editorSupport.Source, tab);
    }

    /// <summary>自動で棚へ下げてはいけないブラウザタブか。読み込み中・音が鳴っている・プレビュー専用は守る。</summary>
    private bool IsBrowserTabShelfProtected(BrowserTab tab, bool byHand) {
        var url = BrowserDisplayMapper.CurrentUrl(tab.View.TryUrl(), tab.PendingUrl);
        if (string.IsNullOrWhiteSpace(url) || EditorSupportNavigationService.IsPreviewUrl(url))
            return true;
        if (byHand)
            return false;
        bool playing;
        try { playing = tab.View.TryCore()?.IsDocumentPlayingAudio == true; }
        catch { playing = false; }
        return ReferenceEquals(tab, _activeBrowserTab) || tab.IsLoading || playing;
    }

    /// <summary>タブを帯から外して棚へ積む。棚へ積んだ項目を返す。</summary>
    private async Task<IReadOnlyList<ShelvedTabSnapshot>> ShelveAsync(
        IReadOnlyList<Guid> editorIds, IReadOnlyList<Guid> browserIds) {
        var shelved = new List<ShelvedTabSnapshot>();
        foreach (var id in editorIds) {
            if (_editorTabs.FirstOrDefault(t => t.Id == id) is not { } tab)
                continue;
            var place = WorkspaceSessionCoordinator.CaptureEditorTab(tab, activeTabId: null);
            shelved.Add(new ShelvedTabSnapshot {
                Kind = ShelvedTabSnapshot.EditorKind,
                FilePath = place.FilePath,
                Title = place.Title,
                CaretLine = place.CaretLine,
                CaretColumn = place.CaretColumn,
                ScrollRatio = place.ScrollRatio,
            });
            CloseEditorTab(id);
        }
        foreach (var id in browserIds) {
            if (_browserTabs.FirstOrDefault(t => t.Id == id) is not { } tab)
                continue;
            shelved.Add(new ShelvedTabSnapshot {
                Kind = ShelvedTabSnapshot.BrowserKind,
                Url = BrowserDisplayMapper.CurrentUrl(tab.View.TryUrl(), tab.PendingUrl),
                Title = tab.CurrentTitle,
            });
            await CloseBrowserTabAsync(id);
        }
        if (shelved.Count > 0)
            _vm.Tabs.Shelf.Add(shelved);   // Changed → 保存
        return shelved;
    }

    /// <summary>棚から戻す。下げたときの場所（キャレット・スクロール・URL）へ戻し、棚から外す。
    /// 複数のときは並べるだけで、前に出すのは先頭の1枚。</summary>
    private async Task RestoreShelvedTabsAsync(IReadOnlyList<ShelvedTabSnapshot> items) {
        await _workspaceTransition.WhenSettledAsync();
        EditorTab? firstEditor = null;
        var restored = new List<Guid>();
        var missing = 0;
        foreach (var item in items) {
            if (item.Kind == ShelvedTabSnapshot.EditorKind) {
                if (item.FilePath is not { Length: > 0 } path || !File.Exists(path)) {
                    missing++;
                    restored.Add(item.Id);   // 戻せない行は棚に残しても押せないだけなので外す
                    continue;
                }
                var tab = EditorTabNavigationPolicy.FindOpenFileTab(_editorTabs, path);
                if (tab is null) {
                    tab = CreatePendingEditorTab(new EditorTabSnapshot {
                        FilePath = path,
                        Title = Path.GetFileName(path),
                        CaretLine = item.CaretLine,
                        CaretColumn = item.CaretColumn,
                        ScrollRatio = item.ScrollRatio,
                    });
                    _editorTabs.Add(tab);
                    _vm.Tabs.AddEditorTab(tab.Id, path, false, false);
                }
                // 戻したもの同士では、上限を超えても互いを押し出さない（いちばん新しく見た扱い）
                tab.LastSeenUtc = DateTime.UtcNow;
                firstEditor ??= tab;
                restored.Add(item.Id);
            } else if (item.Url is { Length: > 0 } url) {
                await OpenUrlInBrowserAsync(url, item.Title);
                restored.Add(item.Id);
            } else {
                restored.Add(item.Id);
            }
        }
        _vm.Tabs.Shelf.Remove(restored);
        if (firstEditor is not null) {
            EnsureEditorPaneForOpenedFile(firstEditor.PeekFilePath);
            ActivateEditorTab(firstEditor.Id);
        }
        if (missing > 0)
            ToastService.Warning($"{missing} 件はファイルが見つからないため戻せませんでした（棚から外しました）");
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>棚を通らずに同じファイルを開き直したら、棚の行は外す（棚と帯に同じものを置かない）。</summary>
    private void ForgetShelvedFile(string? path) => _vm.Tabs.Shelf.ForgetFile(path);
}
