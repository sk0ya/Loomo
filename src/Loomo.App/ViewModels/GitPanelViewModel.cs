using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>サイドバー Git パネルの1変更行。</summary>
public sealed partial class GitChangeItem : ObservableObject
{
    public GitChangeItem(GitChangeEntry entry, bool isStaged, bool isChecked = false)
    {
        Entry = entry;
        IsStaged = isStaged;
        FileName = Path.GetFileName(entry.Path);
        Directory = Path.GetDirectoryName(entry.Path)?.Replace('\\', '/') ?? "";
        Status = entry.IsConflicted ? "U"
            : entry.IsUntracked ? "?"
            : (isStaged ? entry.IndexStatus : entry.WorkStatus).ToString();
        _isChecked = isChecked;
    }

    public GitChangeEntry Entry { get; }
    public bool IsStaged { get; }
    public string FileName { get; }
    public string Directory { get; }
    public string Status { get; }
    /// <summary>作業ツリーのツリー表示でコミット対象に含めるか。ステージ済みには使わない。</summary>
    [ObservableProperty] private bool _isChecked;
    public bool IsConflicted => Entry.IsConflicted;
    public string ToolTipText => Entry.OrigPath is null
        ? Entry.Path
        : $"{Entry.OrigPath} → {Entry.Path}";
}

/// <summary>ディレクトリ階層と親子連動チェックを持つGit変更ノード。</summary>
public sealed class GitChangeTreeNode : ObservableObject
{
    private bool? _isChecked;
    private bool _isExpanded = true;

    private GitChangeTreeNode(string name, GitChangeTreeNode? parent, GitChangeItem? change)
    {
        Name = name;
        Parent = parent;
        Change = change;
        _isChecked = change?.IsChecked ?? false;
    }

    public string Name { get; private set; }
    public GitChangeTreeNode? Parent { get; private set; }
    public GitChangeItem? Change { get; }
    public bool IsDirectory => Change is null;
    public bool IsSection { get; private set; }
    public int LeafCount { get; private set; }
    public ObservableCollection<GitChangeTreeNode> Children { get; } = new();

    /// <summary>展開中か。ディレクトリ／セクション行のツリーアイテムと TwoWay で結ぶ。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            // 三状態は表示専用。クリック操作は「全選択／全解除」の2状態で循環させる。
            var requested = value is null && _isChecked.HasValue ? !_isChecked.Value : value;
            SetChecked(requested, updateChildren: true, updateParent: true);
        }
    }

    public static GitChangeTreeNode Build(IEnumerable<GitChangeItem> items)
    {
        var root = new GitChangeTreeNode("", null, null);
        foreach (var item in items.OrderBy(i => i.Entry.Path, StringComparer.OrdinalIgnoreCase))
            root.Add(item);
        root.CompactAndSort();
        root.Recalculate();
        return root;
    }

    public static GitChangeTreeNode BuildSection(string name, IEnumerable<GitChangeItem> items, bool expanded = true)
    {
        var materialized = items.ToArray();
        var root = Build(materialized);
        root.Name = name;
        root.IsSection = true;
        root.LeafCount = materialized.Length;
        root.IsExpanded = expanded;
        return root;
    }

    private void Add(GitChangeItem item)
    {
        var parts = item.Entry.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = this;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var name = parts[i];
            var next = current.Children.FirstOrDefault(n => n.IsDirectory
                && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                next = new GitChangeTreeNode(name, current, null);
                current.Children.Add(next);
            }
            current = next;
        }
        current.Children.Add(new GitChangeTreeNode(parts.LastOrDefault() ?? item.FileName, current, item));
    }

    /// <summary>子がディレクトリ1つだけの階層を src/Loomo.App のようにまとめ、縦長化を防ぐ。</summary>
    private void CompactAndSort()
    {
        foreach (var child in Children.ToArray()) child.CompactAndSort();
        if (Parent is not null)
        {
            while (Children.Count == 1 && Children[0].IsDirectory)
            {
                var only = Children[0];
                Name += "/" + only.Name;
                Children.Clear();
                foreach (var grandChild in only.Children)
                {
                    grandChild.Parent = this;
                    Children.Add(grandChild);
                }
            }
        }
        var ordered = Children.OrderByDescending(n => n.IsDirectory)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        Children.Clear();
        foreach (var child in ordered) Children.Add(child);
    }

    private void Recalculate()
    {
        foreach (var child in Children) child.Recalculate();
        UpdateFromChildren();
    }

    private void UpdateFromChildren()
    {
        if (Children.Count == 0) return;
        var first = Children[0].IsChecked;
        SetChecked(Children.All(c => c.IsChecked == first) ? first : null, false, true);
    }

    private void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        // CallerMemberName に頼ると "SetChecked" が通知名になり、IsChecked にバインドした
        // 子・親のチェックボックスへ届かない（連動が見た目に反映されない）ため明示する。
        if (!SetProperty(ref _isChecked, value, nameof(IsChecked))) return;
        if (value.HasValue)
        {
            if (Change is not null) Change.IsChecked = value.Value;
            if (updateChildren)
                foreach (var child in Children)
                    child.SetChecked(value, true, false);
        }
        if (updateParent) Parent?.UpdateFromChildren();
    }
}

