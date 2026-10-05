namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: 「留守中に起きたこと」（§24.22）。袖のバッジ（§24.1）が「いま」の周辺視野なら、
/// これは<b>いなかった間</b>の周辺視野——離れていた人が戻ったとき、その間に終わったコマンド・HEAD の動き・
/// 書き換わったファイルを右下のカードで1度だけ知らせ、各行からその出来事の次の一手を開く。
/// 判定は <see cref="PresenceTracker"/>、まとめは <see cref="AwaySummaryBuilder"/>。</summary>
public partial class ShellWindow {
    private readonly PresenceTracker _presence = new();
    private readonly DispatcherTimer _presenceTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
    private bool _appActive = true;
    private static bool _presenceInputHooked;

    /// <summary>reflog と作業ツリーを読む上限（大きなリポジトリで戻った瞬間を重くしない）。</summary>
    private const int AwayReflogTake = 40;
    private const int AwayMaxStatFiles = 2000;

    private void InitializeAwaySummary() {
        // ウィンドウではなく<b>アプリ</b>の活性で見る。切り離したウィンドウ（§21.4）へ移っただけで
        // 主ウィンドウは非アクティブになるが、人は部屋の中に居る。
        if (Application.Current is { } app) {
            app.Deactivated += (_, _) => {
                _appActive = false;
                _presence.OnDeactivated(DateTime.UtcNow);
            };
            app.Activated += (_, _) => {
                _appActive = true;
                OnPresenceReturn();
            };
        }
        // 前面のまま席を立った場合は、戻りを入力で知る（どの Loomo ウィンドウの入力でもよい）。
        if (!_presenceInputHooked) {
            _presenceInputHooked = true;
            EventManager.RegisterClassHandler(typeof(Window), PreviewKeyDownEvent,
                new KeyEventHandler((_, _) => (Application.Current?.MainWindow as ShellWindow)?.OnPresenceInput()), true);
            EventManager.RegisterClassHandler(typeof(Window), PreviewMouseDownEvent,
                new MouseButtonEventHandler((_, _) => (Application.Current?.MainWindow as ShellWindow)?.OnPresenceInput()), true);
        }
        _presenceTimer.Tick += (_, _) => {
            if (_appActive && TryGetLastInputUtc() is { } lastInput)
                _presence.OnIdleCheck(DateTime.UtcNow, lastInput);
        };
        _presenceTimer.Start();
        _vm.AwaySummary.ItemOpenRequested += (_, item) => OpenAwayItem(item);
        _vm.AwaySummary.CopyRequested += (_, text) => {
            try {
                Clipboard.SetText(text);
                ToastService.Info("まとめを Markdown でコピーしました。");
            }
            catch { /* クリップボード占有中は無視 */ }
        };
        Closed += (_, _) => _presenceTimer.Stop();
    }

    private void OnPresenceInput() {
        if (_presence.IsAway && _appActive)
            OnPresenceReturn();
    }

    private void OnPresenceReturn() {
        if (_presence.OnReturn(DateTime.UtcNow) is { } since)
            _ = ShowAwaySummaryAsync(since, DateTime.UtcNow);
    }

    private async Task ShowAwaySummaryAsync(DateTime sinceUtc, DateTime returnedUtc) {
        if (_activeWorkspace is not { } workspace)
            return;
        try {
            var commands = workspace.RecentTerminalCommands?.ToList() ?? new List<RecentTerminalCommandSnapshot>();
            IReadOnlyList<GitReflogEntry> reflog = Array.Empty<GitReflogEntry>();
            IReadOnlyList<AwayFileChange> files = Array.Empty<AwayFileChange>();
            if (_git.RootPath is { Length: > 0 } root) {
                var page = await _git.GetReflogAsync("HEAD", 0, AwayReflogTake);
                if (page.Error is null)
                    reflog = page.Entries;
                var status = await _git.GetStatusAsync();
                if (status.IsRepository)
                    files = await Task.Run(() => StatChangedFiles(root, status));
            }
            // 読んでいる間に別のワークスペースへ移ったら、前の部屋のまとめは出さない
            if (!ReferenceEquals(_activeWorkspace, workspace))
                return;
            var summary = AwaySummaryBuilder.Build(sinceUtc, returnedUtc, commands, reflog, files);
            if (!summary.IsEmpty)
                _vm.AwaySummary.Show(summary);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) {
            // 知らせは補助。読めなければ出さないだけで、戻ってきた人の操作は止めない
        }
    }

