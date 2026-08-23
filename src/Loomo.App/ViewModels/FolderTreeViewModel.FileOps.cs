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

    /// <summary>競合解決付きの貼り付け。スキップ／キャンセルはツリーを更新せず結果だけ返す。</summary>
    public FilePasteResult PasteEntry(
        string targetDirectory,
        string sourcePath,
        bool move,
        Func<FileConflictContext, FileConflictDecision> resolver)
    {
        var source = Path.GetFullPath(sourcePath);
        var isDirectory = _fileCommands.DirectoryExists(source);
        var result = _fileCommands.PasteWithConflict(targetDirectory, source, move, resolver);
        if (result.DestinationPath is not { } destination)
            return result;

        RefreshWorkspace();
        if (move)
            EntryRenamed?.Invoke(this, new EntryRenamedEventArgs(source, destination, isDirectory));
        return result;
    }

    /// <summary>ノードを同じフォルダー内へ複製し、複製後のフルパスを返す（同名衝突は貼り付けと同じく
    /// 「 - コピー」で一意化する）。見出しノード（ワークスペースフォルダー自身）は複製先の親が
    /// ワークスペース外になり得るので対象外＝null を返す。</summary>
    public string? DuplicateEntry(FileNodeViewModel node)
    {
        if (node.IsWorkspaceFolderRoot)
            return null;
        var parent = Path.GetDirectoryName(node.FullPath);
        return parent is null ? null : PasteEntry(parent, node.FullPath, move: false);
    }

    /// <summary>選択項目を ZIP に圧縮し、生成したアーカイブのパスを返す。ZIP は通常の
    /// ファイル操作履歴に積むため、Undo／Redo で生成物を戻せる。</summary>
    public string CompressEntries(IEnumerable<FileNodeViewModel> nodes)
        => CompressEntriesAsync(nodes).GetAwaiter().GetResult();

    /// <summary>ZIP 圧縮を UI スレッドから切り離して実行する。キャンセル時は履歴へ記録せず、
    /// 作成途中の一時ファイルもコマンド側で片付ける。</summary>
    public async Task<string> CompressEntriesAsync(
        IEnumerable<FileNodeViewModel> nodes,
        CancellationToken cancellationToken = default)
    {
        var paths = nodes
            .Where(n => n is not null && !n.IsWorkspaceFolderRoot)
            .Select(n => n.FullPath)
            .ToArray();
        var archive = await _fileCommands.CompressToZipAsync(paths, cancellationToken);
        RefreshWorkspace();
        return archive;
    }

    // ===== Undo / Redo（作成・名前の変更・移動・コピー・削除） =====
    // 履歴の実体はツリーとファイル一覧ペインで共有する FileOperationHistory（記録は
    // FolderTreeCommandHandler、逆操作は履歴側）。ここはその結果をツリーと開いているタブへ流す係。

    /// <summary>ファイル操作の Undo／Redo 履歴（メニューの出し分け・入力可否の判定に使う）。</summary>
    public FileOperationHistory History => _fileCommands.History;

    /// <summary>複数選択の削除・複数ファイルの貼り付けのように、1 回の Undo でまとめて戻したい
    /// 一連の操作をくくる（<c>using</c> を抜けたところで 1 手として記録される）。</summary>
    public IDisposable BeginFileOperationBatch() => History.BeginBatch();

    /// <summary>直近のファイル操作を元に戻す。戻せないときは <see cref="InvalidOperationException"/>
    /// （呼び出し側がメッセージを表示する）。</summary>
    public FileOperationResult UndoFileOperation() => ApplyHistoryResult(History.Undo());

    /// <summary>元に戻したファイル操作をやり直す。</summary>
    public FileOperationResult RedoFileOperation() => ApplyHistoryResult(History.Redo());

    /// <summary>ZIP の再生成を UI スレッドで塞がない非同期版。</summary>
    public async Task<FileOperationResult> RedoFileOperationAsync(CancellationToken cancellationToken = default)
        => ApplyHistoryResult(await History.RedoAsync(cancellationToken));

    // 逆操作でディスク上が変わった分をツリーへ反映し、開いているタブを追従（移動）・クローズ（削除）させる。
    // 監視（DebouncedFolderWatcher）任せにしないのは、通常のファイル操作と同じ即時性にするため。
    private FileOperationResult ApplyHistoryResult(FileOperationResult result)
    {
        RefreshWorkspace();
        foreach (var effect in result.Effects)
        {
            if (effect.MovedFrom is not null && effect.MovedTo is not null)
                EntryRenamed?.Invoke(this, new EntryRenamedEventArgs(effect.MovedFrom, effect.MovedTo, effect.IsDirectory));
            if (effect.Removed is not null)
                EntryDeleted?.Invoke(this, effect.Removed);
        }
        return result;
    }

    /// <summary>「相対パスをコピー」用の、ノードが属するワークスペースフォルダーからの相対パス。
    /// マルチルートでは所属フォルダーが基準（プライマリ固定にすると、あとから追加したフォルダーの
    /// ファイルが「..\..\」だらけの使えないパスになる）。どのフォルダーにも属さなければフルパスのまま。
    /// 基準は表示中ルート（ピン留めで切替わる）ではなくワークスペースフォルダー——ピン留めの状態で
    /// コピーされるパスが変わると、貼り付け先での意味が変わってしまうため。</summary>
    public string RelativePathFor(FileNodeViewModel node)
    {
        var baseFolder = FolderRootFor(node.RootKey) ?? _workspace.FolderForOrPrimary(node.FullPath);
        if (baseFolder is null)
            return node.FullPath;
        try { return Path.GetRelativePath(baseFolder, node.FullPath); }
        catch { return node.FullPath; }
    }

    public void NotifySelected(string fullPath) => _workspace.SelectedPath = fullPath;

    /// <summary>FolderTree 外で行われた移動をツリーと開いているエディタへ通知する。</summary>
    public void NotifyEntryMoved(string oldPath, string newPath, bool isDirectory)
    {
        RefreshWorkspace();
        EntryRenamed?.Invoke(this, new EntryRenamedEventArgs(oldPath, newPath, isDirectory));
    }

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

    /// <summary>Diff ペインでの比較を要求する（ShellWindow が処理）。<paramref name="rightPath"/> が null なら
    /// 左のファイルとクリップボードの比較。どちらも実在ファイルのときだけ発火する。</summary>
    public void RequestCompare(string leftPath, string? rightPath)
    {
        if (!_fileCommands.FileExists(leftPath))
            return;
        if (rightPath is not null && !_fileCommands.FileExists(rightPath))
            return;
        CompareRequested?.Invoke(this, new FileCompareRequest(leftPath, rightPath));
    }

    /// <summary>このフォルダーを検索の開始フォルダーにして検索パネルを開くよう要求する（ShellWindow が処理）。
    /// フォルダかつ実在のときだけ発火する。</summary>
    public void RequestSearchInFolder(FileNodeViewModel node)
    {
        if (node.IsDirectory && _fileCommands.DirectoryExists(node.FullPath))
            SearchInFolderRequested?.Invoke(this, node.FullPath);
    }

    /// <summary>この項目をファイル一覧ペインで開くよう要求する（ShellWindow が処理）。
    /// フォルダーはそこを開き、ファイルは親フォルダーを開いてその行を選ぶ。</summary>
    public void RequestRevealInFilesPane(FileNodeViewModel node)
    {
        if (_fileCommands.EntryExists(node.FullPath, node.IsDirectory))
            RevealInFilesPaneRequested?.Invoke(this, node.FullPath);
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
            var folderRoot = FolderRootFor(node.RootKey);
            var relativePath = folderRoot is null
                ? null
                : Path.GetRelativePath(folderRoot, node.FullPath);
            WorkflowRequested?.Invoke(this,
                new WorkflowRunRequest(workflowId, WorkflowRunInput.FromFile(node.FullPath, relativePath)));
        }
    }

    /// <summary>rootKey が属するワークスペースフォルダーの固定ルート（単一フォルダー時は
    /// ワークスペースルート、複数フォルダー時はそのフォルダー自身のパス）。</summary>
    private string? FolderRootFor(string rootKey)
        => _multiRootStates.Count == 0
            ? _workspace.PrimaryFolder
            : _multiRootStates.TryGetValue(rootKey, out var state) ? state.FolderPath : null;

    /// <summary>指定ファイルの Git Blame 表示を要求する（ShellWindow がエディタペインでファイルを開き、
    /// VimEditorControl のネイティブ Git Blame 表示（:Gblame）をトリガーする）。実在ファイルのときだけ発火する。</summary>
    public void RequestGitBlame(FileNodeViewModel node)
    {
        if (node.IsDirectory || !_fileCommands.FileExists(node.FullPath))
            return;
        GitBlameRequested?.Invoke(this, node.FullPath);
    }

    /// <summary>ファイル一覧からの「Git Blame」要求。ツリーのノードを作らず、同じ Git 判定と受け口を使う。</summary>
    public void RequestGitBlame(string fullPath)
    {
        if (!CanGitForPath(fullPath) || !_fileCommands.FileExists(fullPath))
            return;
        GitBlameRequested?.Invoke(this, fullPath);
    }

    /// <summary>指定ファイル／フォルダの Git 履歴表示を要求する（ShellWindow が Git ペインを前面に出し、
    /// そのパスの履歴に絞る）。Git リポジトリ配下かつ実在するときだけ発火する。</summary>
    public void RequestGitHistory(FileNodeViewModel node)
    {
        if (!IsGitRepositoryFor(node.RootKey))
            return;
        var exists = _fileCommands.EntryExists(node.FullPath, node.IsDirectory);
        if (exists)
            GitHistoryRequested?.Invoke(this, node.FullPath);
    }

    /// <summary>ファイル一覧からの「履歴を表示」要求。ファイル・フォルダーのどちらにも効く。</summary>
    public void RequestGitHistory(string fullPath, bool isDirectory)
    {
        if (!CanGitForPath(fullPath)
            || !_fileCommands.EntryExists(fullPath, isDirectory))
            return;
        GitHistoryRequested?.Invoke(this, fullPath);
    }

    /// <summary>選択ノードのワークスペースルート相対パスを、ルート直下の .gitignore に1行追加する
    /// （フォルダは末尾に "/" を付ける）。.gitignore が無ければ新規作成し、同じ行が既にあれば追加しない
    /// （重複防止）。Git リポジトリではない・ルート未オープンなら何もしない（例外は投げない）。
    /// 書き込み後は git status に変化が出るので、Git ペインが見えていれば既存の
    /// <see cref="sk0ya.Loomo.Services.GitService.RepositoryChanged"/> 監視（ShellWindow が購読し、
    /// 開いているエディタタブをディスクから読み直す）が自然に追従する。</summary>
    public void AddToGitignore(FileNodeViewModel node)
    {
        var folderRoot = FolderRootFor(node.RootKey);
        if (folderRoot is null || !IsGitRepositoryFor(node.RootKey))
            return;
        // ワークスペースフォルダー自身は対象外（相対パスが "." になり、"./" という無意味な行が入る）。
        if (PathsEqual(folderRoot, node.FullPath))
            return;

        if (_fileCommands.AddToGitignore(folderRoot, node.FullPath, node.IsDirectory))
            RefreshWorkspace();
    }

    /// <summary>ファイル一覧から選んだ項目を、所属ワークスペースの .gitignore に追加する。</summary>
    public void AddToGitignore(string fullPath, bool isDirectory)
    {
        var context = GitContextForPath(fullPath);
        if (context is null
            || !_fileCommands.EntryExists(fullPath, isDirectory)
            || PathsEqual(context.Value.FolderRoot, fullPath))
            return;

        if (_fileCommands.AddToGitignore(context.Value.FolderRoot, fullPath, isDirectory))
            RefreshWorkspace();
    }

    /// <summary>指定パスが FolderTree と同じ Git メニューの対象か。</summary>
    public bool CanGitForPath(string fullPath) => GitContextForPath(fullPath) is { State.IsGitRepository: true };

    /// <summary>指定パスを Git 操作へ送れるか（一覧のメニュー出し分け用）。</summary>
    public bool CanAddToGitignoreForPath(string fullPath)
        => GitContextForPath(fullPath) is { State.IsGitRepository: true } context
            && !PathsEqual(context.FolderRoot, fullPath);

    private (string FolderRoot, GitTreeState State)? GitContextForPath(string fullPath)
    {
        string full;
        try { full = Path.GetFullPath(fullPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }

        if (_multiRootStates.Count == 0)
        {
            var root = _workspace.PrimaryFolder;
            return root is not null && IsPathWithin(full, root)
                ? (root, _gitState)
                : null;
        }

        // マルチルートで入れ子の登録がある場合は、最も深いルートを採用する。
        foreach (var state in _multiRootStates.Values
                     .OrderByDescending(s => s.FolderPath.Length))
            if (IsPathWithin(full, state.FolderPath))
                return (state.FolderPath, state.GitState);
        return null;
    }

    private static bool IsPathWithin(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd('\\', '/');
        var fullDirectory = Path.GetFullPath(directory).TrimEnd('\\', '/');
        return string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
