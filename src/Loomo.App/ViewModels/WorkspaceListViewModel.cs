using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ワークスペースが抱えるフォルダー1件（一覧の行を開くと下にぶら下がる）。
/// プライマリ（<see cref="WorkspaceEntryViewModel.RootPath"/>）も含めて全部並べる——行にパスを出すのを
/// やめたぶん、開いたときに「このワークスペースが何を見ているか」が全部読めるようにする。</summary>
public sealed class WorkspaceFolderEntryViewModel(WorkspaceEntryViewModel owner, string path, bool isPrimary)
{
    /// <summary>このフォルダーを持つワークスペース（右クリック操作の対象を引くのに使う）。</summary>
    public WorkspaceEntryViewModel Owner { get; } = owner;

    public string Path { get; } = path;
    public string Name { get; } = WorkspaceListViewModel.DisplayName(path);

    /// <summary>ワークスペースのルート（＝取り除けないフォルダー）。</summary>
    public bool IsPrimary { get; } = isPrimary;

    /// <summary>「ルート」の印を出すか。単一フォルダーのワークスペースでは出さない
    /// ——並んでいるのがそれ1つなら、印は全行に付くだけで何も区別しない。</summary>
    public bool ShowPrimaryTag => IsPrimary && Owner.IsMultiRoot;
}

/// <summary>切替ポップアップ（<c>WorkspaceSwitcherView</c>）に並ぶ1行。フォルダ名・手で付けた表示名・
/// ピン留め・最終利用・フォルダの実在・ワークスペースのフォルダー一覧を持つ。</summary>
public sealed partial class WorkspaceEntryViewModel : ObservableObject
{
    public WorkspaceEntryViewModel(WorkspaceSnapshot snapshot)
    {
        Id = snapshot.Id;
        RootPath = snapshot.RootPath;
        _name = string.IsNullOrWhiteSpace(snapshot.Name)
            ? WorkspaceListViewModel.DisplayName(snapshot.RootPath)
            : snapshot.Name;
        _customName = snapshot.CustomName;
        _isPinned = snapshot.Pinned;
        _lastUsedUtc = snapshot.LastUsedUtc;
        ApplyFolders(snapshot.FolderPaths);
    }

    public Guid Id { get; }
    public string RootPath { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private string _name;

    /// <summary>ユーザーが付けた表示名（null ならフォルダ名を出す）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    [NotifyPropertyChangedFor(nameof(HasCustomName))]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private string? _customName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastUsedLabel))]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private DateTime _lastUsedUtc;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isPinned;

    /// <summary>ルートフォルダが見つからない（消された・外付けドライブが外れた等）。
    /// 切り替えても中身が復元できないので、一覧では警告色で出して削除へ誘導する。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private bool _isMissing;

    /// <summary>このワークスペースのフォルダー（プライマリ＋マルチルートの追加ぶん）。</summary>
    public ObservableCollection<WorkspaceFolderEntryViewModel> Folders { get; } = new();

    /// <summary>この行のフォルダーを出しているか。行のホバーで現れる「🗂」と、一覧全体の
    /// 「🗂 パス」の両方が触る。**その場かぎりの表示状態で、保存はしない**（開きっぱなしを
    /// 復元したいものではないし、索引に持たせるほどの情報でもない）。</summary>
    [ObservableProperty] private bool _isExpanded;

    public bool HasFolders => Folders.Count > 0;

    /// <summary>マルチルート（プライマリ以外のフォルダーがある）。フォルダーを出したときに
    /// 「ルート」の印を添えるかの判定にだけ使う（一覧の行そのものには何も出さない）。</summary>
    public bool IsMultiRoot => Folders.Count > 1;

    /// <summary>一覧・タイトルバーに出す名前。</summary>
    public string Label => string.IsNullOrWhiteSpace(CustomName) ? Name : CustomName!;

    public bool HasCustomName => !string.IsNullOrWhiteSpace(CustomName);

    public string LastUsedLabel => WorkspaceListViewModel.RelativeTime(LastUsedUtc);

    public string ToolTip
    {
        get
        {
            var lines = new List<string> { RootPath };
            if (HasCustomName)
                lines.Add($"フォルダ名: {Name}");
            lines.Add($"最終利用: {LastUsedUtc.ToLocalTime():yyyy/M/d HH:mm}（{LastUsedLabel}）");
            if (IsMultiRoot)
                lines.Add($"フォルダー {Folders.Count}（🗂 パスで表示）");
            if (IsMissing)
                lines.Add("⚠ このフォルダは見つかりません");
            return string.Join("\n", lines);
        }
    }

    /// <summary>フォルダー（プライマリ＋追加ぶん）を反映する。中身が同じなら触らない（開いている
    /// 一覧の行が保存のたびに作り直されて、開閉やホバーが飛ぶのを避ける）。</summary>
    internal void ApplyFolders(IReadOnlyList<string> additionalPaths)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(RootPath))
            paths.Add(RootPath);
        paths.AddRange(additionalPaths);

