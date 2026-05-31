using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Core.Abstractions;

/// <summary>ターミナル（sk0ya.Terminal）への操作を抽象化。</summary>
public interface ITerminalService
{
    /// <summary>コマンドを実行し結果を待つ。</summary>
    Task<CommandResult> RunCommandAsync(string command, CancellationToken ct);

    /// <summary>作業ディレクトリを設定。</summary>
    void SetWorkingDirectory(string path);

    string CurrentDirectory { get; }
    bool IsExecuting { get; }

    /// <summary>実行された全コマンド結果（人間・AI問わず）の通知。</summary>
    event EventHandler<CommandResult>? CommandExecuted;
}

/// <summary>エディタ（sk0ya.Editor）への操作を抽象化。</summary>
public interface IEditorService
{
    Task OpenFileAsync(string path);
    Task<string> GetActiveContentAsync();
    Task<string> GetSelectedTextAsync();

    /// <summary>差分を提示する（適用は ApplyEditAsync）。戻り値は差分の概要。</summary>
    Task<string> ShowDiffAsync(string path, string proposedContent);

    /// <summary>編集を適用して保存。</summary>
    Task<bool> ApplyEditAsync(string path, string newContent);

    string? ActiveFilePath { get; }
}

/// <summary>ワークスペース（フォルダ・選択状態・ファイルシステム）を抽象化。</summary>
public interface IWorkspaceService
{
    string? RootPath { get; }
    string? SelectedPath { get; set; }

    void OpenFolder(string rootPath);
    Task<IReadOnlyList<FileNode>> ListAsync(string path);
    Task<string> ReadFileAsync(string path);

    event EventHandler<string?>? SelectionChanged;
    event EventHandler<string?>? RootChanged;
}

/// <summary>危険操作（コマンド実行・書込）のユーザー承認。</summary>
public interface IApprovalService
{
    Task<bool> RequestApprovalAsync(string toolName, string summary, CancellationToken ct);
}
