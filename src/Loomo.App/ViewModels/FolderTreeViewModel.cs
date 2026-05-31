using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using sk0ya.Loomo.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

public sealed partial class FolderTreeViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspace;
    private GitTreeState _gitState = GitTreeState.Empty;
    private string? _currentRoot;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _refreshTimer;

    [ObservableProperty]
    private string _rootLabel = "(フォルダ未選択)";

    [ObservableProperty]
    private bool _hideIgnoredFiles = true;

    [ObservableProperty]
    private bool _showChangedOnly;

    [ObservableProperty]
    private string _filterStatus = "";

    [ObservableProperty]
    private bool _hasVisibleNodes;

    [ObservableProperty]
    private string _emptyMessage = "";

    public ObservableCollection<FileNodeViewModel> Nodes { get; } = new();

    public FolderTreeViewModel(IWorkspaceService workspace)
    {
        _workspace = workspace;
    }

    public void LoadRoot(string path)
    {
        _workspace.OpenFolder(path);
        _currentRoot = Path.GetFullPath(path);
        RootLabel = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(RootLabel)) RootLabel = path;

        RefreshGitState();
        ReloadNodes();
        StartWatching(path);
    }

    [RelayCommand]
    private void Refresh()
    {
        if (_currentRoot is null) return;
        RefreshWorkspace();
    }

    partial void OnHideIgnoredFilesChanged(bool value) => RefreshWorkspace();

    partial void OnShowChangedOnlyChanged(bool value) => RefreshWorkspace();

    private void RefreshWorkspace()
    {
        if (_currentRoot is null) return;
        RefreshGitState();
        ReloadNodes();
    }

    private void ReloadNodes()
    {
        Nodes.Clear();
        if (_currentRoot is null)
        {
            HasVisibleNodes = false;
            EmptyMessage = "フォルダを開いてください";
            return;
        }

        foreach (var node in EnumerateChildren(_currentRoot))
            Nodes.Add(node);

        HasVisibleNodes = Nodes.Count > 0;
        EmptyMessage = CreateEmptyMessage();
    }

    private IEnumerable<FileNodeViewModel> EnumerateChildren(string path)
    {
        if (!Directory.Exists(path)) yield break;

        var directories = Directory.EnumerateDirectories(path).ToArray();
        var files = Directory.EnumerateFiles(path).ToArray();
        var ignoredPaths = HideIgnoredFiles
            ? _gitState.GetIgnoredPaths(directories.Concat(files))
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var visibleDirectories = directories
            .OrderBy(d => Path.GetFileName(d))
            .Where(d => ShouldShow(d, isDirectory: true, ignoredPaths))
            .Select(d => new FileNodeViewModel(d, true, this));

        var visibleFiles = files
            .OrderBy(f => Path.GetFileName(f))
            .Where(f => ShouldShow(f, isDirectory: false, ignoredPaths))
            .Select(f => new FileNodeViewModel(f, false, this));

        foreach (var node in visibleDirectories.Concat(visibleFiles))
            yield return node;
    }

    public IEnumerable<FileNodeViewModel> Children(string dirPath) => EnumerateChildren(dirPath);

    public void NotifySelected(string fullPath) => _workspace.SelectedPath = fullPath;

    private bool ShouldShow(string path, bool isDirectory, HashSet<string> ignoredPaths)
    {
        var fullPath = Path.GetFullPath(path);

        if (HideIgnoredFiles && ignoredPaths.Contains(fullPath))
            return false;

        if (!ShowChangedOnly)
            return true;

        return isDirectory
            ? _gitState.ChangedDirectories.Contains(fullPath)
            : _gitState.ChangedFiles.Contains(fullPath);
    }

    private string CreateEmptyMessage()
    {
        if (_currentRoot is null)
            return "フォルダを開いてください";

        if (ShowChangedOnly && !_gitState.IsGitRepository)
            return "Git リポジトリではありません";

        if (ShowChangedOnly)
            return "変更されたファイルはありません";

        return "表示する項目はありません";
    }

    private void RefreshGitState()
    {
        if (_currentRoot is null)
        {
            _gitState = GitTreeState.Empty;
            FilterStatus = "";
            return;
        }

        _gitState = GitTreeState.Load(_currentRoot);

        var filters = new List<string>();
        if (HideIgnoredFiles) filters.Add("ignore 非表示");
        if (ShowChangedOnly) filters.Add("変更のみ");

        var gitStatus = _gitState.IsGitRepository
            ? $"{_gitState.ChangedFiles.Count} 件変更"
            : "Git 未検出";
        FilterStatus = filters.Count == 0
            ? gitStatus
            : $"{string.Join(" / ", filters)} - {gitStatus}";
    }

    private void StartWatching(string path)
    {
        _watcher?.Dispose();
        _refreshTimer?.Dispose();

        if (!Directory.Exists(path))
            return;

        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.Attributes
        };

        _watcher.Changed += OnWorkspaceChanged;
        _watcher.Created += OnWorkspaceChanged;
        _watcher.Deleted += OnWorkspaceChanged;
        _watcher.Renamed += OnWorkspaceChanged;
        _watcher.Error += (_, _) => ScheduleRefresh();
        _watcher.EnableRaisingEvents = true;
    }

    private void OnWorkspaceChanged(object sender, FileSystemEventArgs e) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        _refreshTimer ??= new System.Threading.Timer(_ =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
                RefreshWorkspace();
            else
                dispatcher.Invoke(RefreshWorkspace);
        });

        _refreshTimer.Change(300, System.Threading.Timeout.Infinite);
    }
}