        if (Folders.Select(f => f.Path).SequenceEqual(paths, StringComparer.OrdinalIgnoreCase))
            return;

        Folders.Clear();
        for (var i = 0; i < paths.Count; i++)
            Folders.Add(new WorkspaceFolderEntryViewModel(this, paths[i], isPrimary: i == 0));

        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(IsMultiRoot));
        OnPropertyChanged(nameof(ToolTip));
    }

    /// <summary>相対時刻の表示は時間の経過だけで変わる（プロパティは変わらない）ので、
    /// ポップアップを開くたびに更新を促す。</summary>
    internal void RefreshLastUsedLabel() => OnPropertyChanged(nameof(LastUsedLabel));
}

public sealed partial class WorkspaceListViewModel : ObservableObject
{
    private readonly WorkspaceStateStore _store;
    private readonly WorkspaceState _state;

    /// <summary>登録されている全ワークスペース（追加順）。表示の並びは <see cref="FilteredWorkspaces"/> が持つ。</summary>
    public ObservableCollection<WorkspaceEntryViewModel> Workspaces { get; } = new();

    /// <summary>一覧に出すぶん。ピン留め→最終利用の新しい順に並べ、<see cref="Filter"/> で絞り込む。
    /// 要素は <see cref="Workspaces"/> と同じインスタンスなので、選択やバインドは壊れない。</summary>
    public ObservableCollection<WorkspaceEntryViewModel> FilteredWorkspaces { get; } = new();

    public event EventHandler<WorkspaceSnapshot>? WorkspaceActivated;

    /// <summary>ワークスペースを一覧から取り除いた（フォルダ自体は消さない）。引数は取り除いた Id。
    /// ShellWindow がこの Id のキャッシュ済みタブ実体（端末プロセス・WebView2）を破棄するために使う。</summary>
    public event EventHandler<Guid>? WorkspaceRemoved;

    /// <summary>アクティブなワークスペースから追加フォルダーを外してほしい（引数はパス）。
    /// 生きている FolderTree を持つ ShellWindow 側で <c>IWorkspaceService.RemoveFolder</c> を呼ぶ。</summary>
    public event EventHandler<string>? FolderRemoveRequested;

    /// <summary>一覧の絞り込み（名前・パスの部分一致、空白区切りは AND）。</summary>
    [ObservableProperty] private string _filter = "";

    /// <summary>一覧で選択中（＝キーボードのカーソル位置）の行。選択しただけでは切り替わらない
    /// ——切替は <see cref="ActivateWorkspaceCommand"/>（クリック／Enter）だけで起きる。
    /// 矢印キーで一覧をたどるたびにワークスペースが切り替わってしまうのを避けるため。</summary>
    [ObservableProperty] private WorkspaceEntryViewModel? _selectedWorkspace;

    /// <summary>現在アクティブな行（タイトルバーのボタン表示に使う）。</summary>
    [ObservableProperty] private WorkspaceEntryViewModel? _activeEntry;

    public WorkspaceListViewModel(WorkspaceStateStore store)
    {
        _store = store;
        _state = store.LoadForStartup();

        foreach (var snapshot in _state.Workspaces
                     .Where(w => !string.IsNullOrWhiteSpace(w.RootPath))
                     .OrderByDescending(w => w.LastUsedUtc))
        {
            Workspaces.Add(new WorkspaceEntryViewModel(snapshot)
            {
                IsActive = snapshot.Id == _state.ActiveWorkspaceId
            });
        }

        RefreshEntries();
    }

