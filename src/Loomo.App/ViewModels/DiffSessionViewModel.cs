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

    // --- Git変更のとき ---
    public GitChangeEntry? Entry { get; init; }
    public bool IsStaged { get; init; }
}

/// <summary>
/// Diff セッションペインの ViewModel。2つのソースを切り替えて表示する：
/// AI変更（<see cref="IFileChangeJournal"/> が記録した write_file / edit_file の前後全文）と、
/// Git の作業ツリー差分（ステージ済み・未ステージ）。AI変更はファイル単位の巻き戻しに対応する。
/// </summary>
public sealed partial class DiffSessionViewModel : ObservableObject
{
    private readonly IFileChangeJournal _journal;
    private readonly GitService _git;
    private readonly IEditorService _editor;
    private readonly IWorkspaceService _workspace;
    private bool _loaded;

    [ObservableProperty] private bool _isGitMode;
    [ObservableProperty] private DiffFileItem? _selectedFile;
    [ObservableProperty] private string _emptyMessage = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusIsError;

    public ObservableCollection<DiffFileItem> Files { get; } = new();
    public ObservableCollection<DiffRowVm> DiffRows { get; } = new();

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

    /// <summary>Diff ペインが初めて表示されたときに読み込む（以降はジャーナル／リポジトリ変化で追従）。</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _ = RefreshAsync();
    }

    private void DispatchRefresh()
    {
        if (!_loaded) return;
        var app = Application.Current;
        if (app is null) return;
        // ジャーナルはエージェントの実行スレッド、Git はコマンド完了スレッドから届くためディスパッチする
        app.Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync()));
    }

    partial void OnIsGitModeChanged(bool value) => _ = RefreshAsync();

    partial void OnSelectedFileChanged(DiffFileItem? value) => _ = LoadDiffAsync(value);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _loaded = true;
        var selectedPath = SelectedFile?.FullPath;

        var (items, emptyMessage) = IsGitMode ? await BuildGitItemsAsync() : BuildAiItems();

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
            DiffRows.Clear();
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

    private async Task LoadDiffAsync(DiffFileItem? item)
    {
        DiffRows.Clear();
        if (item is null) return;

        if (item.IsAi)
        {
            if (item.OldContent is null || item.NewContent is null)
            {
                DiffRows.Add(new DiffRowVm("Header",
                    "（ファイルが大きいため全文を保持していません。差分を表示できません）"));
                return;
            }
            foreach (var line in DiffUtil.Compute(item.OldContent, item.NewContent))
                DiffRows.Add(new DiffRowVm(line.Kind.ToString(), line.Text));
            return;
        }

        var text = await _git.GetDiffTextAsync(item.Entry!, item.IsStaged);
        if (text.Length == 0)
        {
            DiffRows.Add(new DiffRowVm("Header", "（差分はありません）"));
            return;
        }
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            DiffRows.Add(new DiffRowVm(ClassifyPatchLine(raw), raw));
    }

    /// <summary>git の unified diff 1行を表示種別へ分類する。</summary>
    private static string ClassifyPatchLine(string line)
    {
        if (line.StartsWith("+++", StringComparison.Ordinal)
            || line.StartsWith("---", StringComparison.Ordinal)
            || line.StartsWith("diff ", StringComparison.Ordinal)
            || line.StartsWith("index ", StringComparison.Ordinal)
            || line.StartsWith("new file", StringComparison.Ordinal)
            || line.StartsWith("deleted file", StringComparison.Ordinal)
            || line.StartsWith("rename ", StringComparison.Ordinal)
            || line.StartsWith("#", StringComparison.Ordinal))
            return "Header";
        if (line.StartsWith("@@", StringComparison.Ordinal)) return "Gap";
        if (line.StartsWith("+", StringComparison.Ordinal)) return "Added";
        if (line.StartsWith("-", StringComparison.Ordinal)) return "Removed";
        return "Context";
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
