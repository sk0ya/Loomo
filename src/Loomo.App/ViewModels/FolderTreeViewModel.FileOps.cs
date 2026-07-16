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
        ValidateName(name);
        var fullPath = _workspace.ResolvePath(Path.Combine(parentDirectory, name));

        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new InvalidOperationException("同じ名前の項目が既に存在します。");

        try
        {
            if (isDirectory)
            {
                Directory.CreateDirectory(fullPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                using (File.Create(fullPath)) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"作成に失敗しました: {ex.Message}", ex);
        }

        RefreshWorkspace();
        return fullPath;
    }

    /// <summary>ノードを新しい名前へ変更し、変更後のフルパスを返す。</summary>
    public string RenameEntry(FileNodeViewModel node, string newName)
    {
        ValidateName(newName);
        var oldPath = _workspace.ResolvePath(node.FullPath);
        var parent = Path.GetDirectoryName(oldPath)
            ?? throw new InvalidOperationException("親ディレクトリを特定できません。");
        var newPath = _workspace.ResolvePath(Path.Combine(parent, newName));

        if (string.Equals(oldPath, newPath, StringComparison.Ordinal))
            return oldPath;   // 変更なし

        // 大文字小文字だけの変更は許容しつつ、別項目との衝突は防ぐ。
        if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)
            && (File.Exists(newPath) || Directory.Exists(newPath)))
            throw new InvalidOperationException("同じ名前の項目が既に存在します。");

        try
        {
            if (node.IsDirectory)
                Directory.Move(oldPath, newPath);
            else
                File.Move(oldPath, newPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"名前の変更に失敗しました: {ex.Message}", ex);
        }

        RefreshWorkspace();
        // 開いているエディタタブを新パスへ追従させる（フォルダなら配下のファイルも対象）。
        EntryRenamed?.Invoke(this, new EntryRenamedEventArgs(oldPath, newPath, node.IsDirectory));
        return newPath;
    }

    /// <summary>ノードをゴミ箱へ送る（完全削除ではない）。</summary>
    public void DeleteEntry(FileNodeViewModel node)
    {
        var path = _workspace.ResolvePath(node.FullPath);
        try
        {
            if (node.IsDirectory)
            {
                if (Directory.Exists(path))
                    FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            else
            {
                if (File.Exists(path))
                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            throw new InvalidOperationException($"削除に失敗しました: {ex.Message}", ex);
        }

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
        var isDirectory = Directory.Exists(source);
        if (!isDirectory && !File.Exists(source))
            throw new InvalidOperationException("貼り付け元が見つかりません。");

        var targetDir = _workspace.ResolvePath(targetDirectory);

        // フォルダを自身の中（または配下）へ貼ると無限再帰になるため拒否する。
        if (isDirectory && (PathsEqual(source, targetDir) || IsPathUnder(targetDir, source)))
            throw new InvalidOperationException("フォルダーを自身の中へは貼り付けできません。");

        var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var destination = _workspace.ResolvePath(Path.Combine(targetDir, name));

        // 同じ場所への移動は何もしない（元の位置に置いたまま）。
        if (move && PathsEqual(source, destination))
            return destination;

        destination = EnsureUniqueDestination(destination, isDirectory);

        try
        {
            if (isDirectory)
            {
                if (move) Directory.Move(source, destination);
                else CopyDirectory(source, destination);
            }
            else
            {
                if (move) File.Move(source, destination);
                else File.Copy(source, destination);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"貼り付けに失敗しました: {ex.Message}", ex);
        }

        RefreshWorkspace();
        // 移動はリネームと同じく、開いているエディタタブを新パスへ追従させる。
        if (move)
            EntryRenamed?.Invoke(this, new EntryRenamedEventArgs(source, destination, isDirectory));
        return destination;
    }

    // 貼り付け先が既存なら「 - コピー」「 - コピー (2)」…を付けて空きパスを返す。
    private static string EnsureUniqueDestination(string destination, bool isDirectory)
    {
        if (!File.Exists(destination) && !Directory.Exists(destination))
            return destination;

        var dir = Path.GetDirectoryName(destination)!;
        var name = Path.GetFileName(destination);
        var ext = isDirectory ? "" : Path.GetExtension(name);
        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);

        for (var i = 1; ; i++)
        {
            var suffix = i == 1 ? " - コピー" : $" - コピー ({i})";
            var candidate = Path.Combine(dir, stem + suffix + ext);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    // path が directory と同じか、その配下にあるか。
    private static bool IsPathUnder(string path, string directory)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\', '/');
        var dir = Path.GetFullPath(directory).TrimEnd('\\', '/');
        return full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(dir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("名前を入力してください。");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("名前に使用できない文字が含まれています。");
        if (name is "." or "..")
            throw new InvalidOperationException("その名前は使用できません。");
    }

    public void NotifySelected(string fullPath) => _workspace.SelectedPath = fullPath;

    public void NotifyActivated(string fullPath)
    {
        _workspace.SelectedPath = fullPath;
        if (File.Exists(fullPath))
            FileActivated?.Invoke(this, fullPath);
    }

    public void NotifyPreviewRequested(string fullPath)
    {
        _workspace.SelectedPath = fullPath;
        if (File.Exists(fullPath))
            FilePreviewRequested?.Invoke(this, fullPath);
    }

    /// <summary>HTML ファイルをアプリ内ブラウザで開くよう要求する（ShellWindow が処理）。</summary>
    public void RequestOpenInBrowser(string fullPath)
    {
        if (File.Exists(fullPath))
            OpenInBrowserRequested?.Invoke(this, fullPath);
    }

    /// <summary>項目を可視ターミナルへセットするよう要求する（ShellWindow が処理）。
    /// フォルダはそのフォルダへ cd、ファイルはパスをプロンプトへ入力する。</summary>
    public void RequestSetInTerminal(FileNodeViewModel node)
    {
        if (node.IsDirectory ? Directory.Exists(node.FullPath) : File.Exists(node.FullPath))
            SetInTerminalRequested?.Invoke(this, new TerminalSetRequest(node.FullPath, node.IsDirectory));
    }

    /// <summary>このフォルダーを検索の開始フォルダーにして検索パネルを開くよう要求する（ShellWindow が処理）。
    /// フォルダかつ実在のときだけ発火する。</summary>
    public void RequestSearchInFolder(FileNodeViewModel node)
    {
        if (node.IsDirectory && Directory.Exists(node.FullPath))
            SearchInFolderRequested?.Invoke(this, node.FullPath);
    }

    /// <summary>指定ファイルの誤字脱字チェックを要求する（ShellWindow が AIバーで処理）。
    /// AI が使える状態（暖機完了）かつ実在ファイルのときだけ発火する。</summary>
    public void RequestTypoCheck(FileNodeViewModel node)
    {
        if (!node.IsDirectory && IsAiReady && File.Exists(node.FullPath))
            TypoCheckRequested?.Invoke(this, node.FullPath);
    }

    /// <summary>コンテキストメニューに出す「入力ありワークフロー」一覧。</summary>
    public IReadOnlyList<WorkflowSummary> InputWorkflows() => _workflows.ListInputWorkflows();

    /// <summary>指定ワークフローを、当該ファイルを構造化 input として実行するよう要求する
    /// （ShellWindow が AIバーをワークフローモードへ切替えて処理）。実在ファイルのときだけ発火する。</summary>
    public void RequestRunWorkflow(FileNodeViewModel? node, string workflowId)
    {
        if (node is { IsDirectory: false } && File.Exists(node.FullPath)
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
        if (node.IsDirectory || !File.Exists(node.FullPath))
            return;
        GitBlameRequested?.Invoke(this, node.FullPath);
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

        var relativePath = Path.GetRelativePath(_workspace.RootPath, node.FullPath).Replace('\\', '/');
        if (node.IsDirectory)
            relativePath += "/";

        var gitignorePath = Path.Combine(_workspace.RootPath, ".gitignore");
        var existingText = File.Exists(gitignorePath) ? File.ReadAllText(gitignorePath) : "";
        if (existingText.Split('\n').Any(line => line.Trim() == relativePath))
            return;   // 既に同じ行があれば何もしない

        try
        {
            // 既存の末尾に改行が無ければ先に補い、行を分ける（末尾が空/既に改行済みならそのまま追記）。
            var needsLeadingNewline = existingText.Length > 0 && existingText[^1] is not ('\n' or '\r');
            File.AppendAllText(
                gitignorePath, (needsLeadingNewline ? "\n" : "") + relativePath + "\n", Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($".gitignore への追加に失敗しました: {ex.Message}", ex);
        }

        RefreshWorkspace();
    }
}