    public WorkspaceSnapshot? ActiveWorkspace =>
        _state.ActiveWorkspaceId is { } id ? FindSnapshot(id) : null;

    partial void OnFilterChanged(string value) => RebuildFiltered();

    [RelayCommand]
    private void OpenFolder()
    {
        var dlg = new OpenFolderDialog { Title = "ワークスペースフォルダを選択" };
        if (dlg.ShowDialog() == true)
            ActivateFolder(dlg.FolderName);
    }

    [RelayCommand]
    private void ActivateWorkspace(WorkspaceEntryViewModel? entry)
    {
        if (entry is null)
            return;

        var snapshot = FindSnapshot(entry.Id);
        if (snapshot is not null)
            Activate(snapshot);
    }

    /// <summary>ピン留めの切替。ピン留めは一覧の並び（上部固定）だけに効き、切替の挙動は変わらない。</summary>
    [RelayCommand]
    private void TogglePin(WorkspaceEntryViewModel? entry)
    {
        if (entry is null || FindSnapshot(entry.Id) is not { } snapshot)
            return;

        snapshot.Pinned = !snapshot.Pinned;
        entry.IsPinned = snapshot.Pinned;
        _store.Save(_state);
        RefreshEntries();
    }

    [RelayCommand]
    private void ClearFilter() => Filter = "";

    /// <summary>一覧をパス一覧として読むモード（絞り込み欄の隣のボタンの状態）。切り替えると
    /// 全行のフォルダー表示をまとめて合わせる。行ごとの「🗂」で個別に開き直せる。</summary>
    [ObservableProperty] private bool _showFolders;

    /// <summary>フォルダー表示の一括切替（絞り込み欄の隣のボタン）。</summary>
    [RelayCommand]
    private void ToggleFolders()
    {
        ShowFolders = !ShowFolders;
        foreach (var entry in Workspaces)
            entry.IsExpanded = ShowFolders;
    }

    /// <summary>1行ぶんのフォルダー表示切替（行のホバーで現れる「🗂」）。保存しない一時的な状態。</summary>
    [RelayCommand]
    private void ToggleRowFolders(WorkspaceEntryViewModel? entry)
    {
        if (entry is not null)
            entry.IsExpanded = !entry.IsExpanded;
    }

    /// <summary>追加フォルダーをワークスペースから取り除く（フォルダ自体は消さない）。
    /// アクティブなワークスペースは<em>生きている</em> FolderTree／WorkspaceService を通す必要があるので
    /// <see cref="FolderRemoveRequested"/> で購読側（ShellWindow）に任せ、スナップショットへの反映は
    /// 通常の保存経路（<c>CaptureInto</c>）に乗せる。非アクティブなものはここでスナップショットを直接直す。</summary>
    public void RemoveFolder(WorkspaceFolderEntryViewModel? folder)
    {
        // プライマリ（ルート）は外せない——それはワークスペースそのものを消すことになる。
        if (folder is null or { IsPrimary: true } || FindSnapshot(folder.Owner.Id) is not { } snapshot)
            return;

        if (snapshot.Id == _state.ActiveWorkspaceId)
        {
            FolderRemoveRequested?.Invoke(this, folder.Path);
            return;
        }

        snapshot.AdditionalFolders.RemoveAll(f =>
            string.Equals(f.FolderPath, folder.Path, StringComparison.OrdinalIgnoreCase));
        snapshot.CachedAdditionalFolders?.RemoveAll(p =>
            string.Equals(p, folder.Path, StringComparison.OrdinalIgnoreCase));
        _store.Save(_state);
        RefreshEntries();
    }

    /// <summary>表示名を付け替える（空／フォルダ名と同じなら既定＝フォルダ名に戻す）。
    /// フォルダ名そのものは変えない。</summary>
    public void Rename(WorkspaceEntryViewModel? entry, string? name)
    {
        if (entry is null || FindSnapshot(entry.Id) is not { } snapshot)
            return;

        var trimmed = name?.Trim();
        snapshot.CustomName =
            string.IsNullOrEmpty(trimmed) || trimmed == DisplayName(snapshot.RootPath) ? null : trimmed;
        _store.Save(_state);
        RefreshEntries();
    }

