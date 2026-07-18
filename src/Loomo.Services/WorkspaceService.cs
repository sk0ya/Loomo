using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Safety;

namespace sk0ya.Loomo.Services;

/// <summary>ファイルシステムと FolderTree 選択状態を管理する IWorkspaceService 実装。</summary>
public sealed class WorkspaceService : IWorkspaceService
{
    private readonly SafetySettings _safety;
    private string? _selectedPath;

    public WorkspaceService(SafetySettings safety) => _safety = safety;

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
        RootPath = Path.GetFullPath(rootPath);
        RootChanged?.Invoke(this, RootPath);
    }

    public Task<IReadOnlyList<FileNode>> ListAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var target = ResolvePath(path);
        if (!Directory.Exists(target))
            return Task.FromResult<IReadOnlyList<FileNode>>(Array.Empty<FileNode>());

        var dirs = Directory.EnumerateDirectories(target)
            .TakeWhile(_ => !ct.IsCancellationRequested)
            .Select(d => new FileNode(Path.GetFileName(d), d, true));
        var files = Directory.EnumerateFiles(target)
            .TakeWhile(_ => !ct.IsCancellationRequested)
            .Select(f => new FileNode(Path.GetFileName(f), f, false));
        var nodes = dirs.Concat(files).ToList();
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<FileNode>>(nodes);
    }

    public async Task<string> ReadFileAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var target = ResolvePath(path);
        if (!File.Exists(target)) return $"(ファイルが存在しません: {target})";
        return await File.ReadAllTextAsync(target, ct);
    }

    public string ResolvePath(string path)
    {
        var root = RootPath ?? Environment.CurrentDirectory;
        var full = string.IsNullOrWhiteSpace(path)
            ? Path.GetFullPath(root)
            : Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path));

        // 設計書 §10: ツールのアクセスはワークスペースルート配下に限定（パストラバーサル防止）
        if (_safety.RestrictToWorkspaceRoot && RootPath is not null && !IsWithin(RootPath, full))
            throw new UnauthorizedAccessException(
                $"ワークスペースルート外へのアクセスは許可されていません: {full}");

        return full;
    }

    /// <summary><paramref name="candidate"/> が <paramref name="root"/> と同一またはその配下か。</summary>
    private static bool IsWithin(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var c = Path.GetFullPath(candidate);
        return c.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
