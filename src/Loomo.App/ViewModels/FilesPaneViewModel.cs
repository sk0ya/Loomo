using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ファイル一覧（エクスプローラ）ペイン＝<see cref="PaneKind.Files"/> の ViewModel。
///
/// <para>サイドバーのツリー（<see cref="FolderTreeViewModel"/>）を置き換えるものではない。ツリーは
/// <b>階層を把握する</b>道具で、こちらは<b>集合を処理する</b>道具——1フォルダーぶんを平らに並べ、
/// サイズ・更新日時・種類で並べ替え、名前で絞り込み、まとめて選んで操作する。ツリーでは出せない
/// 「さっき触ったのはどれか」「何が重いか」がここで出る。検索がサイドバーとペインの両方に居るのと
/// 同じ立て付け（設計書 §26.1）。</para>
///
/// <para>操作の実体（作成・名前変更・削除・貼り付け）はツリーと同じ <see cref="FolderTreeCommandHandler"/>
/// に委譲する。2系統に分かれると片方だけ直る——書き込みがワークスペースフォルダー配下に限定される
/// （<see cref="IWorkspaceService.ResolvePath"/>）という防御も、共有しているからこそ同じに効く。</para></summary>
public sealed partial class FilesPaneViewModel : ObservableObject, IDisposable
{
    private readonly IWorkspaceService _workspace;
    private readonly FolderTreeCommandHandler _commands;
    private readonly DebouncedFolderWatcher _watcher;

    // 「戻る／進む」の履歴（フルパス）。ブラウザと同じ規則で、新しい移動は進む側を捨てる。
    private readonly List<string> _back = new();
    private readonly List<string> _forward = new();

    // 表示中フォルダーの生の一覧（絞り込み前）。絞り込み・並べ替えの変更で読み直さないために持つ。
    private List<FileEntryViewModel> _all = new();

    public FilesPaneViewModel(IWorkspaceService workspace, FolderTreeCommandHandler commands)
    {
        _workspace = workspace;
        _commands = commands;
        _watcher = new DebouncedFolderWatcher(Refresh);
        _workspace.FoldersChanged += (_, _) => RefreshWorkspaceFolders();
        FileIcons.PaletteChanged += (_, _) =>
        {
            foreach (var entry in Entries)
                entry.RefreshIcon();
        };
    }

    public ObservableCollection<FileEntryViewModel> Entries { get; } = new();

    /// <summary>現在地のパンくず。先頭は所属するワークスペースフォルダー（<see cref="IWorkspaceService.FolderFor"/>）
    /// で、そこから上へは辿らせない——書き込みがワークスペース外へ出られない以上、見せると行き止まりになる。</summary>
    public ObservableCollection<FilesBreadcrumb> Breadcrumbs { get; } = new();