    /// <summary>ポップアップを開く直前の更新。フォルダの実在確認（消えたワークスペースの警告表示）と
    /// 相対時刻の振り直しは、ここでだけ行う（保存のたびにディスクを叩かないため）。</summary>
    public void Refresh()
    {
        var probed = false;
        foreach (var entry in Workspaces)
        {
            entry.IsMissing = !string.IsNullOrWhiteSpace(entry.RootPath) && !Directory.Exists(entry.RootPath);
            entry.RefreshLastUsedLabel();

            // 追加フォルダーが索引に無いぶん（この機能より前に書かれた workspaces.json）だけ、
            // 詳細から一度だけ拾って索引へ載せる。以後は索引にあるので読まない——開くたびに
            // 全ワークスペースの state.json を読みに行くのは重い。
            if (FindSnapshot(entry.Id) is { IsDetailsLoaded: false, CachedAdditionalFolders: null } stale)
            {
                stale.CachedAdditionalFolders = _store.LoadWorkspace(stale.Id)?.FolderPaths.ToList() ?? [];
                probed = true;
            }
        }
        if (probed)
            _store.Save(_state);
        RefreshEntries();
    }

    /// <summary>ワークスペースを一覧から取り除く（フォルダ自体は削除しない）。アクティブなものを取り除くときは、
    /// 先に最近使った別のワークスペースへ切り替えてから取り除く（切替で現在の内容が退避され、タブ実体が安全に外れる）。</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveWorkspace))]
    private void RemoveWorkspace(WorkspaceEntryViewModel? entry)
    {
        if (entry is null)
            return;

        var snapshot = FindSnapshot(entry.Id);
        if (snapshot is null)
            return;

        if (_state.ActiveWorkspaceId == entry.Id)
        {
            var next = _state.Workspaces
                .Where(w => w.Id != entry.Id && !string.IsNullOrWhiteSpace(w.RootPath))
                .OrderByDescending(w => w.LastUsedUtc)
                .FirstOrDefault();

            // 最後の1つは取り除かない（常にアクティブなワークスペースが要る）。
            if (next is null)
                return;

            Activate(next);
        }

        _state.Workspaces.RemoveAll(w => w.Id == entry.Id);
        Workspaces.Remove(entry);
        _store.DeleteWorkspace(entry.Id);
        _store.Save(_state);
        RemoveWorkspaceCommand.NotifyCanExecuteChanged();
        RefreshEntries();

        WorkspaceRemoved?.Invoke(this, entry.Id);
    }

    private bool CanRemoveWorkspace(WorkspaceEntryViewModel? entry)
        => entry is not null && Workspaces.Count > 1;

    public void ActivateFolder(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var snapshot = _state.Workspaces.FirstOrDefault(w =>
            string.Equals(Path.GetFullPath(w.RootPath), fullPath, StringComparison.OrdinalIgnoreCase));

        if (snapshot is null)
        {
            snapshot = new WorkspaceSnapshot
            {
                RootPath = fullPath,
                Name = DisplayName(fullPath),
                LastUsedUtc = DateTime.UtcNow,
                Terminal = new TerminalSnapshot { WorkingDirectory = fullPath },
                Mode = DisplayMode.Solo,
                Stage = new StageSnapshot {
                    IsActive = true,
                    Pane = PaneKind.Editor
                }
            };
            _state.Workspaces.Add(snapshot);
            Workspaces.Insert(0, new WorkspaceEntryViewModel(snapshot));
            RemoveWorkspaceCommand.NotifyCanExecuteChanged();
        }

        Activate(snapshot);
    }

    public void SaveSnapshot(WorkspaceSnapshot snapshot)
    {
        var index = _state.Workspaces.FindIndex(w => w.Id == snapshot.Id);
        if (index >= 0)
            _state.Workspaces[index] = snapshot;
        else
            _state.Workspaces.Add(snapshot);

        _store.Save(_state);
        RefreshEntries();
    }

