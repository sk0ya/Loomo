using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>Diff セッションの差分1行。Kind は DiffLineKind 名＋"Header"（git ヘッダ行）。</summary>
public sealed record DiffRowVm(string Kind, string Text);

/// <summary>作業ツリー差分の1ハンク（ハンク単位ステージ用）。<see cref="Index"/> は分解後の並び順。
/// <see cref="IsStaged"/> がステージ済み（＝アンステージ対象）かどうかで操作ラベルが変わる。</summary>
public sealed record DiffHunkVm(int Index, string HeaderLine, string Summary, bool IsStaged)
{
    /// <summary>ボタンのラベル（ステージ済みハンクはアンステージ、未ステージはステージ）。</summary>
    public string ActionLabel => IsStaged ? "アンステージ" : "ステージ";
}

/// <summary>
/// 左右並び表示の差分1行。Kind は <see cref="SideCellKind"/> 名。
/// Gap / Header は左右共通の1行として全幅表示する（テンプレート側で判定）。
/// LeftLine / RightLine は表示用の行番号文字列（その側に行が無いときは空文字）。
/// </summary>
public sealed record DiffSideRowVm(
    string LeftKind, string LeftText, string RightKind, string RightText,
    string LeftLine, string RightLine);

/// <summary>Diff セッションのファイル1行（AI変更 or Git変更）。</summary>
public sealed class DiffFileItem
{
    public required string FullPath { get; init; }
    /// <summary>ワークスペース相対の表示パス。</summary>
    public required string DisplayPath { get; init; }
    /// <summary>状態バッジ（AI: 新規/変更、Git: ステータス文字）。</summary>
    public required string Badge { get; init; }
    /// <summary>"+N −M" の行数統計。全文を保持できなかったときは空。</summary>
    public string Stats { get; init; } = "";
    public string FileName => Path.GetFileName(FullPath);

    // --- AI変更のとき ---
    public bool IsAi { get; init; }
    public bool IsNew { get; init; }
    public string? OldContent { get; init; }
    public string? NewContent { get; init; }
    /// <summary>巻き戻し可能か（新規=削除 / 変更=旧全文の復元）。</summary>
    public bool CanRevert => IsAi && (IsNew || OldContent is not null);

    // --- Git変更のとき（作業ツリー） ---
    public GitChangeEntry? Entry { get; init; }
    public bool IsStaged { get; init; }

    // --- Gitコミット範囲のとき ---
    public GitCommitFileChange? CommitFile { get; init; }
}

/// <summary>
/// Diff セッションペインの ViewModel。2つのソースを切り替えて表示する（既定は Git）：
/// AI変更（<see cref="IFileChangeJournal"/> が記録した write_file / edit_file の前後全文）と、
/// Git の差分（作業ツリーのステージ済み・未ステージ。Git セッションからの連携で
/// 特定コミットの変更・コミット範囲の差分も表示できる）。AI変更はファイル単位の巻き戻しに対応する。
/// </summary>
public sealed partial class DiffSessionViewModel : ObservableObject
{
    private readonly IFileChangeJournal _journal;
    private readonly GitService _git;
    private readonly IEditorService _editor;
    private readonly IWorkspaceService _workspace;
    private bool _loaded;

    /// <summary>Git モードでの表示対象コミット範囲。null は作業ツリー。From=null は To 1コミットの変更。</summary>
    private (string? From, string To)? _commitRange;

    [ObservableProperty] private bool _isGitMode = true;
    private bool _suppressModeChangeRefresh;
    /// <summary>差分本体を左右並び（side-by-side）で表示するか。false は統合（unified）表示。既定は左右。</summary>
    [ObservableProperty] private bool _isSideBySide = true;
    /// <summary>コミット範囲を表示中のヘッダーラベル（空なら作業ツリー表示）。</summary>
    [ObservableProperty] private string _gitTargetLabel = "";
    /// <summary>単一コミットの差分を表示中なら、そのコミットを Git 一覧で選択できる。</summary>
    public bool CanOpenCommitInGit => _commitRange is { From: null };
    [ObservableProperty] private DiffFileItem? _selectedFile;
    [ObservableProperty] private string _emptyMessage = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusIsError;
    /// <summary>選択中ファイルを破棄できるか（Git の作業ツリー差分のときだけ。コミット範囲・AI変更では不可）。</summary>
    [ObservableProperty] private bool _canDiscardSelected;
    /// <summary>選択中ファイルで行単位の破棄ができるか（Git・作業ツリー・未ステージの追跡ファイルのときだけ）。</summary>
    [ObservableProperty] private bool _canDiscardLines;