/// <summary>
/// サイドバー Git パネルの ViewModel。変更一覧・ステージ操作・コミット・push/pull/fetch を扱う。
/// 複雑な操作（ログツリー・rebase 等）は Git セッションペイン（<see cref="GitSessionViewModel"/>）が担う。
/// </summary>
public sealed partial class GitPanelViewModel : ObservableObject
{
    private readonly GitService _git;
    private readonly IEditorService _editor;
    private readonly IWorkspaceService _workspace;
    private readonly DiffSessionViewModel _diff;

    /// <summary>「差分を開く」操作：Diff ペインの表示を呼び出し側（ShellWindow）へ要求する。</summary>
    public event EventHandler? DiffOpenRequested;

    /// <summary>初回の読込を済ませたか。Git パネルを開くまで遅延する。</summary>
    private bool _loaded;

    [ObservableProperty] private bool _isRepository = true;
    [ObservableProperty] private string _branchLabel = "";
    /// <summary>上流より進んでいるコミット数（プッシュ対象）。0 なら送るものなし。</summary>
    [ObservableProperty] private int _ahead;
    /// <summary>上流より遅れているコミット数（プル対象）。0 なら取り込むものなし。</summary>
    [ObservableProperty] private int _behind;
    [ObservableProperty] private string _commitMessage = "";
    [ObservableProperty] private bool _amend;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusIsError;

    /// <summary>ステージ済みの変更。コミット時はこれがそのまま入る（チェックボックスは持たない）。</summary>
    public ObservableCollection<GitChangeItem> Staged { get; } = new();
    /// <summary>追跡済みの未ステージ変更。チェックでコミット対象に含める。</summary>
    public ObservableCollection<GitChangeItem> Changes { get; } = new();
    /// <summary>Git の管理対象になっていないファイル。チェックでコミット対象に含める。</summary>
    public ObservableCollection<GitChangeItem> UnversionedFiles { get; } = new();
    /// <summary>「変更」「バージョン管理外」をトップレベルに持つ、コミット対象選択用のツリー。</summary>
    [ObservableProperty] private IReadOnlyList<GitChangeTreeNode> _workingTreeSections = Array.Empty<GitChangeTreeNode>();

    // ステージ済みリストの選択スナップショット（コードビハインドの SelectionChanged から差し替える）。
    // 一括アンステージはここを対象にするので、コマンド側は引数を取らない。
    private readonly List<GitChangeItem> _stagedSelection = new();

    /// <summary>ステージ済みリストで選択中の件数（選択件数バーの表示・有効化に使う）。</summary>
    [ObservableProperty] private int _stagedSelectedCount;

    /// <summary>ステージ済みリストの選択をビューから受け取る。</summary>
    public void SetStagedSelection(IList items)
    {
        _stagedSelection.Clear();
        foreach (var o in items)
            if (o is GitChangeItem g) _stagedSelection.Add(g);
        StagedSelectedCount = _stagedSelection.Count;
    }

    public GitPanelViewModel(
        GitService git, IEditorService editor, IWorkspaceService workspace, DiffSessionViewModel diff)
    {
        _git = git;
        _editor = editor;
        _workspace = workspace;
        _diff = diff;
        _git.RepositoryChanged += OnRepositoryChanged;
    }

