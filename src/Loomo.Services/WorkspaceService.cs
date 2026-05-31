using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Services;

/// <summary>ファイルシステムと FolderTree 選択状態を管理する IWorkspaceService 実装。</summary>
public sealed class WorkspaceService : IWorkspaceService
{
    private string? _selectedPath;

    public string? RootPath { get; private set; }

    public string? SelectedPath
    {
        get => _selectedPath;
        set
        {
            if (_selectedPath == value) return;
            _selectedPath = value;
            SelectionChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<string?>? SelectionChanged;
    public event EventHandler<string?>? RootChanged;

    public void OpenFolder(string rootPath)
    {
        RootPath = rootPath;
        RootChanged?.Invoke(this, rootPath);
    }

    public Task<IReadOnlyList<FileNode>> ListAsync(string path)
    {
        var target = ResolvePath(path);
        if (!Directory.Exists(target))
            return Task.FromResult<IReadOnlyList<FileNode>>(Array.Empty<FileNode>());

        var dirs = Directory.EnumerateDirectories(target)
            .Select(d => new FileNode(Path.GetFileName(d), d, true));
        var files = Directory.EnumerateFiles(target)
            .Select(f => new FileNode(Path.GetFileName(f), f, false));
        return Task.FromResult<IReadOnlyList<FileNode>>(dirs.Concat(files).ToList());
    }

    public async Task<string> ReadFileAsync(string path)
    {
        var target = ResolvePath(path);
        if (!File.Exists(target)) return $"(ファイルが存在しません: {target})";
        return await File.ReadAllTextAsync(target);
    }

    private string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return RootPath ?? Environment.CurrentDirectory;
        if (Path.IsPathRooted(path)) return path;
        return Path.GetFullPath(Path.Combine(RootPath ?? Environment.CurrentDirectory, path));
    }
}
