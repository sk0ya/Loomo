namespace sk0ya.Loomo.App.Services;

public sealed record FolderTreeEntries(IReadOnlyList<string> Directories, IReadOnlyList<string> Files);

/// <summary>フォルダーツリーが表示するファイルシステム情報を読み取る。</summary>
public sealed class FolderTreeQuery
{
    private readonly WindowsFolderTreeShellNamespaceProvider _shell;
    private readonly GitWorkspaceStatus? _gitStatus;

    /// <param name="gitStatus">全リポジトリの状態の共有（§40.6）。null（テスト）ならその場で読む。</param>
    public FolderTreeQuery(WindowsFolderTreeShellNamespaceProvider? shell = null, GitWorkspaceStatus? gitStatus = null)
    {
        _shell = shell ?? new WindowsFolderTreeShellNamespaceProvider();
        _gitStatus = gitStatus;
        if (_gitStatus is not null)
            _gitStatus.RepositoryChanged += (_, root) => GitRepositoryChanged?.Invoke(this, root);
    }

    /// <summary>リポジトリの git ディレクトリが変わった（ターミナルでのコミット・チェックアウト等）。背景スレッドで鳴る。</summary>
    public event EventHandler<string>? GitRepositoryChanged;

    /// <summary>全リポジトリの状態の共有。ファイル一覧などツリー以外の面へも同じものを渡す。</summary>
    public GitWorkspaceStatus? GitStatus => _gitStatus;

    public bool DirectoryExists(string path)
        => FolderTreeShellNamespaces.IsShellPath(path) ? _shell.Exists(path) : Directory.Exists(path);

    public FolderTreeEntries EnumerateChildren(string path)
    {
        if (FolderTreeShellNamespaces.IsShellPath(path))
            return _shell.Enumerate(path);

        if (!Directory.Exists(path))
            return new FolderTreeEntries(Array.Empty<string>(), Array.Empty<string>());

        try
        {
            return new FolderTreeEntries(
                Directory.EnumerateDirectories(path)
                    .Where(p => !Path.GetFileName(p).StartsWith(".loomo-conflict-", StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
                Directory.EnumerateFiles(path)
                    .Where(p => !Path.GetFileName(p).StartsWith(".loomo-conflict-", StringComparison.OrdinalIgnoreCase))
                    .ToArray());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new FolderTreeEntries(Array.Empty<string>(), Array.Empty<string>());
        }
    }

    /// <summary>表示ルートの Git の状態を読み直す（同期・UI スレッドから呼ばない）。共有があれば、同じリポジトリに
    /// かかる他の表示ルート・面と git を1回にまとめる。</summary>
    internal GitTreeState LoadGitState(string rootPath, CancellationToken token)
        => _gitStatus?.TreeStateFor(rootPath, fresh: true, token) ?? GitTreeState.Load(rootPath, token);
}
