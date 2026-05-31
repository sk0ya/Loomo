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

    public ObservableCollection<WorkspaceEntryViewModel> Workspaces { get; } = new();

    public event EventHandler<WorkspaceSnapshot>? WorkspaceActivated;

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
        _store.Save(_state);
        RefreshEntries();
        WorkspaceActivated?.Invoke(this, snapshot);
    }

    private WorkspaceSnapshot? FindSnapshot(Guid id)
        => _state.Workspaces.FirstOrDefault(w => w.Id == id);

    private void RefreshEntries()
    {
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
        }
    }

    internal static string DisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}