internal sealed class GitTreeState
{
    private readonly string _rootPath;
    private readonly string? _gitRootPath;

    public static GitTreeState Empty { get; } = new("", null, new(), new());

    public bool IsGitRepository => _gitRootPath is not null;
    public HashSet<string> ChangedFiles { get; }
    public HashSet<string> ChangedDirectories { get; }

    private GitTreeState(
        string rootPath,
        string? gitRootPath,
        HashSet<string> changedFiles,
        HashSet<string> changedDirectories)
    {
        _rootPath = rootPath;
        _gitRootPath = gitRootPath;
        ChangedFiles = changedFiles;
        ChangedDirectories = changedDirectories;
    }

    public static GitTreeState Load(string rootPath)
    {
        var fullRoot = Path.GetFullPath(rootPath);
        var gitRoot = FindGitRoot(fullRoot);
        if (gitRoot is null)
            return new GitTreeState(fullRoot, null, new(), new());

        var changedFiles = LoadChangedFiles(gitRoot);
        var changedDirectories = LoadChangedDirectories(changedFiles, fullRoot);
        return new GitTreeState(fullRoot, gitRoot, changedFiles, changedDirectories);
    }

    public HashSet<string> GetIgnoredPaths(IEnumerable<string> fullPaths)
    {
        var ignoredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedPaths = fullPaths.Select(Path.GetFullPath).ToArray();

        foreach (var path in normalizedPaths.Where(IsInsideGitDirectory))
            ignoredPaths.Add(path);

        if (_gitRootPath is null)
            return ignoredPaths;

        var candidates = normalizedPaths
            .Where(path => !ignoredPaths.Contains(path))
            .Select(path => new
            {
                FullPath = path,
                RelativePath = ToGitRelativePath(_gitRootPath, path)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.RelativePath))
            .ToArray();

        if (candidates.Length == 0)
            return ignoredPaths;

        var input = string.Join('\0', candidates.Select(x => x.RelativePath)) + '\0';
        var result = RunGit(_gitRootPath, input, new[] { "check-ignore", "-z", "--stdin" });
        if (result.ExitCode is not 0 and not 1)
            return ignoredPaths;

        foreach (var relativePath in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            ignoredPaths.Add(Path.GetFullPath(Path.Combine(_gitRootPath, relativePath)));

        return ignoredPaths;
    }

    private bool IsInsideGitDirectory(string fullPath)
    {
        var gitDir = Path.Combine(_gitRootPath ?? _rootPath, ".git");
        var normalizedGitDir = Path.GetFullPath(gitDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fullPath.Equals(normalizedGitDir, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(normalizedGitDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindGitRoot(string rootPath)
    {
        var result = RunGit(rootPath, "rev-parse", "--show-toplevel");
        if (result.ExitCode != 0)
            return FindGitRootByDirectory(rootPath);

        var path = result.Output.Trim();
        return string.IsNullOrWhiteSpace(path)
            ? FindGitRootByDirectory(rootPath)
            : Path.GetFullPath(path);
    }

    private static string? FindGitRootByDirectory(string rootPath)
    {
        var directory = new DirectoryInfo(rootPath);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    private static HashSet<string> LoadChangedFiles(string gitRoot)
    {
        var result = RunGit(gitRoot, "status", "--porcelain", "-z", "--untracked-files=all");
        if (result.ExitCode != 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4)
                continue;

            var status = entry[..2];
            var relativePath = entry[3..];

            if ((status[0] is 'R' or 'C' || status[1] is 'R' or 'C') && i + 1 < entries.Length)
                relativePath = entries[++i];

            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            files.Add(Path.GetFullPath(Path.Combine(gitRoot, relativePath)));
        }

        return files;
    }

    private static HashSet<string> LoadChangedDirectories(HashSet<string> changedFiles, string visibleRoot)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedRoot = Path.GetFullPath(visibleRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var file in changedFiles)
        {
            var directory = Path.GetDirectoryName(file);
            while (!string.IsNullOrEmpty(directory))
            {
                var normalizedDirectory = Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (normalizedDirectory.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    break;

                if (!normalizedDirectory.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    break;

                directories.Add(normalizedDirectory);
                directory = Path.GetDirectoryName(normalizedDirectory);
            }
        }

        return directories;
    }

    private static string ToGitRelativePath(string gitRoot, string fullPath)
    {
        var relativePath = Path.GetRelativePath(gitRoot, fullPath);
        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static GitCommandResult RunGit(string workingDirectory, params string[] args)
        => RunGit(workingDirectory, standardInput: null, args);

    private static GitCommandResult RunGit(string workingDirectory, string? standardInput, params string[] args)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (var arg in args)
                process.StartInfo.ArgumentList.Add(arg);

            process.Start();

            if (standardInput is not null)
            {
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return new GitCommandResult(-1, "");
            }

            return new GitCommandResult(process.ExitCode, outputTask.GetAwaiter().GetResult());
        }
        catch
        {
            return new GitCommandResult(-1, "");
        }
    }

    private readonly record struct GitCommandResult(int ExitCode, string Output);
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