    public ObservableCollection<DiffFileItem> Files { get; } = new();
    public ObservableCollection<DiffRowVm> DiffRows { get; } = new();
    public ObservableCollection<DiffSideRowVm> SideRows { get; } = new();

    /// <summary>選択中の作業ツリーファイルのハンク一覧（ハンク単位ステージ用）。AI変更・コミット範囲・
    /// 未追跡ファイルでは空。</summary>
    public ObservableCollection<DiffHunkVm> Hunks { get; } = new();

    /// <summary>表示中の単一コミットを Git ペインで選択するよう ShellWindow へ要求する。</summary>
    public event EventHandler<string>? CommitOpenInGitRequested;

    /// <summary>ハンク単位ステージのUIを出せるか（作業ツリーの追跡済みファイルで、ハンクが1つ以上）。</summary>
    public bool CanStageHunks => Hunks.Count > 0;

    public DiffSessionViewModel(
        IFileChangeJournal journal, GitService git, IEditorService editor, IWorkspaceService workspace)
    {
        _journal = journal;
        _git = git;
        _editor = editor;
        _workspace = workspace;
        _journal.Changed += (_, _) => DispatchRefresh();
        _git.RepositoryChanged += (_, _) => DispatchRefresh();
    }

    private IDisposable? _live;

