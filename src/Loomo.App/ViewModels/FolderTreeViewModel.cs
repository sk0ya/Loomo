using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace sk0ya.Loomo.App.ViewModels;

public sealed partial class FolderTreeViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspace;

    [ObservableProperty]
    private string _rootLabel = "(フォルダ未選択)";

    public ObservableCollection<FileNodeViewModel> Nodes { get; } = new();

    public FolderTreeViewModel(IWorkspaceService workspace)
    {
        _workspace = workspace;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var dlg = new OpenFolderDialog { Title = "ワークスペースフォルダを選択" };
        if (dlg.ShowDialog() == true)
            LoadRoot(dlg.FolderName);
    }

    public void LoadRoot(string path)
    {
        _workspace.OpenFolder(path);
        RootLabel = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(RootLabel)) RootLabel = path;

        Nodes.Clear();
        foreach (var node in EnumerateChildren(path))
            Nodes.Add(node);
    }

    private IEnumerable<FileNodeViewModel> EnumerateChildren(string path)
    {
        if (!Directory.Exists(path)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(path).OrderBy(d => d))
            yield return new FileNodeViewModel(dir, true, this);
        foreach (var file in Directory.EnumerateFiles(path).OrderBy(f => f))
            yield return new FileNodeViewModel(file, false, this);
    }

    public IEnumerable<FileNodeViewModel> Children(string dirPath) => EnumerateChildren(dirPath);

    public void NotifySelected(string fullPath) => _workspace.SelectedPath = fullPath;
}

public sealed partial class FileNodeViewModel : ObservableObject
{
    private readonly FolderTreeViewModel _owner;
    private bool _loaded;

    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public string Glyph => IsDirectory ? "📁" : "📄";

    public ObservableCollection<FileNodeViewModel> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public FileNodeViewModel(string fullPath, bool isDirectory, FolderTreeViewModel owner)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Name = Path.GetFileName(fullPath);
        _owner = owner;
        if (isDirectory) Children.Add(Placeholder); // 遅延読込用ダミー
    }

    private static readonly FileNodeViewModel Placeholder = new();
    private FileNodeViewModel() { FullPath = ""; Name = ""; IsDirectory = false; _owner = null!; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && IsDirectory && !_loaded)
        {
            _loaded = true;
            Children.Clear();
            foreach (var child in _owner.Children(FullPath))
                Children.Add(child);
        }
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _owner.NotifySelected(FullPath);
    }
}
