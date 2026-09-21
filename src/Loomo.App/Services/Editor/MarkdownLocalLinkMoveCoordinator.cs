using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.Services;

/// <summary>Markdown のローカルリンクが指す実体の移動と、参照更新をまとめて扱う。</summary>
internal static class MarkdownLocalLinkMoveCoordinator
{
    public static MarkdownLocalLinkMoveResult Move(
        IWorkspaceService workspace,
        string documentPath,
        string destination,
        string sourcePath,
        bool isDirectory,
        string oldText,
        Action<string> saveUpdatedText,
        Action<string, string, bool> notifyEntryMoved)
    {
        var (normalizedDestination, markdownDestination) =
            SelectionActionPolicy.NormalizeMarkdownDestination(destination);
        destination = normalizedDestination;
        if (Path.IsPathRooted(destination))
            throw new InvalidOperationException("ワークスペース内の相対パスを入力してください。");

        sourcePath = workspace.ResolvePath(sourcePath);
        var documentDirectory = Path.GetDirectoryName(documentPath)
            ?? throw new InvalidOperationException("編集中の Markdown ファイルの場所を取得できません。");
        var destinationPath = workspace.ResolvePath(Path.Combine(
            documentDirectory,
            Uri.UnescapeDataString(destination).Replace('/', Path.DirectorySeparatorChar)));
        if (PathsEqual(sourcePath, documentPath))
            throw new InvalidOperationException("編集中の Markdown ファイル自身はこの操作では移動できません。");
        if (PathsEqual(sourcePath, destinationPath))
            return new(false, destination);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            throw new InvalidOperationException("移動先には同名のファイルまたはフォルダーが既にあります。");
        if (Path.GetDirectoryName(destinationPath) is not { } parent)
            throw new InvalidOperationException("移動先のフォルダーを取得できません。");

        var newText = MarkdownLocalLinkTextRewriter.ReplaceReferences(
            oldText, workspace, documentPath, sourcePath, markdownDestination);
        var createdDirectories = CreateMissingDirectories(parent);
        try
        {
            if (isDirectory)
                Directory.Move(sourcePath, destinationPath);
            else
                File.Move(sourcePath, destinationPath);
            saveUpdatedText(newText);
        }
        catch
        {
            if (isDirectory && Directory.Exists(destinationPath))
                Directory.Move(destinationPath, sourcePath);
            else if (!isDirectory && File.Exists(destinationPath))
                File.Move(destinationPath, sourcePath);
            saveUpdatedText(oldText);
            RemoveCreatedDirectories(createdDirectories);
            throw;
        }

        notifyEntryMoved(sourcePath, destinationPath, isDirectory);
        return new(true, destination);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> CreateMissingDirectories(string directory)
    {
        var missing = new List<string>();
        for (var current = directory; !Directory.Exists(current); current = Path.GetDirectoryName(current)!)
        {
            if (string.IsNullOrWhiteSpace(current))
                throw new InvalidOperationException("移動先のフォルダーを作成できません。");
            missing.Add(current);
        }
        Directory.CreateDirectory(directory);
        return missing;
    }

    private static void RemoveCreatedDirectories(IReadOnlyList<string> directories)
    {
        foreach (var directory in directories)
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch
            {
                // 元のファイル移動／保存エラーを優先する。
            }
        }
    }
}

internal readonly record struct MarkdownLocalLinkMoveResult(bool Moved, string Destination);