    /// <summary>Diff ペインが初めて表示されたときに読み込む（以降はジャーナル／リポジトリ変化で追従）。</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _ = RefreshAsync();
    }

    /// <summary>Diff ペインが見えている間のライブ監視を開始する（作業ツリー差分の追従用）。</summary>
    public void StartLiveTracking() => _live ??= _git.TrackLiveChanges();

    /// <summary>Diff ペインが隠れたらライブ監視を止める。</summary>
    public void StopLiveTracking()
    {
        _live?.Dispose();
        _live = null;
    }

    private void DispatchRefresh()
    {
        if (!_loaded) return;
        var app = Application.Current;
        if (app is null) return;
        // ジャーナルはエージェントの実行スレッド、Git はコマンド完了スレッドから届くためディスパッチする
        app.Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync()));
    }

    partial void OnIsGitModeChanged(bool value)
    {
        _changeCursor = -1;
        // AI変更モードへ切り替えたらコミット範囲は解除（戻ったときは作業ツリーから）
        if (!value)
        {
            _commitRange = null;
            OnPropertyChanged(nameof(CanOpenCommitInGit));
            GitTargetLabel = "";
        }
        UpdateCanDiscard();
        if (!_suppressModeChangeRefresh)
            _ = RefreshAsync();
    }

    partial void OnSelectedFileChanged(DiffFileItem? value)
    {
        _changeCursor = -1; // ファイルが変わったら次/前ジャンプの位置をリセット
        // blame からのピンポイントジャンプ待ちがあっても、別ファイルへ移ったら破棄する
        if (_pendingJumpFile is not null && !ReferenceEquals(value, _pendingJumpFile))
        {
            _pendingJumpFile = null;
            _pendingJumpNewLine = -1;
        }
        UpdateCanDiscard();
        InvalidateWorkingTreePatch(value); // 開き直すたびに作業ツリーの最新内容を読み直す
        _ = LoadAndAutoJumpAsync(value);
    }

    /// <summary>「変更を破棄」「選択した行を破棄」の可否を更新する。どちらも Git の作業ツリー差分が前提。</summary>
    private void UpdateCanDiscard()
    {
        var workingTree = IsGitMode && _commitRange is null;
        CanDiscardSelected = workingTree && SelectedFile?.Entry is not null;
        // 行単位は逆適用パッチで実装するため、追跡済み・未ステージのファイルに限る
        // （未追跡は git のファイルヘッダが無く apply できない。ステージ済みは作業ツリーが対象外）。
        CanDiscardLines = workingTree
            && SelectedFile is { IsStaged: false, Entry: { IsUntracked: false } };
    }

    // 表示形式の切替時は同じ選択の差分を組み立て直し、最初の変更へジャンプする（一覧は変わらない）
    partial void OnIsSideBySideChanged(bool value)
    {
        _changeCursor = -1;
        _ = LoadAndAutoJumpAsync(SelectedFile);
    }

    /// <summary>
    /// 差分を読み込み、その組み立てが終わってから最初の変更へ自動ジャンプするよう View へ通知する。
    /// 読込完了後に通知するので、View 側は（再構築が走らないキャッシュ命中時でも）確実にジャンプできる。
    /// </summary>
    private async Task LoadAndAutoJumpAsync(DiffFileItem? item)
    {
        await LoadSelectedContentAsync(item);
        if (item is not null)
            AutoJumpRequested?.Invoke();
    }

    /// <summary>
    /// Git セッションから：コミット範囲の差分を表示する（from=null は to 1コミットの変更）。
    /// ペインの表示は呼び出し側（ShellWindow）が行う。
    /// </summary>
    public void ShowCommitRange(string? fromHash, string toHash, string label)
    {
        _loaded = true;
        _commitRange = (fromHash, toHash);
        OnPropertyChanged(nameof(CanOpenCommitInGit));
        GitTargetLabel = label;
        UpdateCanDiscard();
        if (!IsGitMode)
            IsGitMode = true;  // OnIsGitModeChanged 経由で更新が走る
        else
            _ = RefreshAsync();
    }

    /// <summary>
    /// エディタの blame クリックから：1コミットの差分を表示し、そのファイルを選択して、
    /// コミット時点の行番号 <paramref name="lineInCommit"/>（新側・1始まり）の行へスクロールする。
    /// ファイルが一致しない（リネーム等）ときは既定の選択、行が差分に見つからないときは
    /// 通常の「最初の変更へ」の自動ジャンプにフォールバックする。
    /// ペインの表示は呼び出し側（ShellWindow）が行う。
    /// </summary>
    public async Task ShowCommitFileAsync(string hash, string label, string? filePath, int lineInCommit)
    {
        _loaded = true;
        _commitRange = (null, hash);
        OnPropertyChanged(nameof(CanOpenCommitInGit));
        GitTargetLabel = label;
        UpdateCanDiscard();
        if (!IsGitMode)
        {
            // 生成された setter を通すと OnIsGitModeChanged が RefreshAsync を fire-and-forget で
            // 走らせて下の await と競合するため、この変更での自動更新だけ抑止する。
            _suppressModeChangeRefresh = true;
            try
            {
                IsGitMode = true;
            }
            finally
            {
                _suppressModeChangeRefresh = false;
            }
        }
        await RefreshAsync();

        var display = ToDisplayPath(filePath ?? "");
        var target = Files.FirstOrDefault(f =>
            string.Equals(f.DisplayPath, display, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        _pendingJumpNewLine = lineInCommit;
        _pendingJumpFile = target;
        if (!ReferenceEquals(SelectedFile, target))
            SelectedFile = target;              // OnSelectedFileChanged → 読込 → 自動ジャンプで消費
        else
            await LoadAndAutoJumpAsync(target); // 既に選択済みでも読み直してジャンプさせる
    }

    /// <summary>
    /// サイドバー Git パネルから：作業ツリーの特定ファイルの差分を表示する。
    /// コミット範囲を解除して作業ツリー（Git）モードへ切り替え、一覧を読み直してそのファイルを選択する。
    /// ペインの表示は呼び出し側（ShellWindow）が行う。
    /// </summary>
    public async Task ShowWorkingTreeFileAsync(GitChangeEntry entry, bool isStaged)
    {
        _loaded = true;
        _commitRange = null;
        GitTargetLabel = "";
        IsGitMode = true;
        await RefreshAsync();
        SelectedFile = Files.FirstOrDefault(f =>
            f.IsStaged == isStaged
            && string.Equals(f.DisplayPath, entry.Path, StringComparison.OrdinalIgnoreCase))
            ?? SelectedFile;
    }

    /// <summary>コミット範囲の表示を解除して作業ツリー差分へ戻る。</summary>
    [RelayCommand]
    private void ClearGitTarget()
    {
        if (_commitRange is null) return;
        _commitRange = null;
        OnPropertyChanged(nameof(CanOpenCommitInGit));
        GitTargetLabel = "";
        UpdateCanDiscard();
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void OpenCommitInGit()
    {
        if (_commitRange is { From: null, To: var hash })
            CommitOpenInGitRequested?.Invoke(this, hash);
    }

    private async Task RefreshAsync()
    {
        _loaded = true;
        // 一覧やリポジトリ状態が変わるので、古いパッチキャッシュは捨てる（表示形式の切替では走らない）
        _patchCache.Clear();
        var selectedPath = SelectedFile?.FullPath;

        var (items, emptyMessage) = !IsGitMode ? BuildAiItems()
            : _commitRange is { } range ? await BuildCommitItemsAsync(range)
            : await BuildGitItemsAsync();

        // 一覧に変化がなければ作り直さない（自動更新のたびに選択・スクロールが飛ぶチラつきの防止）。
        // AI変更は差分内容も item に閉じ、コミット範囲は不変なのでこれで完結。Git 作業ツリーだけは
        // 同じ一覧のままファイル内容が変わりうるので、差分本体を静かに読み直す（同一なら再描画しない）。
        if (SameAsCurrentFiles(items))
        {
            EmptyMessage = Files.Count > 0 ? "" : emptyMessage;
            if (IsGitMode && _commitRange is null)
                await LoadSelectedContentAsync(SelectedFile);
            return;
        }

        Files.Clear();
        DiffFileItem? reselect = null;
        foreach (var item in items)
        {
            Files.Add(item);
            if (selectedPath is not null
                && string.Equals(item.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase))
                reselect = item;
        }

        EmptyMessage = Files.Count > 0 ? "" : emptyMessage;

        SelectedFile = reselect ?? Files.FirstOrDefault();
        if (SelectedFile is null)
        {
            DiffRows.Clear();
            SideRows.Clear();
        }
    }

    /// <summary>計算し直した一覧が現在の表示と同一か（差分内容の手掛かりになるフィールドまで比較する）。</summary>
    private bool SameAsCurrentFiles(List<DiffFileItem> items)
    {
        if (items.Count != Files.Count)
            return false;
        for (var i = 0; i < items.Count; i++)
        {
            var a = items[i];
            var b = Files[i];
            if (!string.Equals(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase)
                || a.DisplayPath != b.DisplayPath
                || a.Badge != b.Badge
                || a.Stats != b.Stats
                || a.IsAi != b.IsAi
                || a.IsNew != b.IsNew
                || a.IsStaged != b.IsStaged
                || a.OldContent != b.OldContent
                || a.NewContent != b.NewContent
                || !Equals(a.Entry, b.Entry)
                || !Equals(a.CommitFile, b.CommitFile))
                return false;
        }
        return true;
    }

    // ===== AI変更（ジャーナル） =====

    /// <summary>記録をファイル単位に畳む：最初の記録の変更前 × 最後の記録の変更後。</summary>
    private (List<DiffFileItem> Items, string EmptyMessage) BuildAiItems()
    {
        var items = new List<DiffFileItem>();
        foreach (var group in _journal.Snapshot()
                     .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var last = group.Last();
            var oldContent = first.IsNew ? "" : first.OldContent;
            var newContent = last.NewContent;

            var stats = "";
            if (oldContent is not null && newContent is not null)
            {
                var (added, removed) = DiffUtil.Stat(oldContent, newContent);
                stats = $"+{added} −{removed}";
            }

            items.Add(new DiffFileItem
            {
                FullPath = first.Path,
                DisplayPath = ToDisplayPath(first.Path),
                Badge = first.IsNew ? "新規" : "変更",
                Stats = stats,
                IsAi = true,
                IsNew = first.IsNew,
                OldContent = oldContent,
                NewContent = newContent,
            });
        }
        return (items, "AI によるファイル変更はまだありません。");
    }

    // ===== Git作業ツリー =====

    private async Task<(List<DiffFileItem> Items, string EmptyMessage)> BuildGitItemsAsync()
    {
        var status = await _git.GetStatusAsync();
        var items = new List<DiffFileItem>();
        if (!status.IsRepository)
            return (items, "このワークスペースは git リポジトリではありません。");

        var root = _workspace.RootPath ?? "";
        foreach (var (entry, staged) in status.Staged.Select(e => (e, true))
                     .Concat(status.Unstaged.Select(e => (e, false))))
        {
            var badge = entry.IsConflicted ? "U"
                : entry.IsUntracked ? "?"
                : (staged ? entry.IndexStatus : entry.WorkStatus).ToString();
            items.Add(new DiffFileItem
            {
                FullPath = Path.Combine(root, entry.Path),
                DisplayPath = entry.Path,
                Badge = staged ? $"{badge}（staged）" : badge,
                Entry = entry,
                IsStaged = staged,
            });
        }
        return (items, "Git の変更はありません。");
    }

    // ===== Gitコミット範囲 =====

    private async Task<(List<DiffFileItem> Items, string EmptyMessage)> BuildCommitItemsAsync(
        (string? From, string To) range)
    {
        var changes = await _git.GetRangeChangesAsync(range.From, range.To);
        var root = _workspace.RootPath ?? "";
        var items = changes.Select(c => new DiffFileItem
        {
            FullPath = Path.Combine(root, c.Path),
            DisplayPath = c.Path,
            Badge = c.Status.ToString(),
            CommitFile = c,
        }).ToList();
        return (items, "この範囲に変更ファイルはありません。");
    }

    /// <summary>ワークスペースルート配下なら相対パスへ（表示用）。</summary>
    private string ToDisplayPath(string fullPath)
    {
        var root = _workspace.RootPath;
        if (!string.IsNullOrEmpty(root)
            && fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return fullPath[root.Length..].TrimStart('\\', '/').Replace('\\', '/');
        return fullPath;
    }

    // ===== 操作 =====

    [RelayCommand]
    private async Task OpenInEditorAsync(DiffFileItem? item)
    {
        item ??= SelectedFile;
        if (item is null) return;
        try { await _editor.OpenFileAsync(item.FullPath); }
        catch (Exception ex) { SetStatus($"エディタで開けませんでした: {ex.Message}", isError: true); }
    }

    /// <summary>AI変更の巻き戻し：新規作成ならファイル削除、変更なら変更前の全文を書き戻す。</summary>
    [RelayCommand]
    private async Task RevertAsync(DiffFileItem? item)
    {
        item ??= SelectedFile;
        if (item is not { CanRevert: true }) return;

        var detail = item.IsNew ? "AI が新規作成したファイルなので削除されます。" : "AI 変更前の内容へ書き戻します。";
        var answer = MessageBox.Show(
            Application.Current?.MainWindow!,
            $"{item.DisplayPath} の AI 変更を元に戻しますか？\n{detail}",
            "AI変更の巻き戻し", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            if (item.IsNew)
                File.Delete(item.FullPath);
            else
                await File.WriteAllTextAsync(item.FullPath, item.OldContent!);
            _journal.RemoveForPath(item.FullPath);  // Changed 経由で一覧が更新される
            SetStatus($"{item.DisplayPath} を元に戻しました。", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"巻き戻しに失敗しました: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Git 作業ツリー（Current）の変更を破棄する：追跡済みは作業ツリーを復元、未追跡は削除。破壊的。
    /// コミット範囲・AI変更モードでは呼べない（<see cref="CanDiscardSelected"/> で抑止）。
    /// </summary>
    [RelayCommand]
    private async Task DiscardAsync(DiffFileItem? item)
    {
        item ??= SelectedFile;
        if (item?.Entry is not { } entry || _commitRange is not null) return;

        var detail = entry.IsUntracked ? "未追跡ファイルなので削除されます。" : "作業ツリーの変更が失われます。";
        var answer = MessageBox.Show(
            Application.Current?.MainWindow!,
            $"{item.DisplayPath} の変更を破棄しますか？\n{detail}",
            "変更の破棄", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        // 破棄が成功すると GitService が RepositoryChanged を発火し、一覧は自動で読み直される。
        var result = await _git.DiscardAsync(entry);
        if (result.Success)
            SetStatus($"{item.DisplayPath} の変更を破棄しました。", isError: false);
        else
            SetStatus($"破棄に失敗しました: {Truncate(result.Message)}", isError: true);
    }

    private static string Truncate(string text)
    {
        var t = text.Trim();
        return t.Length <= 300 ? t : t[..300] + "…";
    }

    /// <summary>
    /// 統合表示で選択した差分行（<paramref name="selectedRowIndices"/> は <see cref="DiffRows"/> の添字）の変更だけを
    /// 破棄する。選んだ <c>+</c>/<c>-</c> 行を縮約パッチにして <c>git apply --reverse --recount</c> で逆適用する。
    /// </summary>
    public async Task DiscardSelectedLinesAsync(IReadOnlySet<int> selectedRowIndices)
    {
        if (!CanDiscardLines || SelectedFile is not { Entry: not null } item) return;
        if (selectedRowIndices.Count == 0) return;

        // DiffRows は GetPatchTextAsync(item, 3) を改行分割したものと1対1。同じパッチから縮約する。
        var patch = await GetPatchTextAsync(item, 3);
        var reduced = UnifiedPatchEditor.BuildReverseDiscardPatch(patch, selectedRowIndices);
        if (reduced.IsEmpty)
        {
            SetStatus("破棄する変更行が選択されていません（追加・削除行を選んでください）。", isError: false);
            return;
        }

        var answer = MessageBox.Show(
            Application.Current?.MainWindow!,
            $"{item.DisplayPath} の選択した {reduced.DiscardedLineCount} 行ぶんの変更を破棄しますか？\n作業ツリーのその変更が失われます。",
            "選択行の破棄", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        // 適用が成功すると GitService が RepositoryChanged を発火し、一覧・差分は自動で読み直される。
        var result = await _git.ApplyReverseDiscardPatchAsync(reduced.Patch);
        if (result.Success)
            SetStatus($"{item.DisplayPath} の選択した {reduced.DiscardedLineCount} 行を破棄しました。", isError: false);
        else
            SetStatus($"選択行の破棄に失敗しました: {Truncate(result.Message)}", isError: true);
    }

    /// <summary>
    /// 左右並び表示で、変更ブロック（範囲）の旧/新行番号を指定してその範囲の変更だけを破棄する。
    /// <paramref name="oldLines"/> は復活させる削除行の旧行番号、<paramref name="newLines"/> は取り消す追加行の新行番号。
    /// </summary>
    public async Task DiscardSideLinesAsync(IReadOnlySet<int> oldLines, IReadOnlySet<int> newLines)
    {
        if (!CanDiscardLines || SelectedFile is not { Entry: not null } item) return;
        if (oldLines.Count == 0 && newLines.Count == 0) return;

        var patch = await GetPatchTextAsync(item, 3);
        var reduced = UnifiedPatchEditor.BuildReverseDiscardPatchForLines(patch, oldLines, newLines);
        if (reduced.IsEmpty) return;

        var answer = MessageBox.Show(
            Application.Current?.MainWindow!,
            $"{item.DisplayPath} のこの範囲（{reduced.DiscardedLineCount} 行）の変更を破棄しますか？\n作業ツリーのその変更が失われます。",
            "範囲の破棄", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        var result = await _git.ApplyReverseDiscardPatchAsync(reduced.Patch);
        if (result.Success)
            SetStatus($"{item.DisplayPath} のこの範囲（{reduced.DiscardedLineCount} 行）を破棄しました。", isError: false);
        else
            SetStatus($"範囲の破棄に失敗しました: {Truncate(result.Message)}", isError: true);
    }

    /// <summary>AI変更の記録をすべて消す（ファイル自体は変更しない）。</summary>
    [RelayCommand]
    private void ClearAiChanges()
    {
        _journal.Clear();
        SetStatus("AI変更の記録をクリアしました。", isError: false);
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }
}