    public void Persist()
    {
        _store.Save(_state);
        RefreshEntries();
    }

    private void Activate(WorkspaceSnapshot snapshot)
    {
        if (_state.ActiveWorkspaceId == snapshot.Id)
        {
            snapshot.LastUsedUtc = DateTime.UtcNow;
            _store.Save(_state);
            RefreshEntries();
            return;
        }

        var loaded = _store.LoadWorkspace(snapshot.Id);
        if (loaded is not null && !ReferenceEquals(loaded, snapshot))
        {
            // ピン留め・表示名は索引（workspaces.json）側が正。未読込のあいだに変更されていると
            // state.json は古いままなので、読み込んだ実体へ引き継いでから差し替える
            // （さもないと次の保存で索引の値が古い値に戻る）。
            loaded.Pinned = snapshot.Pinned;
            loaded.CustomName = snapshot.CustomName;
            var index = _state.Workspaces.FindIndex(w => w.Id == snapshot.Id);
            if (index >= 0) _state.Workspaces[index] = loaded;
            snapshot = loaded;
        }

        snapshot.LastUsedUtc = DateTime.UtcNow;
        _state.ActiveWorkspaceId = snapshot.Id;
        // ここでは保存しない。購読側（ShellWindow.SwitchWorkspaceAsync）が冒頭で
        // captureCurrent の即時スナップショット保存を行い、その _store.Save が
        // 新しい ActiveWorkspaceId を含む _state 全体を永続化する。二重書込を避ける。
        RefreshEntries();
        WorkspaceActivated?.Invoke(this, snapshot);
    }

    private WorkspaceSnapshot? FindSnapshot(Guid id)
        => _state.Workspaces.FirstOrDefault(w => w.Id == id);

    private void RefreshEntries()
    {
        WorkspaceEntryViewModel? active = null;

        foreach (var entry in Workspaces)
        {
            var snapshot = FindSnapshot(entry.Id);
            if (snapshot is null)
                continue;

            entry.Name = string.IsNullOrWhiteSpace(snapshot.Name)
                ? DisplayName(snapshot.RootPath)
                : snapshot.Name;
            entry.CustomName = snapshot.CustomName;
            entry.IsPinned = snapshot.Pinned;
            entry.LastUsedUtc = snapshot.LastUsedUtc;
            entry.ApplyFolders(snapshot.FolderPaths);
            entry.IsActive = entry.Id == _state.ActiveWorkspaceId;
            if (entry.IsActive)
                active = entry;
        }

        ActiveEntry = active;
        if (SelectedWorkspace is null || !Workspaces.Contains(SelectedWorkspace))
            SelectedWorkspace = active;

        RebuildFiltered();
    }

    /// <summary>絞り込み・並べ替えの結果を <see cref="FilteredWorkspaces"/> へ反映する。
    /// 中身が同じなら触らない（開いたままのポップアップで選択やスクロールが飛ばないように）。</summary>
    private void RebuildFiltered()
    {
        var terms = Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var next = Workspaces
            .Where(w => terms.All(t =>
                w.Label.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                w.RootPath.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(w => w.IsPinned)
            .ThenByDescending(w => w.LastUsedUtc)
            .ToList();

        if (FilteredWorkspaces.SequenceEqual(next))
            return;

        FilteredWorkspaces.Clear();
        foreach (var entry in next)
            FilteredWorkspaces.Add(entry);

        // 絞り込んで選択が候補から外れたら、先頭へ寄せる（Enter がそのまま効くように）。
        if (SelectedWorkspace is null || !next.Contains(SelectedWorkspace))
            SelectedWorkspace = next.FirstOrDefault();
    }

    internal static string DisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    /// <summary>最終利用の相対表記（「たった今」「3時間前」…、1か月以上前は日付）。</summary>
    internal static string RelativeTime(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "たった今";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}分前";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}時間前";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}日前";
        if (span.TotalDays < 30) return $"{(int)(span.TotalDays / 7)}週間前";
        return utc.ToLocalTime().ToString("yyyy/M/d");
    }
}
