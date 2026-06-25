using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

public sealed partial class WorkspaceEntryViewModel : ObservableObject
{
    public WorkspaceEntryViewModel(WorkspaceSnapshot snapshot)
    {
        Id = snapshot.Id;
        RootPath = snapshot.RootPath;
        _name = string.IsNullOrWhiteSpace(snapshot.Name)
            ? WorkspaceListViewModel.DisplayName(snapshot.RootPath)
            : snapshot.Name;
        _lastUsedUtc = snapshot.LastUsedUtc;
    }

    public Guid Id { get; }
    public string RootPath { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private DateTime _lastUsedUtc;
    [ObservableProperty] private bool _isActive;
}

public sealed partial class WorkspaceListViewModel : ObservableObject
{
    private readonly WorkspaceStateStore _store;
    private readonly WorkspaceState _state;
    private bool _isRefreshingSelection;

    public ObservableCollection<WorkspaceEntryViewModel> Workspaces { get; } = new();

    public event EventHandler<WorkspaceSnapshot>? WorkspaceActivated;

    /// <summary>ワークスペースを一覧から取り除いた（フォルダ自体は消さない）。引数は取り除いた Id。
    /// ShellWindow がこの Id のキャッシュ済みタブ実体（端末プロセス・WebView2）を破棄するために使う。</summary>
    public event EventHandler<Guid>? WorkspaceRemoved;

    [ObservableProperty] private WorkspaceEntryViewModel? _selectedWorkspace;

    public WorkspaceListViewModel(WorkspaceStateStore store)
    {
        _store = store;
        _state = store.Load();

        foreach (var snapshot in _state.Workspaces
                     .Where(w => !string.IsNullOrWhiteSpace(w.RootPath))
                     .OrderByDescending(w => w.LastUsedUtc))
        {
            Workspaces.Add(new WorkspaceEntryViewModel(snapshot)
            {
                IsActive = snapshot.Id == _state.ActiveWorkspaceId
            });
        }

        _isRefreshingSelection = true;
        try
        {
            SelectedWorkspace = Workspaces.FirstOrDefault(w => w.IsActive);
        }
        finally
        {
            _isRefreshingSelection = false;
        }
    }

    public WorkspaceSnapshot? ActiveWorkspace =>
        _state.ActiveWorkspaceId is { } id ? FindSnapshot(id) : null;

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

    partial void OnSelectedWorkspaceChanged(WorkspaceEntryViewModel? value)
    {
        if (_isRefreshingSelection || value is null)
            return;

        ActivateWorkspace(value);
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
        _store.Save(_state);
        RemoveWorkspaceCommand.NotifyCanExecuteChanged();

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
                Terminal = new TerminalSnapshot { WorkingDirectory = fullPath }
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
        _isRefreshingSelection = true;
        try
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
                entry.LastUsedUtc = snapshot.LastUsedUtc;
                entry.IsActive = entry.Id == _state.ActiveWorkspaceId;
                if (entry.IsActive)
                    active = entry;
            }

            SelectedWorkspace = active;
        }
        finally
        {
            _isRefreshingSelection = false;
        }
    }

    internal static string DisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}
