using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ファイル一覧ペインの<b>1カラム</b>＝1フォルダーぶんの一覧。ペイン（<see cref="FilesPaneViewModel"/>）は
/// これを最大4つ持ち、1／2／4カラムで見せる。カラムは現在地・履歴・並べ替え・絞り込み・選択を
/// それぞれ独立に持つ——2つ並べて左から右へ移す、が2カラムの主目的なので、状態を共有させない。
///
/// <para>操作の実体（作成・名前変更・削除・貼り付け）はツリーと同じ <see cref="FolderTreeCommandHandler"/>
/// に委譲する。ただしこちらは<b>ワークスペース外でも操作できる版</b>（<c>Unconfined</c>）を使う——
/// ワークスペース配下への限定はエージェントの手綱（§10）であって、人間のファイラに被せるものではない。
/// AI のツールは従来どおり <see cref="IWorkspaceService.ResolvePath"/> を通る。</para></summary>
public sealed partial class FilesColumnViewModel : ObservableObject, IDisposable
{
    private readonly IWorkspaceService _workspace;
    private readonly FolderTreeCommandHandler _commands;
    private readonly IFolderPinStore _pins;
    private readonly IFilePlacesProvider _places;
    private readonly DebouncedFolderWatcher _watcher;

    // 「戻る／進む」の履歴（フルパス）。ブラウザと同じ規則で、新しい移動は進む側を捨てる。
    private readonly List<string> _back = new();
    private readonly List<string> _forward = new();

    // 表示中フォルダーの生の一覧（絞り込み前）。絞り込み・並べ替えの変更で読み直さないために持つ。
    private List<FileEntryViewModel> _all = new();

    public FilesColumnViewModel(
        IWorkspaceService workspace,
        FolderTreeCommandHandler commands,
        IFolderPinStore pins,
        IFilePlacesProvider places)
    {
        _workspace = workspace;
        _commands = commands;
        _pins = pins;
        _places = places;
        _watcher = new DebouncedFolderWatcher(Refresh);
        _pins.PinsChanged += (_, _) => OnPropertyChanged(nameof(CanPinCurrentFolder));
        FileIcons.PaletteChanged += (_, _) =>
        {
            foreach (var entry in Entries)
                entry.RefreshIcon();
        };
    }

    public ObservableCollection<FileEntryViewModel> Entries { get; } = new();

    /// <summary>現在地のパンくず。ワークスペース配下なら所属フォルダー名から、外ならドライブから並べる。</summary>
    public ObservableCollection<FilesBreadcrumb> Breadcrumbs { get; } = new();