    /// <summary>変更中のファイルの更新時刻（書き出しスレッドではなくプールで。ファイル数は上限で切る）。
    /// 消えたファイルは時刻が無いので数えない（いつ消えたかは分からない）。</summary>
    private static IReadOnlyList<AwayFileChange> StatChangedFiles(string root, GitStatusSnapshot status) {
        var result = new List<AwayFileChange>();
        foreach (var entry in status.Unstaged.Concat(status.Staged)
                     .GroupBy(e => e.Path, StringComparer.Ordinal).Select(g => g.First())
                     .Take(AwayMaxStatFiles)) {
            try {
                var full = Path.GetFullPath(Path.Combine(root, entry.Path));
                var info = new FileInfo(full);
                if (info.Exists)
                    result.Add(new AwayFileChange(full, entry.Path, entry, info.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
            }
        }
        return result;
    }

    /// <summary>行を押したとき：その出来事の「次の一手」を開く。カードは閉じない（続けて他の行も見る）。</summary>
    private void OpenAwayItem(AwayItem item) {
        switch (item.Payload) {
            case string command:
                OpenAwayCommand(command);
                break;
            case GitReflogEntry entry when entry.MovedCommit:
                // その操作で HEAD がどこからどこへ動いたか＝何が入った／消えたか
                ShowDiff(new DiffOpenTarget.CommitRange(entry.PreviousHash, entry.Hash, item.Title));
                break;
            case GitReflogEntry:
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Git);
                FocusPane(PaneKind.Git);
                break;
            case AwayFileChange file when file.Entry.IsUntracked:
                _ = OpenFileInNewEditorTabAsync(file.FullPath);
                break;
            case AwayFileChange file:
                ShowDiff(new DiffOpenTarget.WorkingTreeFile(file.Entry, IsStaged: file.Entry.WorkStatus is ' ' or '.'));
                break;
            case TrailNoteRecord bookmark:
                if (!_vm.Trail.JumpToBookmark(bookmark))
                    ToastService.Info("そのしおりの地点が見つかりませんでした。");
                break;
        }
    }

    /// <summary>直前の留守中のまとめをもう一度出す（× で閉じた後に読み返す口・§24.22）。</summary>
    private void ReshowAwaySummary() {
        if (_vm.AwaySummary.LastAway is { } last)
            _vm.AwaySummary.Show(last);
        else
            ToastService.Info("まだ留守中のまとめはありません（5分以上離れて戻ると出ます）。");
    }

    /// <summary>この日のまとめを読む reflog の上限（ページ単位でその日より前に届くまで読む）。</summary>
    private const int DaySummaryReflogPage = 100;
    private const int DaySummaryReflogMax = 1000;

    /// <summary>軌跡で表示中の日のまとめ（§24.23）。</summary>
    private void ShowTrailDaySummary() => _ = ShowDaySummaryAsync(_vm.Trail.DisplayDate);

    /// <summary>その日に部屋で起きたこと（しおり・コマンド・Git・変更中のファイル）を留守中と同じカードで出す。
    /// まとめは保存せず、開くたびに記録から組み立てる。</summary>
    private async Task ShowDaySummaryAsync(DateOnly day) {
        if (_activeWorkspace is not { } workspace)
            return;
        try {
            var trail = _vm.Trail;
            var (bookmarks, runs) = await Task.Run(() => (trail.ListBookmarks(), trail.LoadCommandRuns(day)));
            var reflog = new List<GitReflogEntry>();
            IReadOnlyList<AwayFileChange> files = Array.Empty<AwayFileChange>();
            if (_git.RootPath is { Length: > 0 } root) {
                // reflog は新しい順。その日の始まりより古い記録に届くまでページを読む
                var dayStart = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local));
                for (int skip = 0; skip < DaySummaryReflogMax; skip += DaySummaryReflogPage) {
                    var page = await _git.GetReflogAsync("HEAD", skip, DaySummaryReflogPage);
                    if (page.Error is not null)
                        break;
                    reflog.AddRange(page.Entries);
                    if (!page.HasMore || page.Entries.Count == 0 || page.Entries[^1].Time is { } oldest && oldest < dayStart)
                        break;
                }
                var status = await _git.GetStatusAsync();
                if (status.IsRepository)
                    files = await Task.Run(() => StatChangedFiles(root, status));
            }
            if (!ReferenceEquals(_activeWorkspace, workspace))
                return;
            var summary = DaySummaryBuilder.Build(day, bookmarks, runs, reflog, files);
            if (summary.IsEmpty)
                ToastService.Info($"{day:M/d} の記録（しおり・コマンド・Git・変更中のファイル）はありません。");
            else
                _vm.AwaySummary.Show(summary);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) {
            ToastService.Info("この日のまとめを読めませんでした。");
        }
    }

    /// <summary>コマンド：出力が残っていれば前回と比べる（無ければ今回の出力を開く）、何も無ければターミナルへ。</summary>
    private void OpenAwayCommand(string command) {
        var outputs = ActiveTerminalOutputs;
        if (outputs?.Pairs.FirstOrDefault(p => p.Latest.Command == command) is { Latest: not null } pair) {
            ShowDiff(new DiffOpenTarget.Comparison(TerminalCommandOutputs.Compare(pair.Previous, pair.Latest)));
            return;
        }
        if (outputs?.Latest.FirstOrDefault(o => o.Command == command) is { } output) {
            _ = OpenTerminalOutputInEditorAsync(output);
            return;
        }
        SetPaneVisible(PaneKind.Terminal, true);
        FocusPane(PaneKind.Terminal);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>システム全体の最後の入力時刻。<c>dwTime</c> は起動からのミリ秒（49.7日で一周する
    /// 32bit）なので、同じく32bitに切った現在の TickCount との差で経過を求める。</summary>
    private static DateTime? TryGetLastInputUtc() {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
            return null;
        uint elapsedMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return DateTime.UtcNow - TimeSpan.FromMilliseconds(elapsedMs);
    }
}
