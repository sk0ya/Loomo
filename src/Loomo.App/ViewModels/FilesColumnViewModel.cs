using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;
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
    private readonly FolderTreeViewModel? _folderTree;
    private readonly IFilePlacesProvider _places;
    private readonly IFileThumbnailService? _thumbnails;
    private readonly DebouncedFolderWatcher _watcher;
    private CancellationTokenSource? _thumbnailCts;
    private int _thumbnailGeneration;
    private CancellationTokenSource? _gitStatusCts;

    // 「戻る／進む」の履歴（フルパス）。ブラウザと同じ規則で、新しい移動は進む側を捨てる。
    private readonly List<string> _back = new();
    private readonly List<string> _forward = new();

    // 表示中フォルダーの生の一覧（絞り込み前）。絞り込み・並べ替えの変更で読み直さないために持つ。
    private List<FileEntryViewModel> _all = new();
    private readonly Dictionary<string, FilesColumnLayoutSnapshot> _folderLayouts =
        new(StringComparer.OrdinalIgnoreCase);
    private List<FilesColumnSettingSnapshot> _legacyLayout = new();
    private bool _restoringLayout;
    private readonly FilesGroupDescription _groupDescription;

    public FilesColumnViewModel(
        IWorkspaceService workspace,
        FolderTreeCommandHandler commands,
        IFolderPinStore pins,
        IFilePlacesProvider places,
        FolderTreeViewModel? folderTree = null,
        IFileThumbnailService? thumbnails = null,
        RecentItemsViewModel? recent = null)
    {
        _workspace = workspace;
        _commands = commands;
        _pins = pins;
        _folderTree = folderTree;
        _places = places;
        _thumbnails = thumbnails;
        Recent = recent;
        _watcher = new DebouncedFolderWatcher(Refresh);
        if (_folderTree is not null)
            _folderTree.GitStatusChanged += OnGitStatusChanged;
        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        _groupDescription = new FilesGroupDescription(this);
        foreach (var setting in CreateColumnSettings())
        {
            setting.PropertyChanged += OnColumnSettingPropertyChanged;
            ColumnSettings.Add(setting);
        }
        _pins.PinsChanged += (_, _) => OnPropertyChanged(nameof(CanPinCurrentFolder));
        FileIcons.PaletteChanged += (_, _) =>
        {
            foreach (var entry in Entries)
                entry.RefreshIcon();
        };
    }

    public ObservableCollection<FileEntryViewModel> Entries { get; } = new();

    /// <summary>行は <see cref="Entries"/> のままにし、表示側だけをグループ化するビュー。
    /// ListBox の選択項目が常に <see cref="FileEntryViewModel"/> であることを保つ。</summary>
    public ICollectionView EntriesView { get; }

    /// <summary>現在地のパンくず（住所欄）。ワークスペースの内外を問わず、常にドライブから並べる。</summary>
    public ObservableCollection<FilesBreadcrumb> Breadcrumbs { get; } = new();

    /// <summary>「場所」ポップアップの中身（ワークスペース／ピン留め／クイックアクセス／PC）。
    /// 開いたときに <see cref="LoadPlaces"/> で作り直す——シェルを毎回叩かないため。</summary>
    public ObservableCollection<FilesPlaceGroup> Places { get; } = new();

    /// <summary>場所Expanderへ最近／頻繁項目を供給する共有状態。開くたびに場所一覧へ反映する。</summary>
    public RecentItemsViewModel? Recent { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoUp))]
    [NotifyPropertyChangedFor(nameof(CanPinCurrentFolder))]
    private string _currentFolder = "";

    [ObservableProperty] private FilesSortColumn _sortColumn = FilesSortColumn.Name;

    [ObservableProperty] private bool _sortDescending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupByLabel))]
    private FilesGroupBy _groupBy;

    public IReadOnlyList<FilesGroupByOption> GroupByOptions => FilesGrouping.Options;

    /// <summary>グループ化・表示形式は素の ComboBox ではなく、ツールバーのアイコン＋ポップアップで
    /// 選ぶ（OS 既定のコンボは配色も行間もこのペインの作法から外れる）。開閉状態はここが持つ。</summary>
    [ObservableProperty] private bool _isGroupMenuOpen;

    [ObservableProperty] private bool _isDisplayMenuOpen;

    /// <summary>ツールチップに出す、いま選んでいるグループ化の名前。</summary>
    public string GroupByLabel =>
        GroupByOptions.FirstOrDefault(option => option.Value == GroupBy)?.Label ?? "グループ化なし";

    /// <summary>ツールチップに出す、いま選んでいる表示形式の名前。</summary>
    public string DisplayModeLabel =>
        DisplayModeOptions.FirstOrDefault(option => option.Value == DisplayMode)?.Label ?? "詳細";

    [RelayCommand]
    private void SelectGroupBy(FilesGroupByOption? option)
    {
        if (option is null)
            return;
        GroupBy = option.Value;
        IsGroupMenuOpen = false;
    }

    [RelayCommand]
    private void SelectDisplayMode(FilesDisplayModeOption? option)
    {
        if (option is null)
            return;
        DisplayMode = option.Value;
        IsDisplayMenuOpen = false;
    }

    /// <summary>このカラムの一覧表示形式。現在地・並べ替えと同じくワークスペースごとに保存する。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayModeLabel))]
    private FilesDisplayMode _displayMode = FilesDisplayMode.Details;

    public IReadOnlyList<FilesDisplayModeOption> DisplayModeOptions => FilesDisplayModes.Options;

    /// <summary>詳細表示の列。順序はこのコレクション、表示・幅は各項目が持つ。</summary>
    public ObservableCollection<FilesColumnSetting> ColumnSettings { get; } = new();

    [ObservableProperty] private bool _isColumnSettingsOpen;

    public IReadOnlyList<FilesColumnSetting> VisibleColumnSettings
        => ColumnSettings.Where(setting => setting.IsVisible).ToList();

    public int NameColumnIndex => VisibleColumnIndex(FilesColumnKey.Name);
    public int SizeColumnIndex => VisibleColumnIndex(FilesColumnKey.Size);
    public int ModifiedColumnIndex => VisibleColumnIndex(FilesColumnKey.Modified);
    public int TypeColumnIndex => VisibleColumnIndex(FilesColumnKey.Type);
    public bool IsNameColumnVisible => IsColumnVisible(FilesColumnKey.Name);
    public bool IsSizeColumnVisible => IsColumnVisible(FilesColumnKey.Size);
    public bool IsModifiedColumnVisible => IsColumnVisible(FilesColumnKey.Modified);
    public bool IsTypeColumnVisible => IsColumnVisible(FilesColumnKey.Type);

    public GridLength Slot0Width => SlotWidth(0);
    public GridLength Slot1Width => SlotWidth(1);
    public GridLength Slot2Width => SlotWidth(2);
    public GridLength Slot3Width => SlotWidth(3);

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
    public event EventHandler<EntryRenamedEventArgs>? EntryRenamed;
    public event EventHandler<string>? EntryDeleted;
    public event EventHandler<TerminalSetRequest>? SetInTerminalRequested;
    public event EventHandler<FileCompareRequest>? CompareRequested;
    public event EventHandler<string>? SearchInFolderRequested;
    public event EventHandler<FileAiRequest>? FileAiRequested;
    public event EventHandler<string>? OpenInBrowserRequested;
    public event EventHandler<string>? FolderNavigated;

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

        var workspaceFolders = _workspace.Folders
            .Where(Directory.Exists)
            .Select(folder => new FilesPlace(NameOf(folder), folder, FilesPlaceKind.WorkspaceFolder))
            .ToList();
        if (workspaceFolders.Count > 0)
            Places.Add(new FilesPlaceGroup("ワークスペース", workspaceFolders));

        var pinned = _pins.AllPins
            .Where(Directory.Exists)
            .Select(path => new FilesPlace(LabelForPin(path), path, FilesPlaceKind.Pinned))
            .ToList();
        if (pinned.Count > 0)
            Places.Add(new FilesPlaceGroup("ピン留め", pinned));

        var recentFiles = Recent?.RecentFiles
            .Where(item => File.Exists(item.FullPath))
            .Select(item => new FilesPlace(item.Name, item.FullPath, FilesPlaceKind.RecentFile))
            .ToList() ?? [];
        if (recentFiles.Count > 0)
            Places.Add(new FilesPlaceGroup("最近使ったファイル", recentFiles));

        var frequentFolders = Recent?.FrequentFolders
            .Where(item => Directory.Exists(item.FullPath))
            .Select(item => new FilesPlace(item.Name, item.FullPath, FilesPlaceKind.FrequentFolder))
            .ToList() ?? [];
        if (frequentFolders.Count > 0)
            Places.Add(new FilesPlaceGroup("よく使うフォルダー", frequentFolders));

        var quickAccess = _places.QuickAccess();
        if (quickAccess.Count > 0)
            Places.Add(new FilesPlaceGroup("クイックアクセス", quickAccess));

        var drives = _places.Drives();
        if (drives.Count > 0)
            Places.Add(new FilesPlaceGroup("PC", drives));
    }

    /// <summary>場所Expanderの項目を開く。最近使ったファイルだけは直接エディタへ渡す。</summary>
    public void OpenPlace(FilesPlace? place)
    {
        if (place is null)
            return;
        if (place.Kind == FilesPlaceKind.RecentFile)
        {
            if (File.Exists(place.FullPath))
                FileActivated?.Invoke(this, place.FullPath);
            return;
        }
        Navigate(place.FullPath);
    }

    /// <summary>ピンの表示名。所属ワークスペースフォルダーからの相対パスで、同名フォルダーを区別する。</summary>
    private string LabelForPin(string path)
    {
        var owner = _workspace.FolderFor(path);
        if (owner is null)
            return path;
        var relative = Path.GetRelativePath(owner, path);
        return _workspace.Folders.Count > 1 ? $"{NameOf(owner)}/{relative.Replace('\\', '/')}" : relative.Replace('\\', '/');
    }

    /// <summary>ワークスペース切替・復元時の入り口。保存してあった現在地へ戻し、無ければ
    /// フォールバック（プライマリフォルダー）を開く。</summary>
    public void Restore(FilesColumnSnapshot? snapshot, string? fallbackFolder)
    {
        _restoringLayout = true;
        _folderLayouts.Clear();
        if (snapshot?.FolderColumnSettings is not null)
            foreach (var pair in snapshot.FolderColumnSettings)
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                    _folderLayouts[pair.Key] = pair.Value;
        _legacyLayout = snapshot?.ColumnSettings?.ToList() ?? new();

        _back.Clear();
        _forward.Clear();
        NotifyHistoryChanged();

        if (snapshot is not null)
        {
            SortColumn = snapshot.SortColumn;
            SortDescending = snapshot.SortDescending;
            GroupBy = FilesGrouping.Normalize(snapshot.GroupBy);
            ShowHiddenFiles = snapshot.ShowHidden;
            DisplayMode = FilesDisplayModes.Normalize(snapshot.DisplayMode);
        }
        else
        {
            DisplayMode = FilesDisplayMode.Details;
            GroupBy = FilesGroupBy.None;
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
            _restoringLayout = false;
            return;
        }
        SetFolder(folder, raiseStateChanged: false);
        if (_folderLayouts.ContainsKey(folder))
            ApplyFolderLayout(folder);
        else if (_legacyLayout.Count > 0)
            ApplyLayout(_legacyLayout);
        else
            ResetLayout();
        _restoringLayout = false;
    }

    public FilesColumnSnapshot Capture()
    {
        SaveFolderLayout(CurrentFolder);
        return new FilesColumnSnapshot
        {
            CurrentFolder = CurrentFolder.Length > 0 ? CurrentFolder : null,
            SortColumn = SortColumn,
            SortDescending = SortDescending,
            GroupBy = GroupBy,
            ShowHidden = ShowHiddenFiles,
            DisplayMode = DisplayMode,
            ColumnSettings = CaptureLayout().Columns,
            FolderColumnSettings = _folderLayouts.ToDictionary(
                pair => pair.Key,
                pair => new FilesColumnLayoutSnapshot
                {
                    Columns = pair.Value.Columns.Select(CloneSetting).ToList(),
                }, StringComparer.OrdinalIgnoreCase),
        };
    }

    private void SetFolder(string folder, bool raiseStateChanged = true)
    {
        CancelThumbnailLoads();
        CancelGitStatusLoad();
        if (!_restoringLayout)
            SaveFolderLayout(CurrentFolder);
        CurrentFolder = folder;
        if (!_restoringLayout)
            ApplyFolderLayout(folder);
        // 表示するのは直下だけなので再帰監視はしない（リポジトリ全体を見張る必要はない）。
        _watcher.Watch(folder, includeSubdirectories: false);
        UpdateBreadcrumbs();
        LoadEntries(preserveSelection: false);
        NotifyHistoryChanged();
        if (raiseStateChanged)
        {
            FolderNavigated?.Invoke(this, folder);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnGitStatusChanged(object? sender, EventArgs e)
    {
        if (CurrentFolder.Length == 0)
            return;

        StartGitStatusLoad(_all, CurrentFolder);
    }

    /// <summary>「種類」列が拡張子（<c>MD</c>）を出していた頃の既定幅。保存済みレイアウトの
    /// 引き上げ判定にだけ使う（<see cref="ApplyLayout"/>）。</summary>
    private const double LegacyTypeColumnWidth = 72;

    private static IEnumerable<FilesColumnSetting> CreateColumnSettings()
    {
        yield return new FilesColumnSetting(FilesColumnKey.Name, "名前", 240, canHide: false);
        yield return new FilesColumnSetting(FilesColumnKey.Size, "サイズ", 86, canHide: true);
        yield return new FilesColumnSetting(FilesColumnKey.Modified, "更新日時", 124, canHide: true);
        yield return new FilesColumnSetting(FilesColumnKey.Type, "種類", 150, canHide: true);
    }

    private void OnColumnSettingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is FilesColumnSetting setting && e.PropertyName == nameof(FilesColumnSetting.IsVisible)
            && !setting.CanHide && !setting.IsVisible)
            setting.IsVisible = true;

        NotifyColumnLayoutChanged();
        if (!_restoringLayout)
        {
            SaveFolderLayout(CurrentFolder);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void NotifyColumnLayoutChanged()
    {
        OnPropertyChanged(nameof(VisibleColumnSettings));
        OnPropertyChanged(nameof(NameColumnIndex));
        OnPropertyChanged(nameof(SizeColumnIndex));
        OnPropertyChanged(nameof(ModifiedColumnIndex));
        OnPropertyChanged(nameof(TypeColumnIndex));
        OnPropertyChanged(nameof(IsNameColumnVisible));
        OnPropertyChanged(nameof(IsSizeColumnVisible));
        OnPropertyChanged(nameof(IsModifiedColumnVisible));
        OnPropertyChanged(nameof(IsTypeColumnVisible));
        OnPropertyChanged(nameof(Slot0Width));
        OnPropertyChanged(nameof(Slot1Width));
        OnPropertyChanged(nameof(Slot2Width));
        OnPropertyChanged(nameof(Slot3Width));
    }

    private int VisibleColumnIndex(FilesColumnKey key)
        => Math.Max(0, VisibleColumnSettings.ToList().FindIndex(setting => setting.Key == key));

    private bool IsColumnVisible(FilesColumnKey key)
        => ColumnSettings.Any(setting => setting.Key == key && setting.IsVisible);

    private GridLength SlotWidth(int index)
    {
        var visible = VisibleColumnSettings;
        return new GridLength(index < visible.Count ? visible[index].Width : 0);
    }

    public void SetColumnWidth(FilesColumnKey key, double width)
    {
        var setting = ColumnSettings.FirstOrDefault(candidate => candidate.Key == key);
        if (setting is null)
            return;
        setting.Width = ClampWidth(setting, width);
    }

    public double ColumnWidth(FilesColumnKey key)
        => ColumnSettings.FirstOrDefault(candidate => candidate.Key == key)?.Width ?? 0;

    [RelayCommand]
    private void MoveColumnUp(FilesColumnSetting? setting)
    {
        if (setting is null)
            return;
        var index = ColumnSettings.IndexOf(setting);
        if (index <= 0)
            return;
        ColumnSettings.Move(index, index - 1);
        NotifyColumnLayoutChanged();
        SaveFolderLayout(CurrentFolder);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void MoveColumnDown(FilesColumnSetting? setting)
    {
        if (setting is null)
            return;
        var index = ColumnSettings.IndexOf(setting);
        if (index < 0 || index >= ColumnSettings.Count - 1)
            return;
        ColumnSettings.Move(index, index + 1);
        NotifyColumnLayoutChanged();
        SaveFolderLayout(CurrentFolder);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SaveFolderLayout(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;
        _folderLayouts[folder] = CaptureLayout();
    }

    private void ApplyFolderLayout(string folder)
    {
        if (_folderLayouts.TryGetValue(folder, out var saved))
            ApplyLayout(saved.Columns);
        else
            ResetLayout();
    }

    private FilesColumnLayoutSnapshot CaptureLayout() => new()
    {
        Columns = ColumnSettings.Select(setting => new FilesColumnSettingSnapshot
        {
            Key = setting.Key,
            IsVisible = setting.IsVisible,
            Width = setting.Width,
        }).ToList(),
    };

    private static FilesColumnSettingSnapshot CloneSetting(FilesColumnSettingSnapshot setting)
        => new() { Key = setting.Key, IsVisible = setting.IsVisible, Width = setting.Width };

    private void ApplyLayout(IEnumerable<FilesColumnSettingSnapshot> saved)
    {
        var savedList = saved.ToList();
        var byKey = savedList
            .GroupBy(item => item.Key)
            .ToDictionary(group => group.Key, group => group.First());
        var orderedKeys = savedList
            .Select(item => item.Key)
            .Where(key => ColumnSettings.Any(setting => setting.Key == key))
            .Distinct()
            .ToList();
        orderedKeys.AddRange(ColumnSettings.Select(setting => setting.Key)
            .Where(key => !orderedKeys.Contains(key)));
        var ordered = orderedKeys
            .Select(key => ColumnSettings.Single(setting => setting.Key == key))
            .ToList();

        _restoringLayout = true;
        try
        {
            for (var i = 0; i < ordered.Count; i++)
                ColumnSettings.Move(ColumnSettings.IndexOf(ordered[i]), i);
            foreach (var setting in ColumnSettings)
            {
                if (!byKey.TryGetValue(setting.Key, out var item))
                {
                    setting.IsVisible = true;
                    setting.Width = setting.DefaultWidth;
                    continue;
                }
                setting.IsVisible = setting.Key == FilesColumnKey.Name || item.IsVisible;
                var width = item.Width;
                // 「種類」列の中身は拡張子（MD）から、エクスプローラーと同じ種類名
                // （Markdown ソース ファイル）へ変わった。旧既定幅のまま保存されている
                // レイアウトは、変更前の中身に合わせた幅なので新しい既定幅へ引き上げる
                // ——そのままだと保存済みのフォルダーだけ種類が読めないまま残る。
                // 自分で幅を変えた列（旧既定幅と違う値）はユーザーの指定なので触らない。
                if (setting.Key == FilesColumnKey.Type && Math.Abs(width - LegacyTypeColumnWidth) < 0.5)
                    width = setting.DefaultWidth;
                setting.Width = ClampWidth(setting, width > 0 ? width : setting.DefaultWidth);
            }
        }
        finally
        {
            _restoringLayout = false;
        }
        NotifyColumnLayoutChanged();
    }

    private void ResetLayout()
    {
        ApplyLayout(Array.Empty<FilesColumnSettingSnapshot>());
    }

    private static double ClampWidth(FilesColumnSetting setting, double width)
        => Math.Clamp(double.IsFinite(width) ? width : setting.DefaultWidth,
            setting.Key == FilesColumnKey.Name ? 120 : 40, 800);

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
                    // Git 状態は一覧を集めた後に一括で照会する。ここで項目ごとに
                    // check-ignore を起動すると、件数ぶん同期プロセスを起動して UI を固めたうえ、
                    // 下の一括照会と同じ結果を二重に取得してしまう。
                    var entry = new FileEntryViewModel(path, isDirectory, size, info.LastWriteTime, hidden);
                    items.Add(entry);
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

        // 変更状態は既に読み込まれているキャッシュだけをここで使う。ignore 判定を含む
        // git プロセスの起動は StartGitStatusLoad でバックグラウンドに分離する。
        foreach (var entry in items)
            entry.GitStatus = _folderTree?.GitStatusForPath(entry.FullPath, entry.IsDirectory)
                ?? GitChangeKind.None;

        _all = items;
        ApplyView(preserveSelection);
        StartGitStatusLoad(items, CurrentFolder);
    }

    private void StartGitStatusLoad(IReadOnlyList<FileEntryViewModel> entries, string folder)
    {
        if (_folderTree is null || entries.Count == 0)
            return;

        CancelGitStatusLoad();
        var cts = new CancellationTokenSource();
        _gitStatusCts = cts;
        _ = LoadGitStatusesAsync(entries, folder, cts);
    }

    private async Task LoadGitStatusesAsync(
        IReadOnlyList<FileEntryViewModel> entries, string folder, CancellationTokenSource cts)
    {
        try
        {
            var statuses = await Task.Run(() => _folderTree!.GitStatusesForPaths(
                entries.Select(entry => (entry.FullPath, entry.IsDirectory))), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_gitStatusCts, cts) || !PathsEqual(CurrentFolder, folder))
                return;

            foreach (var entry in entries)
                entry.GitStatus = statuses.TryGetValue(entry.FullPath, out var status)
                    ? status : GitChangeKind.None;
            ApplyView(preserveSelection: true);
        }
        catch (OperationCanceledException) { }
        catch { /* Git 不可用・権限エラー時はキャッシュ状態を表示したままにする。 */ }
        finally
        {
            if (ReferenceEquals(_gitStatusCts, cts))
                _gitStatusCts = null;
            cts.Dispose();
        }
    }

    private void CancelGitStatusLoad()
    {
        _gitStatusCts?.Cancel();
        _gitStatusCts = null;
    }

    private void ApplyView(bool preserveSelection)
    {
        var arranged = FilesListing.Arrange(_all, SortColumn, SortDescending, Filter, ShowHiddenFiles, GroupBy);
        if (preserveSelection)
            Reconcile(arranged);
        else
        {
            Entries.Clear();
            foreach (var entry in arranged)
                Entries.Add(entry);
        }
        EntriesView.Refresh();

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

        UpdateThumbnails();
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

    partial void OnGroupByChanged(FilesGroupBy value)
    {
        var normalized = FilesGrouping.Normalize(value);
        if (normalized != value)
        {
            GroupBy = normalized;
            return;
        }

        EntriesView.GroupDescriptions.Clear();
        if (GroupBy != FilesGroupBy.None)
            EntriesView.GroupDescriptions.Add(_groupDescription);
        if (_restoringLayout)
            return;
        ApplyView(preserveSelection: true);
        EntriesView.Refresh();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnShowHiddenFilesChanged(bool value)
    {
        ApplyView(preserveSelection: true);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnDisplayModeChanged(FilesDisplayMode value)
    {
        // バインディング以外から不正値が入っても、XAML側で全レイアウトが非表示にならないようにする。
        var normalized = FilesDisplayModes.Normalize(value);
        if (normalized != value)
        {
            DisplayMode = normalized;
            return;
        }
        UpdateThumbnails();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>現在の表示世代だけにサムネイルを適用する。フォルダー移動や表示形式変更で
    /// 前の一覧の遅い Shell 応答が新しい一覧へ紛れ込まないよう、世代とキャンセルを二重に見る。</summary>
    private void UpdateThumbnails()
    {
        CancelThumbnailLoads();
        var edge = ThumbnailSupport.EdgeFor(DisplayMode);
        if (_thumbnails is null || edge == 0)
        {
            foreach (var entry in Entries)
                entry.ThumbnailImage = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _thumbnailCts = cts;
        var generation = _thumbnailGeneration;
        foreach (var entry in Entries)
        {
            if (!ThumbnailSupport.IsSupported(entry.FullPath))
            {
                entry.ThumbnailImage = null;
                continue;
            }

            _ = LoadThumbnailAsync(entry, edge, generation, cts.Token);
        }
    }

    private async Task LoadThumbnailAsync(FileEntryViewModel entry, int edge, int generation, CancellationToken cancellationToken)
    {
        try
        {
            var image = await _thumbnails!.GetThumbnailAsync(entry.FullPath, edge, cancellationToken);
            if (!cancellationToken.IsCancellationRequested && generation == _thumbnailGeneration
                && Entries.Contains(entry))
                entry.ThumbnailImage = image;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // フォルダー移動・表示モード切替では通常の制御フロー。
        }
    }

    private void CancelThumbnailLoads()
    {
        _thumbnailGeneration++;
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
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

    /// <summary>競合解決付きの貼り付け。キャンセル／スキップ時は表示と選択を変更しない。</summary>
    public FilePasteResult PasteEntry(
        string targetDirectory,
        string sourcePath,
        bool move,
        Func<FileConflictContext, FileConflictDecision> resolver)
    {
        var source = Path.GetFullPath(sourcePath);
        var isDirectory = _commands.DirectoryExists(source);
        var result = _commands.PasteWithConflict(targetDirectory, source, move, resolver);
        if (result.DestinationPath is not { } destination)
            return result;

        if (move)
            EntryRenamed?.Invoke(this, new EntryRenamedEventArgs(source, destination, isDirectory));
        Refresh();
        PendingSelection = destination;
        return result;
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

    // ===== Undo / Redo =====
    // 履歴はツリー（エクスプローラー）と共有する 1 本。どちらのペインで行った操作も、
    // どちらのペインからでも同じ順に戻せる。

    /// <summary>ファイル操作の Undo／Redo 履歴（ツリーと共有）。</summary>
    public FileOperationHistory History => _commands.History;

    /// <summary>複数選択ぶんを 1 回の Undo でまとめて戻すためのくくり。</summary>
    public IDisposable BeginFileOperationBatch() => History.BeginBatch();

    /// <summary>直近のファイル操作を元に戻す（戻せないときは <see cref="InvalidOperationException"/>）。</summary>
    public FileOperationResult UndoFileOperation() => ApplyHistoryResult(History.Undo());

    /// <summary>元に戻したファイル操作をやり直す。</summary>
    public FileOperationResult RedoFileOperation() => ApplyHistoryResult(History.Redo());

    /// <summary>ZIP の再生成を UI スレッドで塞がない非同期版。</summary>
    public async Task<FileOperationResult> RedoFileOperationAsync(CancellationToken cancellationToken = default)
        => ApplyHistoryResult(await History.RedoAsync(cancellationToken));

    private FileOperationResult ApplyHistoryResult(FileOperationResult result)
    {
        foreach (var effect in result.Effects)
        {
            if (effect.MovedFrom is not null && effect.MovedTo is not null)
                EntryRenamed?.Invoke(this, new EntryRenamedEventArgs(effect.MovedFrom, effect.MovedTo, effect.IsDirectory));
            if (effect.Removed is not null)
                EntryDeleted?.Invoke(this, effect.Removed);
        }
        Refresh();
        // 戻した先が今見ているフォルダーの中なら、その行を選ぶ（別フォルダーなら選択は動かさない）。
        if (result.RevealPath is { } reveal
            && string.Equals(Path.GetDirectoryName(reveal), CurrentFolder, StringComparison.OrdinalIgnoreCase))
            PendingSelection = reveal;
        return result;
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

    public bool CanRunFileAi => _folderTree?.IsAiReady == true;

    public void RequestFileAi(FileAiAction action, IEnumerable<FileEntryViewModel> entries)
    {
        if (!CanRunFileAi)
            return;
        var paths = entries.Select(entry => entry.FullPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count > 0)
            FileAiRequested?.Invoke(this, new FileAiRequest(action, paths));
    }

    /// <summary>そのフォルダーを検索へ送れるか（コンテキストメニューの出し分け）。</summary>
    public bool CanSearchIn(string folder) => Directory.Exists(folder) && _workspace.Contains(folder);

    /// <summary>FolderTree と同じ Git コンテキストメニューの出し分け。</summary>
    public bool CanGitFor(FileEntryViewModel? entry)
        => entry is not null && _folderTree?.CanGitForPath(entry.FullPath) == true;

    public bool CanAddToGitignoreFor(FileEntryViewModel? entry)
        => entry is not null && _folderTree?.CanAddToGitignoreForPath(entry.FullPath) == true;

    public void RequestGitBlame(FileEntryViewModel entry)
    {
        if (!entry.IsDirectory && _folderTree is not null)
            _folderTree.RequestGitBlame(entry.FullPath);
    }

    public void RequestGitHistory(FileEntryViewModel entry)
    {
        if (_folderTree is not null)
            _folderTree.RequestGitHistory(entry.FullPath, entry.IsDirectory);
    }

    public void AddToGitignore(FileEntryViewModel entry)
    {
        if (_folderTree is not null)
            _folderTree.AddToGitignore(entry.FullPath, entry.IsDirectory);
    }

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

    public void Dispose()
    {
        CancelThumbnailLoads();
        CancelGitStatusLoad();
        _watcher.Dispose();
        if (_folderTree is not null)
            _folderTree.GitStatusChanged -= OnGitStatusChanged;
    }
}
