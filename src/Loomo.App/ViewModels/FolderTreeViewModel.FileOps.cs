using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualBasic.FileIO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

public sealed partial class FolderTreeViewModel
{
    // ===== ファイル操作（新規作成・名前変更・削除）とコンテキストメニュー要求 =====
    // View（コンテキストメニュー／F2・Delete）から呼ばれる。検証に失敗した場合や
    // I/O が失敗した場合は InvalidOperationException を投げ、呼び出し側がメッセージを表示する。
    // パスは ResolvePath を通してワークスペースルート配下に限定する（ツールと同じ防御）。

    /// <summary>新規項目の作成先となる親ディレクトリ。ディレクトリ選択時はその中、
    /// ファイル選択時はその親、未選択時はルート。フォルダ未選択なら null。</summary>
    public string? GetTargetDirectory(FileNodeViewModel? selected)
    {
        if (_currentRoot is null)
            return null;
        if (selected is null)
            return _currentRoot;
        return selected.IsDirectory ? selected.FullPath : Path.GetDirectoryName(selected.FullPath);
    }

    /// <summary>指定ディレクトリ直下に空ファイル／フォルダを作成し、作成したフルパスを返す。</summary>
    public string CreateEntry(string parentDirectory, string name, bool isDirectory)
    {
        var fullPath = _fileCommands.Create(parentDirectory, name, isDirectory);
        RefreshWorkspace();
        return fullPath;
    }

    /// <summary>ノードを新しい名前へ変更し、変更後のフルパスを返す。</summary>
    public string RenameEntry(FileNodeViewModel node, string newName)
    {
        var oldPath = _workspace.ResolvePath(node.FullPath);
        var newPath = _fileCommands.Rename(oldPath, newName, node.IsDirectory);
        if (string.Equals(oldPath, newPath, StringComparison.Ordinal)) return oldPath;
        RefreshWorkspace();
        // 開いているエディタタブを新パスへ追従させる（フォルダなら配下のファイルも対象）。
        EntryRenamed?.Invoke(this, new EntryRenamedEventArgs(oldPath, newPath, node.IsDirectory));
        return newPath;
    }

    /// <summary>ノードをゴミ箱へ送る（完全削除ではない）。</summary>
    public void DeleteEntry(FileNodeViewModel node)
    {
        var path = _workspace.ResolvePath(node.FullPath);
        _fileCommands.Delete(path, node.IsDirectory);
        RefreshWorkspace();
        // 削除したファイル（フォルダなら配下）を開いているエディタタブを閉じる。
        EntryDeleted?.Invoke(this, path);
    }

    /// <summary>クリップボードのファイル／フォルダを targetDirectory 直下へコピー（move=false）または
    /// 移動（move=true）し、貼り付け先のフルパスを返す。貼り付け先は ResolvePath でワークスペースルート
    /// 配下に限定する（コピー元は外部＝Explorer からでも受け付ける）。同名衝突は上書きせず
    /// 「 - コピー」を付けて一意化し、フォルダを自身／配下へ貼るのは拒否する。</summary>
    public string PasteEntry(string targetDirectory, string sourcePath, bool move)
    {
        var source = Path.GetFullPath(sourcePath);
        var isDirectory = _fileCommands.DirectoryExists(source);
        var destination = _fileCommands.Paste(targetDirectory, source, move);
        RefreshWorkspace();
        // 移動はリネームと同じく、開いているエディタタブを新パスへ追従させる。
        if (move)
            EntryRenamed?.Invoke(this, new EntryRenamedEventArgs(source, destination, isDirectory));
        return destination;
    }

    public void NotifySelected(string fullPath) => _workspace.SelectedPath = fullPath;

    public void NotifyActivated(string fullPath)
    {
        _workspace.SelectedPath = fullPath;
        if (_fileCommands.FileExists(fullPath))
            FileActivated?.Invoke(this, fullPath);
    }

    public void NotifyPreviewRequested(string fullPath)
    {
        _workspace.SelectedPath = fullPath;
        if (_fileCommands.FileExists(fullPath))
            FilePreviewRequested?.Invoke(this, fullPath);
    }

    /// <summary>HTML ファイルをアプリ内ブラウザで開くよう要求する（ShellWindow が処理）。</summary>
    public void RequestOpenInBrowser(string fullPath)
    {
        if (_fileCommands.FileExists(fullPath))
            OpenInBrowserRequested?.Invoke(this, fullPath);
    }