    /// <summary>ワークスペースフォルダーの一覧（マルチルートのときだけ View に出す切替ボタン）。</summary>
    public ObservableCollection<FilesBreadcrumb> WorkspaceFolders { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoUp))]
    private string _currentFolder = "";

    [ObservableProperty] private FilesSortColumn _sortColumn = FilesSortColumn.Name;

    [ObservableProperty] private bool _sortDescending;

    /// <summary>名前での絞り込み（部分一致。<c>*.cs</c> のようにワイルドカードも書ける）。</summary>
    [ObservableProperty] private string _filter = "";

    [ObservableProperty] private bool _showHiddenFiles;

    [ObservableProperty] private string _statusText = "";

    /// <summary>フォルダーが1つも開かれていない（＝ワークスペース未選択）。View は案内文を出す。</summary>
    [ObservableProperty] private bool _isEmpty = true;

    [ObservableProperty] private string _emptyMessage = "フォルダーが開かれていません。";

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>「上へ」が効くか。ワークスペースフォルダー自身まで来たら止まる。</summary>
    public bool CanGoUp => ParentOf(CurrentFolder) is not null;

    /// <summary>列見出しに出す並べ替えの向き（現在の列だけ ▲▼ が付く）。</summary>
    public string NameSortMark => MarkFor(FilesSortColumn.Name);
    public string SizeSortMark => MarkFor(FilesSortColumn.Size);
    public string ModifiedSortMark => MarkFor(FilesSortColumn.Modified);
    public string TypeSortMark => MarkFor(FilesSortColumn.Type);

    private string MarkFor(FilesSortColumn column)
        => SortColumn != column ? "" : SortDescending ? " ▼" : " ▲";

    // ファイルを開く要求（ダブルクリック／Enter）。ShellWindow がエディタタブで開く。
    public event EventHandler<string>? FileActivated;

    // 単クリックのプレビュー要求（編集するまで確定しないタブ）。ツリーと同じ扱い。
    public event EventHandler<string>? FilePreviewRequested;

    // 名前変更・削除の通知。ShellWindow が開いているエディタタブを追従／クローズさせる
    // （ツリーと同じ受け口へ流すので、どちらから操作しても結果は同じ）。
    public event EventHandler<EntryRenamedEventArgs>? EntryRenamed;
    public event EventHandler<string>? EntryDeleted;

    // 素材の流れ（設計書 §24.3）：ターミナルへ送る／Diff へ送る／このフォルダーで検索／ブラウザで開く。
    public event EventHandler<TerminalSetRequest>? SetInTerminalRequested;
    public event EventHandler<FileCompareRequest>? CompareRequested;
    public event EventHandler<string>? SearchInFolderRequested;
    public event EventHandler<string>? OpenInBrowserRequested;

    /// <summary>現在地・並べ替え・絞り込みが変わったので保存してほしい（ShellWindow が購読）。</summary>
    public event EventHandler? StateChanged;

    // ===== 現在地とナビゲーション =====

    /// <summary>フォルダーを開く（履歴に積む）。ワークスペース外・存在しないパスは無視する。</summary>
    public void Navigate(string? path)
    {
        if (!TryNormalizeFolder(path, out var target) || PathsEqual(target, CurrentFolder))
            return;
        if (CurrentFolder.Length > 0)
            _back.Add(CurrentFolder);
        _forward.Clear();
        SetFolder(target);
    }

    [RelayCommand]
    private void OpenFolder(string? path) => Navigate(path);

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (_back.Count == 0)
            return;
        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(CurrentFolder);
        SetFolder(target);
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward()
    {
        if (_forward.Count == 0)
            return;
        var target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(CurrentFolder);
        SetFolder(target);
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void GoUp() => Navigate(ParentOf(CurrentFolder));

    /// <summary>項目を開く。フォルダーなら移動、ファイルならエディタへ。</summary>
    public void OpenEntry(FileEntryViewModel? entry)
    {
        if (entry is null)
            return;
        if (entry.IsDirectory)
            Navigate(entry.FullPath);
        else if (File.Exists(entry.FullPath))
            FileActivated?.Invoke(this, entry.FullPath);
    }

    /// <summary>そのパスの場所を一覧で開く（ファイルなら親フォルダー＋その行を選ぶ）。
    /// 選択は View が <see cref="PendingSelection"/> を見て行う。</summary>
    public void Reveal(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return;
        var full = Path.GetFullPath(fullPath);
        var folder = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        if (folder is null)
            return;
        Navigate(folder);
        PendingSelection = full;
    }

    /// <summary>View に選ばせたい行（<see cref="Reveal"/> 後に一度だけ使われる）。</summary>
    [ObservableProperty] private string? _pendingSelection;

    [RelayCommand]
    private void Refresh() => LoadEntries(preserveSelection: true);

    /// <summary>ワークスペース切替・復元時の入り口。保存してあった現在地へ戻し、無ければ
    /// プライマリフォルダーを開く。</summary>
    public void Restore(FilesPaneSnapshot? snapshot, string? fallbackFolder)
    {
        _back.Clear();
        _forward.Clear();
        NotifyHistoryChanged();

        if (snapshot is not null)
        {
            SortColumn = snapshot.SortColumn;
            SortDescending = snapshot.SortDescending;
            ShowHiddenFiles = snapshot.ShowHidden;
        }
        Filter = "";   // 絞り込みは「今この瞬間の道具」なので持ち越さない

        var target = snapshot?.CurrentFolder;
        if (!TryNormalizeFolder(target, out var folder) && !TryNormalizeFolder(fallbackFolder, out folder))
        {
            CurrentFolder = "";
            _all = new List<FileEntryViewModel>();
            Entries.Clear();
            _watcher.Watch("");
            RefreshWorkspaceFolders();
            UpdateBreadcrumbs();
            IsEmpty = true;
            EmptyMessage = "フォルダーが開かれていません。";
            StatusText = "";
            return;
        }
        SetFolder(folder, raiseStateChanged: false);
    }

    public FilesPaneSnapshot Capture() => new()
    {
        CurrentFolder = CurrentFolder.Length > 0 ? CurrentFolder : null,
        SortColumn = SortColumn,
        SortDescending = SortDescending,
        ShowHidden = ShowHiddenFiles,
    };

    private void SetFolder(string folder, bool raiseStateChanged = true)
    {
        CurrentFolder = folder;
        // 表示するのは直下だけなので再帰監視はしない（リポジトリ全体を見張る必要はない）。
        _watcher.Watch(folder, includeSubdirectories: false);
        RefreshWorkspaceFolders();
        UpdateBreadcrumbs();
        LoadEntries(preserveSelection: false);
        NotifyHistoryChanged();
        if (raiseStateChanged)
            StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        GoUpCommand.NotifyCanExecuteChanged();
    }

    /// <summary>現在地の親（ワークスペースフォルダー自身なら null＝これ以上は上がらない）。</summary>
    private string? ParentOf(string folder)
    {
        if (string.IsNullOrEmpty(folder))
            return null;
        var owner = _workspace.FolderFor(folder);
        if (owner is null || PathsEqual(owner, folder))
            return null;
        var parent = Path.GetDirectoryName(folder);
        return parent is not null && _workspace.Contains(parent) ? parent : null;
    }

    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Clear();
        if (CurrentFolder.Length == 0)
            return;

        var owner = _workspace.FolderFor(CurrentFolder) ?? CurrentFolder;
        var segments = new List<string>();
        var current = CurrentFolder;
        while (!PathsEqual(current, owner))
        {
            segments.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (parent is null || PathsEqual(parent, current))
                break;
            current = parent;
        }
        segments.Add(owner);
        segments.Reverse();

        for (var i = 0; i < segments.Count; i++)
        {
            var path = segments[i];
            var name = Path.GetFileName(path.TrimEnd('\\', '/'));
            Breadcrumbs.Add(new FilesBreadcrumb(
                string.IsNullOrEmpty(name) ? path : name, path, i == segments.Count - 1));
        }
    }

    private void RefreshWorkspaceFolders()
    {
        // マルチルートのときだけ意味を持つ行（1フォルダーならパンくずの先頭と同じ情報になる）。
        var folders = _workspace.Folders;
        WorkspaceFolders.Clear();
        if (folders.Count < 2)
            return;
        var owner = CurrentFolder.Length > 0 ? _workspace.FolderFor(CurrentFolder) : null;
        foreach (var folder in folders)
        {
            var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
            WorkspaceFolders.Add(new FilesBreadcrumb(
                string.IsNullOrEmpty(name) ? folder : name, folder,
                owner is not null && PathsEqual(owner, folder)));
        }
    }

    // ===== 一覧の読み込み・並べ替え・絞り込み =====

    private void LoadEntries(bool preserveSelection)
    {
        if (CurrentFolder.Length == 0 || !Directory.Exists(CurrentFolder))
        {
            _all = new List<FileEntryViewModel>();
            ApplyView(preserveSelection);
            return;
        }

        var items = new List<FileEntryViewModel>();
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(CurrentFolder))
            {
                try
                {
                    var info = Directory.Exists(path) ? new DirectoryInfo(path) : (FileSystemInfo)new FileInfo(path);
                    var isDirectory = info is DirectoryInfo;
                    var hidden = (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                    var size = info is FileInfo file ? file.Length : 0L;
                    items.Add(new FileEntryViewModel(path, isDirectory, size, info.LastWriteTime, hidden));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 読めない項目（別プロセスが消した直後など）はその1件だけ落とす。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _all = new List<FileEntryViewModel>();
            ApplyView(preserveSelection);
            EmptyMessage = "このフォルダーは読み取れません。";
            IsEmpty = true;
            return;
        }

        _all = items;
        ApplyView(preserveSelection);
    }

    private void ApplyView(bool preserveSelection)
    {
        var arranged = FilesListing.Arrange(_all, SortColumn, SortDescending, Filter, ShowHiddenFiles);
        if (preserveSelection)
            Reconcile(arranged);
        else
        {
            Entries.Clear();
            foreach (var entry in arranged)
                Entries.Add(entry);
        }

        var folders = arranged.Count(e => e.IsDirectory);
        var files = arranged.Count - folders;
        var hiddenByFilter = _all.Count - arranged.Count;
        StatusText = CurrentFolder.Length == 0
            ? ""
            : $"{folders} フォルダー・{files} ファイル" + (hiddenByFilter > 0 ? $"（{hiddenByFilter} 件を非表示）" : "");

        IsEmpty = CurrentFolder.Length == 0 || arranged.Count == 0;
        if (CurrentFolder.Length > 0 && arranged.Count == 0)
            EmptyMessage = _all.Count == 0
                ? "このフォルダーは空です。"
                : "絞り込みに一致する項目がありません。";
    }

    /// <summary>監視更新のとき、同じパスの行は既存インスタンスのまま位置と値だけ直す
    /// （作り直すと選択とスクロール位置が飛ぶ）。</summary>
    private void Reconcile(List<FileEntryViewModel> next)
    {
        var existing = new Dictionary<string, FileEntryViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
            existing[entry.FullPath] = entry;

        for (var i = 0; i < next.Count; i++)
        {
            var target = next[i];
            if (existing.TryGetValue(target.FullPath, out var reused))
            {
                reused.Size = target.Size;
                reused.Modified = target.Modified;
                target = reused;
            }

            if (i < Entries.Count && ReferenceEquals(Entries[i], target))
                continue;

            var currentIndex = Entries.IndexOf(target);
            if (currentIndex >= 0)
                Entries.Move(currentIndex, i);
            else
                Entries.Insert(i, target);
        }

        while (Entries.Count > next.Count)
            Entries.RemoveAt(Entries.Count - 1);
    }

    partial void OnFilterChanged(string value) => ApplyView(preserveSelection: true);

    partial void OnShowHiddenFilesChanged(bool value)
    {
        ApplyView(preserveSelection: true);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>列見出しのクリック。同じ列なら向きを反転、別の列なら昇順から。</summary>
    [RelayCommand]
    private void Sort(string? column)
    {
        if (!Enum.TryParse<FilesSortColumn>(column, out var target))
            return;
        if (SortColumn == target)
            SortDescending = !SortDescending;
        else
        {
            SortColumn = target;
            // 日時・サイズは「大きい／新しい方を見たい」ことが多いので、選んだ直後は降順から。
            SortDescending = target is FilesSortColumn.Modified or FilesSortColumn.Size;
        }
        OnPropertyChanged(nameof(NameSortMark));
        OnPropertyChanged(nameof(SizeSortMark));
        OnPropertyChanged(nameof(ModifiedSortMark));
        OnPropertyChanged(nameof(TypeSortMark));
        ApplyView(preserveSelection: true);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ===== ファイル操作（ツリーと同じ FolderTreeCommandHandler へ委譲） =====

    /// <summary>新規作成の親フォルダー（＝現在地）。フォルダー未選択なら null。</summary>
    public string? TargetDirectory => CurrentFolder.Length > 0 && Directory.Exists(CurrentFolder)
        ? CurrentFolder
        : null;

    public string CreateEntry(string name, bool isDirectory)
    {
        var parent = TargetDirectory ?? throw new InvalidOperationException("フォルダーが開かれていません。");
        var created = _commands.Create(parent, name, isDirectory);
        Refresh();
        PendingSelection = created;
        return created;
    }

    public string RenameEntry(FileEntryViewModel entry, string newName)
    {
        var oldPath = Path.GetFullPath(entry.FullPath);
        var newPath = _commands.Rename(oldPath, newName, entry.IsDirectory);
        EntryRenamed?.Invoke(this, new EntryRenamedEventArgs(oldPath, newPath, entry.IsDirectory));
        Refresh();
        PendingSelection = newPath;
        return newPath;
    }

    public void DeleteEntry(FileEntryViewModel entry)
    {
        var path = Path.GetFullPath(entry.FullPath);
        _commands.Delete(path, entry.IsDirectory);
        EntryDeleted?.Invoke(this, path);
        Refresh();
    }

    /// <summary>クリップボード／ドロップされた項目を現在地へコピー（move=false）または移動する。
    /// 貼り付け先はワークスペース配下に限定され、同名は「 - コピー」で一意化される（ツリーと同じ規則）。</summary>
    public string PasteEntry(string sourcePath, bool move) => PasteEntry(CurrentFolder, sourcePath, move);

    public string PasteEntry(string targetDirectory, string sourcePath, bool move)
    {
        var source = Path.GetFullPath(sourcePath);
        var isDirectory = _commands.DirectoryExists(source);
        var destination = _commands.Paste(targetDirectory, source, move);
        if (move)
            EntryRenamed?.Invoke(this, new EntryRenamedEventArgs(source, destination, isDirectory));
        Refresh();
        PendingSelection = destination;
        return destination;
    }

    public string DuplicateEntry(FileEntryViewModel entry)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(entry.FullPath))
            ?? throw new InvalidOperationException("親フォルダーを特定できません。");
        var copied = _commands.Paste(parent, Path.GetFullPath(entry.FullPath), move: false);
        Refresh();
        PendingSelection = copied;
        return copied;
    }

    /// <summary>「相対パスをコピー」用。基準は所属するワークスペースフォルダー（マルチルートで
    /// プライマリ固定にすると「..\..\」だらけの使えないパスになる）。</summary>
    public string RelativePathFor(FileEntryViewModel entry)
    {
        var full = Path.GetFullPath(entry.FullPath);
        var owner = _workspace.FolderFor(full);
        return owner is null ? full : Path.GetRelativePath(owner, full);
    }

    /// <summary>外部（Explorer 等）からのドロップ先として妥当なフォルダー。行の上ならそのフォルダー、
    /// 空き領域なら現在地。</summary>
    public string? DropTargetFor(FileEntryViewModel? entry)
        => entry is { IsDirectory: true } ? entry.FullPath : TargetDirectory;

    public void NotifySelected(string fullPath) => _workspace.SelectedPath = fullPath;

    public void NotifyPreviewRequested(string fullPath)
    {
        if (File.Exists(fullPath))
            FilePreviewRequested?.Invoke(this, fullPath);
    }

    public void RequestOpenInBrowser(string fullPath)
    {
        if (File.Exists(fullPath))
            OpenInBrowserRequested?.Invoke(this, fullPath);
    }

    public void RequestSetInTerminal(FileEntryViewModel entry)
    {
        if (_commands.EntryExists(entry.FullPath, entry.IsDirectory))
            SetInTerminalRequested?.Invoke(this, new TerminalSetRequest(entry.FullPath, entry.IsDirectory));
    }

    /// <summary>Diff ペインでの比較要求。<paramref name="rightPath"/> が null なら
    /// クリップボードとの比較（ツリーの「Diff へ送る」と同じ）。</summary>
    public void RequestCompare(string leftPath, string? rightPath)
    {
        if (!File.Exists(leftPath))
            return;
        if (rightPath is not null && !File.Exists(rightPath))
            return;
        CompareRequested?.Invoke(this, new FileCompareRequest(leftPath, rightPath));
    }

    public void RequestSearchInFolder(string folder)
    {
        if (Directory.Exists(folder))
            SearchInFolderRequested?.Invoke(this, folder);
    }

    private bool TryNormalizeFolder(string? path, out string folder)
    {
        folder = "";
        if (string.IsNullOrWhiteSpace(path))
            return false;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (!Directory.Exists(full) || !_workspace.Contains(full))
            return false;
        folder = full;
        return true;
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _watcher.Dispose();
}
