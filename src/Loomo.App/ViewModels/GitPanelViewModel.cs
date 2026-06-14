using System;
using System.IO;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>サイドバー Git パネルの1変更行。</summary>
public sealed class GitChangeItem
{
    public GitChangeItem(GitChangeEntry entry, bool isStaged)
    {
        Entry = entry;
        IsStaged = isStaged;
        FileName = Path.GetFileName(entry.Path);
        Directory = Path.GetDirectoryName(entry.Path)?.Replace('\\', '/') ?? "";
        Status = entry.IsConflicted ? "U"
            : entry.IsUntracked ? "?"
            : (isStaged ? entry.IndexStatus : entry.WorkStatus).ToString();
    }

    public GitChangeEntry Entry { get; }
    public bool IsStaged { get; }
    public string FileName { get; }
    public string Directory { get; }
    public string Status { get; }
    public bool IsConflicted => Entry.IsConflicted;
    public string ToolTipText => Entry.OrigPath is null
        ? Entry.Path
        : $"{Entry.OrigPath} → {Entry.Path}";
}

/// <summary>
/// サイドバー Git パネルの ViewModel。変更一覧・ステージ操作・コミット・push/pull/fetch を扱う。
/// 複雑な操作（ログツリー・rebase 等）は Git セッションペイン（<see cref="GitSessionViewModel"/>）が担う。
/// </summary>
public sealed partial class GitPanelViewModel : ObservableObject
{
    private readonly GitService _git;
    private readonly IEditorService _editor;

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

    public ObservableCollection<GitChangeItem> Staged { get; } = new();
    public ObservableCollection<GitChangeItem> Unstaged { get; } = new();

    public GitPanelViewModel(GitService git, IEditorService editor)
    {
        _git = git;
        _editor = editor;
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
            Unstaged.Clear();
            return;
        }

        BranchLabel = status.Branch;
        Ahead = status.Ahead;
        Behind = status.Behind;

        Staged.Clear();
        foreach (var entry in status.Staged)
            Staged.Add(new GitChangeItem(entry, isStaged: true));
        Unstaged.Clear();
        foreach (var entry in status.Unstaged)
            Unstaged.Add(new GitChangeItem(entry, isStaged: false));
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
    private Task StageAllAsync() => RunOpAsync("全ステージ", () => _git.StageAllAsync());

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
        await RunOpAsync(amend ? "コミット（amend）" : "コミット", () => _git.CommitAsync(message, amend));
        if (!StatusIsError)
        {
            CommitMessage = "";
            Amend = false;
        }
    }

    /// <summary>変更行クリック：差分（未追跡は内容）をエディタの仮想ドキュメントで開く。</summary>
    [RelayCommand]
    private async Task OpenDiffAsync(GitChangeItem? item)
    {
        if (item is null) return;
        var diff = await _git.GetDiffTextAsync(item.Entry, item.IsStaged);
        await _editor.OpenDocumentAsync(new EditorDocument
        {
            FileName = $"{item.FileName}.diff",
            Content = diff.Length > 0 ? diff : "（差分はありません）",
            OnSaved = _ => { },  // 読み取り専用の用途（:w しても何も永続化しない）
        });
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
}
