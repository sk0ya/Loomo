namespace sk0ya.Loomo.App.Services;

public sealed record FolderTreeEntries(IReadOnlyList<string> Directories, IReadOnlyList<string> Files);

/// <summary>フォルダーツリーが表示するファイルシステム情報を読み取る。</summary>
public sealed class FolderTreeQuery
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public FolderTreeEntries EnumerateChildren(string path)
    {
        if (!Directory.Exists(path))
            return new FolderTreeEntries(Array.Empty<string>(), Array.Empty<string>());

        try
        {
            return new FolderTreeEntries(
                Directory.EnumerateDirectories(path).ToArray(),
                Directory.EnumerateFiles(path).ToArray());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new FolderTreeEntries(Array.Empty<string>(), Array.Empty<string>());
        }
    }

    internal GitTreeState LoadGitState(string rootPath, CancellationToken token)
        => GitTreeState.Load(rootPath, token);
}