    /// <summary>「場所」ポップアップの中身（ワークスペース／ピン留め／クイックアクセス／PC）。
    /// 開いたときに <see cref="LoadPlaces"/> で作り直す——シェルを毎回叩かないため。</summary>
    public ObservableCollection<FilesPlaceGroup> Places { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoUp))]
    [NotifyPropertyChangedFor(nameof(CanPinCurrentFolder))]
    private string _currentFolder = "";

    [ObservableProperty] private FilesSortColumn _sortColumn = FilesSortColumn.Name;

    [ObservableProperty] private bool _sortDescending;

    /// <summary>名前での絞り込み（部分一致。<c>*.cs</c> のようにワイルドカードも書ける）。
    /// 入力欄はツールバーに常設せず、一覧で <c>/</c> を押したときだけ下端に出す。</summary>
    [ObservableProperty] private string _filter = "";

    /// <summary>絞り込みバーを出しているか。効いている間は開いたままにする（閉じると
    /// 虫食いの一覧を「ファイルが消えた」と読み違える）。</summary>
    [ObservableProperty] private bool _isFilterBarOpen;

    /// <summary>絞り込みを解除してバーを畳む（Esc）。</summary>
    public void CloseFilter()
    {
        Filter = "";
        IsFilterBarOpen = false;
    }

    [ObservableProperty] private bool _showHiddenFiles;

    [ObservableProperty] private string _statusText = "";

    [ObservableProperty] private bool _isEmpty = true;

    [ObservableProperty] private string _emptyMessage = "フォルダーが開かれていません。";

    /// <summary>このカラムが操作対象（キーボードの行き先・ペインの現在地）か。</summary>
    [ObservableProperty] private bool _isActive;

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>「上へ」が効くか。ワークスペース外も見られるので、親フォルダーがある限り上がれる。</summary>
    public bool CanGoUp => ParentOf(CurrentFolder) is not null;

    /// <summary>現在地をピン留めできるか（ツリーと共有・ワークスペース配下のみ）。</summary>
    public bool CanPinCurrentFolder => CurrentFolder.Length > 0 && _pins.CanPin(CurrentFolder);

    /// <summary>列見出しに出す並べ替えの向き（現在の列だけ ▲▼ が付く）。</summary>
    public string NameSortMark => MarkFor(FilesSortColumn.Name);
    public string SizeSortMark => MarkFor(FilesSortColumn.Size);
    public string ModifiedSortMark => MarkFor(FilesSortColumn.Modified);
    public string TypeSortMark => MarkFor(FilesSortColumn.Type);

    private string MarkFor(FilesSortColumn column)
        => SortColumn != column ? "" : SortDescending ? " ▼" : " ▲";

    // ファイルを開く要求（ダブルクリック／Enter）。ペイン経由で ShellWindow がエディタタブで開く。
    public event EventHandler<string>? FileActivated;
    public event EventHandler<string>? FilePreviewRequested;
    public event EventHandler<EntryRenamedEventArgs>? EntryRenamed;
    public event EventHandler<string>? EntryDeleted;
    public event EventHandler<TerminalSetRequest>? SetInTerminalRequested;
    public event EventHandler<FileCompareRequest>? CompareRequested;
    public event EventHandler<string>? SearchInFolderRequested;
    public event EventHandler<string>? OpenInBrowserRequested;

    /// <summary>現在地・並べ替え・絞り込みが変わったので保存してほしい。</summary>
    public event EventHandler? StateChanged;

    /// <summary>このカラムが操作された（ペインが現在のカラムを切り替えるため）。</summary>
    public event EventHandler? Activated;

    public void NotifyActivated() => Activated?.Invoke(this, EventArgs.Empty);

    // ===== 現在地とナビゲーション =====

    /// <summary>フォルダーを開く（履歴に積む）。ワークスペースの内外を問わず、実在すれば開ける。</summary>
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

    /// <summary>そのパスの場所を開く（ファイルなら親フォルダー＋その行を選ぶ）。</summary>
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

    /// <summary>View に選ばせたい行（<see cref="Reveal"/> や作成・貼り付けの直後に一度だけ使われる）。</summary>
    [ObservableProperty] private string? _pendingSelection;

    [RelayCommand]
    private void Refresh() => LoadEntries(preserveSelection: true);

    // ===== ピン留め（ツリーと共有・IFolderPinStore） =====
    // 対象は「選んでいるフォルダー行」または現在地。ワークスペース配下だけが対象で、
    // 外の場所は Windows のクイックアクセス側で辿る（ピンはワークスペースの持ち物なので）。

    public bool CanPin(string? path) => path is { Length: > 0 } && _pins.CanPin(path);

    public bool IsPinned(string? path) => path is { Length: > 0 } && _pins.IsPinned(path);

    /// <summary>ピン留め／解除。留まっていれば外し、留められるなら留める。</summary>
    public void TogglePin(string? path)
    {
        if (path is not { Length: > 0 })
            return;
        if (_pins.IsPinned(path))
            _pins.Unpin(path);
        else if (_pins.CanPin(path))
            _pins.Pin(path);
        OnPropertyChanged(nameof(CanPinCurrentFolder));
    }

    /// <summary>「場所」ポップアップを開くときに呼ぶ。ワークスペースフォルダー・ピン留め
    /// （ツリーと共有）・Windows のクイックアクセス・ドライブを、近いものから順に並べる。</summary>
    public void LoadPlaces()
    {
        Places.Clear();

        // ワークスペースとピン留めは名前ではなくフルパスで出す。ここに並ぶのは「自分で選んだ場所」で、
        // フォルダー名は同じものがどこにでもある——どのドライブのどこかまで見えて初めて選べる。
        var workspaceFolders = _workspace.Folders
            .Where(Directory.Exists)
            .Select(folder => new FilesPlace(folder, folder, FilesPlaceKind.WorkspaceFolder))
            .ToList();
        if (workspaceFolders.Count > 0)
            Places.Add(new FilesPlaceGroup("ワークスペース", workspaceFolders));

        var pinned = _pins.AllPins
            .Where(Directory.Exists)
            .Select(path => new FilesPlace(path, path, FilesPlaceKind.Pinned))
            .ToList();
        if (pinned.Count > 0)
            Places.Add(new FilesPlaceGroup("ピン留め", pinned));

        var quickAccess = _places.QuickAccess();
        if (quickAccess.Count > 0)
            Places.Add(new FilesPlaceGroup("クイックアクセス", quickAccess));

        var drives = _places.Drives();
        if (drives.Count > 0)
            Places.Add(new FilesPlaceGroup("PC", drives));
    }

    /// <summary>ワークスペース切替・復元時の入り口。保存してあった現在地へ戻し、無ければ
    /// フォールバック（プライマリフォルダー）を開く。</summary>
    public void Restore(FilesColumnSnapshot? snapshot, string? fallbackFolder)
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
        IsFilterBarOpen = false;

        var target = snapshot?.CurrentFolder;
        if (!TryNormalizeFolder(target, out var folder) && !TryNormalizeFolder(fallbackFolder, out folder))
        {
            CurrentFolder = "";
            _all = new List<FileEntryViewModel>();
            Entries.Clear();
            _watcher.Watch("");
            UpdateBreadcrumbs();
            IsEmpty = true;
            EmptyMessage = "フォルダーが開かれていません。";
            StatusText = "";
            return;
        }
        SetFolder(folder, raiseStateChanged: false);
    }

    public FilesColumnSnapshot Capture() => new()
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

    /// <summary>現在地の親（無ければ null＝ドライブのルート）。</summary>
    private static string? ParentOf(string folder)
    {
        if (string.IsNullOrEmpty(folder))
            return null;
        var parent = Path.GetDirectoryName(folder);
        return string.IsNullOrEmpty(parent) || PathsEqual(parent, folder) ? null : parent;
    }

    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Clear();
        if (CurrentFolder.Length == 0)
            return;

        // 常にドライブから並べる。ここは住所欄なので、まず「フルパスとして読めること」を取る——
        // ワークスペースフォルダーを起点にすると、先頭が C:\Projects\Loomo なのか
        // D:\work\Loomo なのか分からないただの名前になる（同名のフォルダーはどこにでもある）。
        // 狭くて入りきらないぶんは View 側が右端（現在地）へ寄せて見せる。
        var segments = new List<string>();
        var current = CurrentFolder;
        while (true)
        {
            segments.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || PathsEqual(parent, current))
                break;
            current = parent;
        }
        segments.Reverse();

        for (var i = 0; i < segments.Count; i++)
        {
            var path = segments[i];
            Breadcrumbs.Add(new FilesBreadcrumb(NameOf(path), path, i == segments.Count - 1));
        }
    }

    private static string NameOf(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? path : name;
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

    /// <summary>新規作成の親フォルダー（＝現在地）。フォルダーを開いていなければ null。</summary>
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
    /// 貼り付け元・貼り付け先ともワークスペースの内外を問わない（エクスプローラーと同じ）。</summary>
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
    /// プライマリ固定にすると「..\..\」だらけの使えないパスになる）。外のファイルはフルパスのまま。</summary>
    public string RelativePathFor(FileEntryViewModel entry)
    {
        var full = Path.GetFullPath(entry.FullPath);
        var owner = _workspace.FolderFor(full);
        return owner is null ? full : Path.GetRelativePath(owner, full);
    }

    /// <summary>ドロップ先として妥当なフォルダー。行の上ならそのフォルダー、空き領域なら現在地。</summary>
    public string? DropTargetFor(FileEntryViewModel? entry)
    {
        var target = entry is { IsDirectory: true } ? entry.FullPath : CurrentFolder;
        return target.Length > 0 && Directory.Exists(target) ? target : null;
    }

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
        // 検索はワークスペースを対象にする道具なので、外のフォルダーは渡さない。
        if (Directory.Exists(folder) && _workspace.Contains(folder))
            SearchInFolderRequested?.Invoke(this, folder);
    }

    /// <summary>そのフォルダーを検索へ送れるか（コンテキストメニューの出し分け）。</summary>
    public bool CanSearchIn(string folder) => Directory.Exists(folder) && _workspace.Contains(folder);

    private static bool TryNormalizeFolder(string? path, out string folder)
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
        if (!Directory.Exists(full))
            return false;
        folder = full;
        return true;
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _watcher.Dispose();
}
