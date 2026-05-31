using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentStudio.Core.Abstractions;
using AgentStudio.Core.Models;
using AgentStudio.Core.Tools;

namespace AgentStudio.Tests;

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
