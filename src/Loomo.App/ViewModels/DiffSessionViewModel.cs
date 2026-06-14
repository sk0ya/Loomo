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

/// <summary>
/// 左右並び表示の差分1行。Kind は <see cref="SideCellKind"/> 名。
/// Gap / Header は左右共通の1行として全幅表示する（テンプレート側で判定）。
/// </summary>
public sealed record DiffSideRowVm(string LeftKind, string LeftText, string RightKind, string RightText);

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
    /// <summary>差分本体を左右並び（side-by-side）で表示するか。false は統合（unified）表示。</summary>
    [ObservableProperty] private bool _isSideBySide;
    /// <summary>コミット範囲を表示中のヘッダーラベル（空なら作業ツリー表示）。</summary>
    [ObservableProperty] private string _gitTargetLabel = "";
    [ObservableProperty] private DiffFileItem? _selectedFile;
    [ObservableProperty] private string _emptyMessage = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusIsError;

    public ObservableCollection<DiffFileItem> Files { get; } = new();
    public ObservableCollection<DiffRowVm> DiffRows { get; } = new();
    public ObservableCollection<DiffSideRowVm> SideRows { get; } = new();

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
        // AI変更モードへ切り替えたらコミット範囲は解除（戻ったときは作業ツリーから）
        if (!value)
        {
            _commitRange = null;
            GitTargetLabel = "";
        }
        _ = RefreshAsync();
    }

    partial void OnSelectedFileChanged(DiffFileItem? value) => _ = LoadDiffAsync(value);

    // 表示形式の切替時は同じ選択の差分を組み立て直すだけ（一覧は変わらない）
    partial void OnIsSideBySideChanged(bool value) => _ = LoadDiffAsync(SelectedFile);

    /// <summary>
    /// Git セッションから：コミット範囲の差分を表示する（from=null は to 1コミットの変更）。
    /// ペインの表示は呼び出し側（ShellWindow）が行う。
    /// </summary>
    public void ShowCommitRange(string? fromHash, string toHash, string label)
    {
        _loaded = true;
        _commitRange = (fromHash, toHash);
        GitTargetLabel = label;
        if (!IsGitMode)
            IsGitMode = true;  // OnIsGitModeChanged 経由で更新が走る
        else
            _ = RefreshAsync();
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
        GitTargetLabel = "";
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _loaded = true;
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
                await LoadDiffAsync(SelectedFile);
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

    // ===== 差分本体 =====

    /// <summary>読込の世代番号。読込中に選択や一覧が変わったとき、古い結果の適用を捨てる。</summary>
    private int _diffLoadVersion;

    /// <summary>
    /// 差分本体を読み込む。全行を組み立ててから、現在の表示と異なるときだけ差し替える
    /// （Clear→await→再追加だと自動更新のたびに空白が見えてチラつくため）。
    /// 表示中の形式（統合／左右）のコレクションだけを組み立てる。
    /// </summary>
    private async Task LoadDiffAsync(DiffFileItem? item)
    {
        var version = ++_diffLoadVersion;
        if (IsSideBySide)
        {
            var rows = await BuildSideRowsAsync(item);
            if (version != _diffLoadVersion)
                return; // より新しい読込が始まっている
            ReplaceIfChanged(SideRows, rows);
        }
        else
        {
            var rows = await BuildDiffRowsAsync(item);
            if (version != _diffLoadVersion)
                return;
            ReplaceIfChanged(DiffRows, rows);
        }
    }

    /// <summary>同一内容なら再描画しない差し替え（行 VM は record なので値比較）。</summary>
    private static void ReplaceIfChanged<T>(ObservableCollection<T> target, List<T> rows)
    {
        if (rows.Count == target.Count && rows.SequenceEqual(target))
            return;
        target.Clear();
        foreach (var row in rows)
            target.Add(row);
    }

    /// <summary>Git 差分のパッチテキストを取得する（作業ツリー／コミット範囲）。</summary>
    private Task<string> GetPatchTextAsync(DiffFileItem item)
        => item.CommitFile is { } commitFile && _commitRange is { } range
            ? _git.GetRangeFileDiffAsync(range.From, range.To, commitFile)
            : _git.GetDiffTextAsync(item.Entry!, item.IsStaged);

    private const string TooLargeMessage = "（ファイルが大きいため全文を保持していません。差分を表示できません）";
    private const string NoDiffMessage = "（差分はありません）";

    private async Task<List<DiffRowVm>> BuildDiffRowsAsync(DiffFileItem? item)
    {
        var rows = new List<DiffRowVm>();
        if (item is null) return rows;

        if (item.IsAi)
        {
            if (item.OldContent is null || item.NewContent is null)
            {
                rows.Add(new DiffRowVm("Header", TooLargeMessage));
                return rows;
            }
            foreach (var line in DiffUtil.Compute(item.OldContent, item.NewContent))
                rows.Add(new DiffRowVm(line.Kind.ToString(), line.Text));
            return rows;
        }

        var text = await GetPatchTextAsync(item);
        if (text.Length == 0)
        {
            rows.Add(new DiffRowVm("Header", NoDiffMessage));
            return rows;
        }
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            rows.Add(new DiffRowVm(SideBySideDiff.ClassifyPatchLine(raw).ToString(), raw));
        return rows;
    }

    private async Task<List<DiffSideRowVm>> BuildSideRowsAsync(DiffFileItem? item)
    {
        var rows = new List<DiffSideRowVm>();
        if (item is null) return rows;

        if (item.IsAi)
        {
            if (item.OldContent is null || item.NewContent is null)
            {
                rows.Add(SharedRow("Header", TooLargeMessage));
                return rows;
            }
            AddSideRows(rows, SideBySideDiff.Build(DiffUtil.Compute(item.OldContent, item.NewContent)));
            return rows;
        }

        var text = await GetPatchTextAsync(item);
        if (text.Length == 0)
        {
            rows.Add(SharedRow("Header", NoDiffMessage));
            return rows;
        }
        AddSideRows(rows, SideBySideDiff.FromUnifiedPatch(text));
        return rows;
    }

    private static DiffSideRowVm SharedRow(string kind, string text) => new(kind, text, kind, text);

    private static void AddSideRows(List<DiffSideRowVm> rows, IReadOnlyList<SideBySideRow> source)
    {
        foreach (var row in source)
            rows.Add(new DiffSideRowVm(
                row.LeftKind.ToString(), row.LeftText, row.RightKind.ToString(), row.RightText));
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
