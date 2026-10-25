using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: Git ペインのワークツリーを<b>部屋のどこへ</b>開くか。ワークスペース（部屋）ごと移る／
/// 今の部屋にフォルダーとして足す／表示ターミナルをそこへ移す／エクスプローラー、の4つ。
/// どれも部屋の配置に関わるので VM ではなくここが決める（§24.5.2 の「行き先はホストが持つ」と同じ）。
/// </summary>
public partial class ShellWindow {
    private void WireWorktreeRequests() {
        _vm.GitSession.WorktreeOpenRequested += (_, request) => OpenWorktree(request);
        _vm.GitSession.WorktreeRemoving += (_, path) => DetachWorktreeFolders(path);
        _vm.GitSession.WorktreeRemoved += (_, path) => _vm.Workspaces.RemoveWorktreeRoom(path);
        _vm.GitSession.WorktreeMoved += (_, moved) => _vm.Workspaces.MoveWorktreeRoom(
            moved.OldPath, moved.NewPath, p => System.IO.File.Exists(p) || System.IO.Directory.Exists(p));
        _vm.GitSession.WorktreeRoomLastUsed = _vm.Workspaces.LastUsedOfRoot;
        _vm.Tabs.WorktreeCounterpartsFor = WorktreeCounterpartsForAsync;
        _vm.Tabs.WorktreeCounterpartRequested += (_, request) => _ = OpenWorktreeCounterpartAsync(request);
        _vm.GitSession.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(GitSessionViewModel.Worktrees)) RefreshWorktreeAgentMarks();
        };
    }

    /// <summary>ワークツリー一覧の「そこで Claude Code が動いている」印を、この部屋のターミナルの状態から
    /// 入れ直す（§24.17.2）。ターミナルの印が動いたときと一覧を読み直したときに呼ぶ。</summary>
    private void RefreshWorktreeAgentMarks() {
        var worktrees = _vm.GitSession.Worktrees;
        var marks = worktrees.Count <= 1 && _vm.GitSession.WorktreeAgentMarks.Count == 0
            ? _vm.GitSession.WorktreeAgentMarks
            : WorktreeAgentMarks.For(worktrees, _paneActivity.AgentTabs().Select(agent =>
                new WorktreeAgentSighting(agent.State, agent.TranscriptPath,
                    _terminalTabs.FirstOrDefault(t => t.Id == agent.TabId)?.View.WorkingDirectory)));
        var current = _vm.GitSession.WorktreeAgentMarks;
        if (marks.Count == current.Count && marks.All(p => current.TryGetValue(p.Key, out var v) && v == p.Value))
            return;   // 同じなら差し替えない（行の再描画を起こさない）
        _vm.GitSession.WorktreeAgentMarks = marks;
    }

    private void OpenWorktree(GitWorktreeOpenRequest request) {
        var path = request.Path;
        switch (request.Mode) {
            case GitWorktreeOpenMode.Workspace:
                SwitchToWorktree(path);
                break;
            case GitWorktreeOpenMode.AddFolder:
                if (_workspace.Folders.Any(f => WorkspacePaths.IsWithin(f, path) && WorkspacePaths.IsWithin(path, f)))
                    return;   // もう入っている
                if (!_vm.FolderTree.AddFolderToWorkspace(path))
                    MessageBox.Show(this,
                        "このワークツリーはワークスペースのフォルダーと親子関係にあるため、フォルダーとして追加できません。\n" +
                        "（リポジトリの中に置いたワークツリーは「ワークスペースとして開く」を使ってください）",
                        "フォルダーとして追加", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            case GitWorktreeOpenMode.Terminal:
                // エクスプローラーの「ターミナルでここへ」と同じ道（表示ターミナルへ送るのは移動だけ）。
                OnSetInTerminalRequested(this, new TerminalSetRequest(path, IsDirectory: true));
                break;
            case GitWorktreeOpenMode.Explorer:
                FileExplorerLauncher.OpenInExplorer(path);
                break;
        }
    }

    /// <summary>
    /// ブランチを切り替えるようにワークツリーへ移る（§24.17.1）。部屋が無ければ今の部屋を写して作る
    /// ——写し元は<b>いまこの瞬間</b>の状態なので、先に捕まえてから渡す（定期保存は間引かれていて古い）。
    /// 切替の途中に押されたら、捨てずに終わるのを待つ（途中で写すと、半分入れ替わった部屋が写し元になる）。
    /// </summary>
    private async void SwitchToWorktree(string path) {
        try {
            await _workspaceTransition.WhenSettledAsync();
            SaveActiveWorkspaceSnapshot(immediate: true);
            var git = _vm.GitSession;
            var request = new WorktreeSwitchRequest(
                path,
                git.CurrentWorktree?.Path ?? _workspace.PrimaryFolder ?? path,
                git.MainWorktreePath ?? path,
                git.Worktrees.Select(w => w.Path).ToList());
            _vm.Workspaces.ActivateWorktree(request, _activeWorkspace, p => File.Exists(p) || Directory.Exists(p));
        } catch (Exception ex) {
            ToastService.Error($"ワークツリーへ切り替えられませんでした: {ex.Message}");
        }
    }

    /// <summary>ファイルの、ほかのワークツリーでの同じ相対パス（§24.80）。Git ペインをまだ開いていなくて一覧が空なら、ここで読む。</summary>
    private async Task<IReadOnlyList<WorktreeCounterpart>> WorktreeCounterpartsForAsync(string filePath) {
        var git = _vm.GitSession;
        if (git.Worktrees.Count <= 1 && git.IsRepository)
            await git.RefreshWorktreesAsync();
        return WorktreeCounterparts.For(filePath,
            git.Worktrees.Where(w => !w.IsBare).Select(w => (w.Path, w.Branch)),
            File.Exists);
    }

    /// <summary>TABS の「ほかのワークツリーで ▸」（§24.80）。開くときは、いま見ている行へ合わせる——
    /// 「本線ではこの関数はどうなっていたか」を見たいので、ファイルの頭から探し直させない。</summary>
    private async Task OpenWorktreeCounterpartAsync(WorktreeCounterpartRequest request) {
        var target = request.Target;
        if (!File.Exists(target.FilePath)) {
            ToastService.Error($"{target.Label} にこのファイルはありません");
            return;
        }
        var line = _editorTabs.FirstOrDefault(t => t.Id == request.Tab.Id) is { IsRealized: true } tab
            ? tab.Control.Caret.Line + 1 : 0;
        switch (request.Action) {
            case WorktreeCounterpartAction.OpenHere:
                await OpenPathInEditorAsync(target.FilePath, line, 0);
                break;
            case WorktreeCounterpartAction.Compare when request.Tab.FilePath is { } current:
                // 左＝相手（比べる元）、右＝いまのファイル（行から飛ぶ先を手元の側にそろえる）。
                CompareFilesInDiff(new FileCompareRequest(target.FilePath, current));
                break;
            case WorktreeCounterpartAction.OpenInRoom:
                try {
                    // 切替は列に積まれて Background で走るので、始まる（世代が進む）のを待ってから終わるのを待つ。
                    // 行き先がいまの部屋そのもの等で切替が起きなければ、待ち切ってそのまま開く。
                    await _workspaceTransition.WhenSettledAsync();
                    var epoch = _workspaceTransition.Epoch;
                    SwitchToWorktree(target.WorktreePath);
                    for (var i = 0; i < 100 && !_workspaceTransition.HasSwitchedSince(epoch); i++)
                        await Task.Delay(50);
                    await _workspaceTransition.WhenSettledAsync();
                    await OpenPathInEditorAsync(target.FilePath, line, 0);
                } catch (Exception ex) {
                    ToastService.Error($"その部屋で開けませんでした: {ex.Message}");
                }
                break;
        }
    }

    /// <summary>
    /// 消すワークツリーを（フォルダーとして足してあれば）今のワークスペースから先に外す。言語サーバー・
    /// ファイル監視・ツリーが握ったまま消すと、Windows では「使用中」で削除が失敗する。
    /// プライマリは外せない（そもそも「いま開いているワークツリー」は削除できないようにしてある）。
    /// 写して作ったそのワークツリーの部屋は、ここではなく<b>消し終えてから</b>片付ける（<c>WorktreeRemoved</c>）。
    /// </summary>
    private void DetachWorktreeFolders(string worktreePath) {
        var inside = _workspace.Folders
            .Where(f => WorkspacePaths.IsWithin(worktreePath, f)
                        && !string.Equals(f, _workspace.PrimaryFolder, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var folder in inside)
            _vm.FolderTree.RemoveFolderFromWorkspace(folder);
    }
}