    private IDisposable? _live;

    /// <summary>Git パネルが見えている間のライブ監視を開始する（手動更新ボタンの代わり）。
    /// 開いた瞬間に最新化し、以降は GitService の軽量ポーリングが変化を取り込む。</summary>
    public void StartLiveTracking()
    {
        if (_live is not null) return;
        _live = _git.TrackLiveChanges();
        _ = RefreshAsync();
    }

    /// <summary>Git パネルが隠れたらライブ監視を止める（見えていない間はチェックしない）。</summary>
    public void StopLiveTracking()
    {
        _live?.Dispose();
        _live = null;
    }

    private void OnRepositoryChanged(object? sender, EventArgs e)
    {
        if (!_loaded) return;
        var app = Application.Current;
        if (app is null) return;
        // 更新系コマンドの完了スレッドは UI とは限らないためディスパッチする
        app.Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync()));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _loaded = true;
        var status = await _git.GetStatusAsync();

        IsRepository = status.IsRepository;
        if (!status.IsRepository)
        {
            BranchLabel = "";
            Ahead = 0;
            Behind = 0;
            Staged.Clear();
            Changes.Clear();
            UnversionedFiles.Clear();
            WorkingTreeSections = Array.Empty<GitChangeTreeNode>();
            return;
        }

        BranchLabel = status.Branch;
        Ahead = status.Ahead;
        Behind = status.Behind;

        // ライブ更新でチェックが勝手に戻らないよう、作業ツリー側はパス単位で選択状態を引き継ぐ。
        var wasChecked = Changes.Concat(UnversionedFiles)
            .Where(i => i.IsChecked).Select(i => i.Entry.Path)
            .ToHashSet(StringComparer.Ordinal);

        Staged.Clear();
        foreach (var entry in status.Staged)
            Staged.Add(new GitChangeItem(entry, isStaged: true));

        Changes.Clear();
        UnversionedFiles.Clear();
        foreach (var entry in status.Unstaged)
        {
            var item = new GitChangeItem(entry, isStaged: false, isChecked: wasChecked.Contains(entry.Path));
            (entry.IsUntracked ? UnversionedFiles : Changes).Add(item);
        }

        // セクションの開閉はユーザー操作を更新間で引き継ぐ。「バージョン管理外ファイル」は既定で畳む。
        var wasExpanded = WorkingTreeSections.ToDictionary(s => s.Name, s => s.IsExpanded, StringComparer.Ordinal);
        var sections = new List<GitChangeTreeNode>(2);
        if (Changes.Count > 0)
            sections.Add(GitChangeTreeNode.BuildSection(
                "変更", Changes, wasExpanded.GetValueOrDefault("変更", true)));
        if (UnversionedFiles.Count > 0)
            sections.Add(GitChangeTreeNode.BuildSection(
                "バージョン管理外ファイル", UnversionedFiles, wasExpanded.GetValueOrDefault("バージョン管理外ファイル", false)));
        WorkingTreeSections = sections;
    }

    [RelayCommand]
    private Task InitRepositoryAsync() => RunOpAsync("git init", () => _git.InitAsync());

    [RelayCommand]
    private Task FetchAsync() => RunOpAsync("フェッチ", () => _git.FetchAsync());

    [RelayCommand]
    private Task PullAsync() => RunOpAsync("プル", () => _git.PullAsync());

    [RelayCommand]
    private Task PushAsync() => RunOpAsync("プッシュ", () => _git.PushAsync());

    [RelayCommand]
    private Task UnstageAllAsync() => RunOpAsync("全アンステージ", () => _git.UnstageAllAsync());

    [RelayCommand]
    private Task StageAsync(GitChangeItem? item) => item is null
        ? Task.CompletedTask
        : RunOpAsync("ステージ", () => _git.StageAsync(item.Entry.Path));

    [RelayCommand]
    private Task UnstageAsync(GitChangeItem? item) => item is null
        ? Task.CompletedTask
        : RunOpAsync("アンステージ", () => _git.UnstageAsync(item.Entry.Path));

    [RelayCommand]
    private async Task DiscardAsync(GitChangeItem? item)
    {
        if (item is null) return;
        var detail = item.Entry.IsUntracked ? "未追跡ファイルなので削除されます。" : "作業ツリーの変更が失われます。";
        var answer = MessageBox.Show(
            Application.Current?.MainWindow!,
            $"{item.Entry.Path} の変更を破棄しますか？\n{detail}",
            "変更の破棄", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        await RunOpAsync("破棄", () => _git.DiscardAsync(item.Entry));
    }

    /// <summary>ステージ済みリストで選択中のファイルをまとめてアンステージする。</summary>
    [RelayCommand]
    private Task UnstageSelectedAsync()
    {
        var paths = _stagedSelection.Select(i => i.Entry.Path).ToArray();
        return paths.Length == 0 ? Task.CompletedTask : RunOpAsync("アンステージ", () => _git.UnstageAsync(paths));
    }

    /// <summary>
    /// コミット。ステージ済みはそのまま、ツリーでチェックした作業ツリーのファイルは
    /// コミット直前にステージしてから含める。amend はメッセージ修正だけでも成立する。
    /// </summary>
    [RelayCommand]
    private async Task CommitAsync()
    {
        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            StatusMessage = "コミットメッセージを入力してください。";
            StatusIsError = true;
            return;
        }
        var message = CommitMessage.Trim();
        var amend = Amend;
        // チェックした作業ツリーのファイルをステージ対象にする。リネームは新旧パスを両方含める。
        var pathsToStage = Changes.Concat(UnversionedFiles).Where(i => i.IsChecked)
            .SelectMany(i => PathsOf(i.Entry)).Distinct(StringComparer.Ordinal).ToArray();
        if (!amend && Staged.Count == 0 && pathsToStage.Length == 0)
        {
            StatusMessage = "コミットするファイルをチェックしてください。";
            StatusIsError = true;
            return;
        }
        await RunOpAsync(amend ? "コミット（amend）" : "コミット", async () =>
        {
            if (pathsToStage.Length > 0)
            {
                var stage = await _git.StageAsync(pathsToStage);
                if (!stage.Success) return stage;
            }
            return await _git.CommitAsync(message, amend);
        });
        if (!StatusIsError)
        {
            CommitMessage = "";
            Amend = false;
        }
    }

    /// <summary>変更行クリック：そのファイルの実体をエディタで開く（差分ではなくファイルそのもの）。
    /// 削除済みエントリは開く実体が無いので何もしない。</summary>
    [RelayCommand]
    private async Task OpenFileAsync(GitChangeItem? item)
    {
        if (item is null) return;
        var root = _workspace.RootPath;
        if (string.IsNullOrEmpty(root)) return;
        var fullPath = Path.Combine(root, item.Entry.Path);
        if (!File.Exists(fullPath)) return;  // 削除済みなど、開く実体が無い
        await _editor.OpenFileAsync(fullPath);
    }

    /// <summary>変更行のコンテキストメニュー「差分を開く」：その作業ツリー差分を Diff ペインで開く。</summary>
    [RelayCommand]
    private async Task OpenDiffAsync(GitChangeItem? item)
    {
        if (item is null) return;
        await _diff.ShowWorkingTreeFileAsync(item.Entry, item.IsStaged);
        DiffOpenRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>更新系操作の共通枠：多重実行の抑止・結果メッセージの表示。</summary>
    private async Task RunOpAsync(string label, Func<Task<GitCommandResult>> operation)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = $"{label}を実行中…";
        StatusIsError = false;
        try
        {
            var result = await operation();
            StatusIsError = !result.Success;
            StatusMessage = result.Success
                ? $"{label}が完了しました。"
                : Truncate(result.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Truncate(string text)
    {
        var t = text.Trim();
        return t.Length <= 300 ? t : t[..300] + "…";
    }

    /// <summary>pathspec 用のパス。リネームは旧パスも含め、旧パスの削除を同じコミットへ入れる。</summary>
    private static IEnumerable<string> PathsOf(GitChangeEntry entry)
    {
        if (entry.OrigPath is not null) yield return entry.OrigPath;
        yield return entry.Path;
    }
}
