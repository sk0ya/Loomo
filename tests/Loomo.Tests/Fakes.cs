using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Tests;

/// <summary>ウォームアップの副作用なしフェイク。常に停止状態。</summary>
internal sealed class FakeAiWarmup : IAiWarmup
{
    public bool IsWarmingUp => false;
    public bool IsReady => false;
    public DateTimeOffset? WarmupStartedAt => null;
    public string CurrentStatus => "";
    public string StatusDetails => "";
    public IReadOnlyList<WarmupStageTiming> StageTimings => Array.Empty<WarmupStageTiming>();
    public TimeSpan? TotalDuration => null;
#pragma warning disable CS0067 // テストでは発火させないイベント
    public event Action? StateChanged;
#pragma warning restore CS0067
    public void RequestWarmup() { }
    public Task EnsureWarmAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>VM 構築に必要な最小限のワークスペース実装（副作用なし）。</summary>
internal sealed class FakeWorkspaceService : IWorkspaceService
{
    private readonly List<string> _folders = new();

    public IReadOnlyList<string> Folders => _folders;
    public string? RootPath => _folders.Count > 0 ? _folders[0] : null;
    public string? SelectedPath { get; set; }

    public void OpenFolder(string rootPath)
    {
        _folders.Clear();
        _folders.Add(rootPath);
        RootChanged?.Invoke(this, rootPath);
        FoldersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddFolder(string path)
    {
        if (!_folders.Contains(path))
            _folders.Add(path);
        FoldersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveFolder(string path)
    {
        var index = _folders.IndexOf(path);
        if (index <= 0)
            return;
        _folders.RemoveAt(index);
        FoldersChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<IReadOnlyList<FileNode>> ListAsync(string path, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<FileNode>>(Array.Empty<FileNode>());

    public async Task<string> ReadFileAsync(string path, CancellationToken ct = default)
        => File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : string.Empty;

    public string ResolvePath(string path) => path;

#pragma warning disable CS0067 // テストでは発火させないイベント
    public event EventHandler<string?>? SelectionChanged;
    public event EventHandler<string?>? RootChanged;
    public event EventHandler? FoldersChanged;
#pragma warning restore CS0067
}

/// <summary>VM 構築に必要な最小限のターミナル実装（副作用なし）。</summary>
internal sealed class FakeTerminalService : ITerminalService
{
    public string CurrentDirectory => "C:\\ws";
    public bool IsExecuting => false;
    public Task<CommandResult> RunCommandAsync(string command, CancellationToken ct)
        => Task.FromResult(new CommandResult(command, "", 0, CurrentDirectory, true));
    public void SetWorkingDirectory(string path) { }
    public bool TryRunInVisibleTerminal(string command) => false;
#pragma warning disable CS0067 // テストでは発火させないイベント
    public event EventHandler<CommandResult>? CommandExecuted;
#pragma warning restore CS0067
}

/// <summary>AI へは到達しないテスト用ファクトリ（呼ばれたら失敗させる）。</summary>
internal sealed class FakeAiClientFactory : IAiClientFactory
{
    public IAiClient ResolveCurrent() => throw new NotSupportedException();
}
