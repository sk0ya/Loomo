using System.IO;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Files;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>リポジトリの帯の1項目（§40.8.3）。Git 管理外のワークスペースフォルダーも項目にする（init／clone の入口）。</summary>
/// <param name="Root">作業ツリーの根（管理外ならワークスペースフォルダー）。Git の対象にする場所。</param>
/// <param name="Label">見出し（入れ子のリポジトリはワークスペースフォルダーからの相対）。</param>
/// <param name="Chip">リポジトリの印（管理外なら null）。</param>
/// <param name="Indent">字下げの段（入れ子のリポジトリは 1）。</param>
public sealed record GitRepositoryStripItem(string Root, string Label, RepositoryChipViewModel? Chip, bool IsRepository, int Indent)
{
    public System.Windows.Thickness IndentMargin => new(Indent * 12, 0, 0, 0);
}

/// <summary>
/// Git パネル・Git ペイン・ブランチ切替が見ている<b>リポジトリの帯</b>（§40.8.3・§40.8.4）。以前の「対象フォルダーの
/// ComboBox」（旧 GitRootSwitchViewModel）の置き換えで、ワークスペースの全リポジトリ（入れ子を含む）を印付きで並べ、
/// 今どれを見ているかを常に見せる。選べば Git の対象（<see cref="GitService.SetActiveRoot"/>）をそのリポジトリへ。
///
/// <para>ファイル起点の操作（履歴・コミットの中のファイル）は、そのファイルのリポジトリを対象にしないと正しく動かない
/// ので対象を切り替える——ただし<b>見える形で</b>：帯の選択が動き、<see cref="SwitchNotice"/> が数秒出る。以前は
/// ComboBox の中身が黙って替わるだけで、Git パネルの変更一覧が理由なく入れ替わったように見えていた。</para>
/// </summary>
public sealed partial class GitRepositoryStripViewModel : ObservableObject
{
    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(4);

    private readonly GitService _git;
    private readonly IWorkspaceService _workspace;
    private readonly GitRepositoryChips? _chips;
    private bool _suppressSelection;
    private bool _selecting;
    private int _rebuildGeneration;
    private DispatcherTimer? _noticeTimer;

    [ObservableProperty] private IReadOnlyList<GitRepositoryStripItem> _items = Array.Empty<GitRepositoryStripItem>();
    [ObservableProperty] private GitRepositoryStripItem? _selectedItem;
    /// <summary>ファイル起点の操作で対象が替わったときの一言（数秒で消える）。</summary>
    [ObservableProperty] private string _switchNotice = "";

    public GitRepositoryStripViewModel(GitService git, IWorkspaceService workspace, GitRepositoryChips? chips = null)
    {
        _git = git;
        _workspace = workspace;
        _chips = chips;
        _git.ActiveRootChanged += (_, _) => UiDispatch.Post(OnActiveRootChanged);
        _git.Repositories.Changed += (_, _) => Rebuild();
        _workspace.FoldersChanged += (_, _) => Rebuild();
        Rebuild();
    }

    /// <summary>項目が2つ以上あるか。1つ（ふつうの1リポジトリの部屋）なら帯は出さない。</summary>
    public bool IsMulti => Items.Count > 1;

    /// <summary>見ているリポジトリの見出し（タイトルバーのブランチの前に出す）。</summary>
    public string SelectedLabel => SelectedItem?.Label ?? "";

    /// <summary>項目を作り直す（一覧の作成は背景で）。</summary>
    public void Rebuild()
    {
        var generation = ++_rebuildGeneration;
        var folders = _workspace.Folders.ToArray();
        _ = Task.Run(() => Build(folders, _git.Repositories.Repositories))
            .ContinueWith(task =>
            {
                if (task.IsFaulted || generation != _rebuildGeneration) return;
                UiDispatch.Post(() => Apply(task.Result));
            }, TaskScheduler.Default);
    }

    /// <summary>項目を組む（純関数・テスト用に internal）：ワークスペースフォルダーの順に、フォルダーを含むリポジトリ
    /// （無ければ管理外のフォルダー）→その下の入れ子のリポジトリ。</summary>
    internal IReadOnlyList<GitRepositoryStripItem> Build(IReadOnlyList<string> folders, IReadOnlyList<GitRepositoryInfo> repositories)
    {
        var items = new List<GitRepositoryStripItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in folders)
        {
            var folder = WorkspacePaths.Normalize(raw);
            var enclosing = repositories
                .Where(r => WorkspacePaths.IsWithin(r.Root, folder))
                .OrderByDescending(r => r.Root.Length)
                .FirstOrDefault();
            if (enclosing is not null)
            {
                if (seen.Add(enclosing.Root))
                    items.Add(new GitRepositoryStripItem(enclosing.Root, enclosing.Name, _chips?.For(enclosing.Root), true, 0));
            }
            else if (seen.Add(folder))
            {
                items.Add(new GitRepositoryStripItem(folder, $"{WorkspacePaths.FolderName(folder)}（Git 管理外）", null, false, 0));
            }
            foreach (var nested in repositories.Where(r => !string.Equals(r.Root, folder, StringComparison.OrdinalIgnoreCase)
                                                           && WorkspacePaths.IsWithin(folder, r.Root)))
            {
                if (!seen.Add(nested.Root)) continue;
                var label = Path.GetRelativePath(folder, nested.Root).Replace(Path.DirectorySeparatorChar, '/');
                items.Add(new GitRepositoryStripItem(nested.Root, label, _chips?.For(nested.Root), true, 1)); // 見出し（フォルダー）の下に字下げ
            }
        }
        return items;
    }

    internal void Apply(IReadOnlyList<GitRepositoryStripItem> items)
    {
        _suppressSelection = true;
        try
        {
            Items = items;
            SelectedItem = Find(items);
        }
        finally
        {
            _suppressSelection = false;
        }
        OnPropertyChanged(nameof(IsMulti));
        OnPropertyChanged(nameof(SelectedLabel));
    }

    partial void OnSelectedItemChanged(GitRepositoryStripItem? value)
    {
        OnPropertyChanged(nameof(SelectedLabel));
        if (_suppressSelection || value is null)
            return;
        _selecting = true;
        try { _git.SetActiveRoot(value.Root); }
        finally { _selecting = false; }
    }

    private void OnActiveRootChanged()
    {
        var next = Find(Items);
        if (ReferenceEquals(next, SelectedItem))
            return;
        var initiatedHere = _selecting;
        _suppressSelection = true;
        try { SelectedItem = next; }
        finally { _suppressSelection = false; }
        // 帯で選んだのではない切替（ファイル起点の操作・印のクリック）は一言添える。
        if (!initiatedHere && next is not null && IsMulti)
            ShowNotice($"{next.Label} に切り替えました");
    }

    private GitRepositoryStripItem? Find(IReadOnlyList<GitRepositoryStripItem> items)
    {
        if (_git.RootPath is not { } current) return null;
        var normalized = WorkspacePaths.Normalize(current);
        return items.FirstOrDefault(i => string.Equals(i.Root, normalized, StringComparison.OrdinalIgnoreCase))
               ?? items.Where(i => WorkspacePaths.IsWithin(i.Root, normalized))
                   .OrderByDescending(i => i.Root.Length).FirstOrDefault();
    }

    private void ShowNotice(string text)
    {
        SwitchNotice = text;
        if (System.Windows.Application.Current?.Dispatcher is not { } dispatcher) return;
        _noticeTimer ??= new DispatcherTimer(NoticeDuration, DispatcherPriority.Background,
            (_, _) => { SwitchNotice = ""; _noticeTimer!.Stop(); }, dispatcher);
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }
}
