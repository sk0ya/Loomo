using System;
using System.Collections.Generic;
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
    public string? RootPath { get; private set; }
    public string? SelectedPath { get; set; }

    public void OpenFolder(string rootPath)
    {
        RootPath = rootPath;
        RootChanged?.Invoke(this, rootPath);
    }

    public Task<IReadOnlyList<FileNode>> ListAsync(string path)
        => Task.FromResult<IReadOnlyList<FileNode>>(Array.Empty<FileNode>());

    public Task<string> ReadFileAsync(string path) => Task.FromResult(string.Empty);

    public string ResolvePath(string path) => path;

#pragma warning disable CS0067 // テストでは発火させないイベント
    public event EventHandler<string?>? SelectionChanged;
    public event EventHandler<string?>? RootChanged;
#pragma warning restore CS0067
}

/// <summary>AI へは到達しないテスト用ファクトリ（呼ばれたら失敗させる）。</summary>
internal sealed class FakeAiClientFactory : IAiClientFactory
{
    public IAiClient Resolve(AiProvider provider) => throw new NotSupportedException();
    public IAiClient ResolveCurrent() => throw new NotSupportedException();
}