    /// <summary>項目を可視ターミナルへセットするよう要求する（ShellWindow が処理）。
    /// フォルダはそのフォルダへ cd、ファイルはパスをプロンプトへ入力する。</summary>
    public void RequestSetInTerminal(FileNodeViewModel node)
    {
        if (_fileCommands.EntryExists(node.FullPath, node.IsDirectory))
            SetInTerminalRequested?.Invoke(this, new TerminalSetRequest(node.FullPath, node.IsDirectory));
    }

    /// <summary>このフォルダーを検索の開始フォルダーにして検索パネルを開くよう要求する（ShellWindow が処理）。
    /// フォルダかつ実在のときだけ発火する。</summary>
    public void RequestSearchInFolder(FileNodeViewModel node)
    {
        if (node.IsDirectory && _fileCommands.DirectoryExists(node.FullPath))
            SearchInFolderRequested?.Invoke(this, node.FullPath);
    }

    /// <summary>指定ファイルの誤字脱字チェックを要求する（ShellWindow が AIバーで処理）。
    /// AI が使える状態（暖機完了）かつ実在ファイルのときだけ発火する。</summary>
    public void RequestTypoCheck(FileNodeViewModel node)
    {
        if (!node.IsDirectory && IsAiReady && _fileCommands.FileExists(node.FullPath))
            TypoCheckRequested?.Invoke(this, node.FullPath);
    }

    /// <summary>コンテキストメニューに出す「入力ありワークフロー」一覧。</summary>
    public IReadOnlyList<WorkflowSummary> InputWorkflows() => _workflows.ListInputWorkflows();

    /// <summary>指定ワークフローを、当該ファイルを構造化 input として実行するよう要求する
    /// （ShellWindow が AIバーをワークフローモードへ切替えて処理）。実在ファイルのときだけ発火する。</summary>
    public void RequestRunWorkflow(FileNodeViewModel? node, string workflowId)
    {
        if (node is { IsDirectory: false } && _fileCommands.FileExists(node.FullPath)
            && !string.IsNullOrEmpty(workflowId))
        {
            var relativePath = _workspace.RootPath is null
                ? null
                : Path.GetRelativePath(_workspace.RootPath, node.FullPath);
            WorkflowRequested?.Invoke(this,
                new WorkflowRunRequest(workflowId, WorkflowRunInput.FromFile(node.FullPath, relativePath)));
        }
    }

    /// <summary>指定ファイルの Git Blame 表示を要求する（ShellWindow がエディタペインでファイルを開き、
    /// VimEditorControl のネイティブ Git Blame 表示（:Gblame）をトリガーする）。実在ファイルのときだけ発火する。</summary>
    public void RequestGitBlame(FileNodeViewModel node)
    {
        if (node.IsDirectory || !_fileCommands.FileExists(node.FullPath))
            return;
        GitBlameRequested?.Invoke(this, node.FullPath);
    }

    /// <summary>指定ファイル／フォルダの Git 履歴表示を要求する（ShellWindow が Git ペインを前面に出し、
    /// そのパスの履歴に絞る）。Git リポジトリ配下かつ実在するときだけ発火する。</summary>
    public void RequestGitHistory(FileNodeViewModel node)
    {
        if (!_gitState.IsGitRepository)
            return;
        var exists = _fileCommands.EntryExists(node.FullPath, node.IsDirectory);
        if (exists)
            GitHistoryRequested?.Invoke(this, node.FullPath);
    }

    /// <summary>選択ノードのワークスペースルート相対パスを、ルート直下の .gitignore に1行追加する
    /// （フォルダは末尾に "/" を付ける）。.gitignore が無ければ新規作成し、同じ行が既にあれば追加しない
    /// （重複防止）。Git リポジトリではない・ルート未オープンなら何もしない（例外は投げない）。
    /// 書き込み後は git status に変化が出るので、Git ペインが見えていれば既存の
    /// <see cref="sk0ya.Loomo.Services.GitService.RepositoryChanged"/> 監視（ShellWindow が購読し、
    /// 開いているエディタタブをディスクから読み直す）が自然に追従する。</summary>
    public void AddToGitignore(FileNodeViewModel node)
    {
        if (_workspace.RootPath is null || !_gitState.IsGitRepository)
            return;

        if (_fileCommands.AddToGitignore(_workspace.RootPath, node.FullPath, node.IsDirectory))
            RefreshWorkspace();
    }
}

