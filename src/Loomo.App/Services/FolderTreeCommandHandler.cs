using System.Text;
using Microsoft.VisualBasic.FileIO;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.Services;

/// <summary>フォルダーツリーから要求されるファイル操作を実行する。</summary>
public sealed class FolderTreeCommandHandler
{
    private readonly IWorkspaceService _workspace;

    public FolderTreeCommandHandler(IWorkspaceService workspace) => _workspace = workspace;

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool EntryExists(string path, bool isDirectory) =>
        isDirectory ? Directory.Exists(path) : File.Exists(path);

    public string Create(string parentDirectory, string name, bool isDirectory)
    {
        ValidateName(name);
        var fullPath = _workspace.ResolvePath(Path.Combine(parentDirectory, name));
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new InvalidOperationException("同じ名前の項目が既に存在します。");

        try
        {
            if (isDirectory)
                Directory.CreateDirectory(fullPath);
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
        return fullPath;
    }

    public string Rename(string path, string newName, bool isDirectory)
    {
        ValidateName(newName);
        var oldPath = _workspace.ResolvePath(path);
        var parent = Path.GetDirectoryName(oldPath)
            ?? throw new InvalidOperationException("親ディレクトリを特定できません。");
        var newPath = _workspace.ResolvePath(Path.Combine(parent, newName));
        if (string.Equals(oldPath, newPath, StringComparison.Ordinal)) return oldPath;
        if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)
            && (File.Exists(newPath) || Directory.Exists(newPath)))
            throw new InvalidOperationException("同じ名前の項目が既に存在します。");

        try
        {
            if (isDirectory) Directory.Move(oldPath, newPath);
            else File.Move(oldPath, newPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"名前の変更に失敗しました: {ex.Message}", ex);
        }
        return newPath;
    }

    public void Delete(string path, bool isDirectory)
    {
        path = _workspace.ResolvePath(path);
        try
        {
            if (isDirectory && Directory.Exists(path))
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else if (!isDirectory && File.Exists(path))
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            throw new InvalidOperationException($"削除に失敗しました: {ex.Message}", ex);
        }
    }

    public string Paste(string targetDirectory, string sourcePath, bool move)
    {
        var source = Path.GetFullPath(sourcePath);
        var isDirectory = Directory.Exists(source);
        if (!isDirectory && !File.Exists(source))
            throw new InvalidOperationException("貼り付け元が見つかりません。");
        var targetDir = _workspace.ResolvePath(targetDirectory);
        if (isDirectory && (PathsEqual(source, targetDir) || IsPathUnder(targetDir, source)))
            throw new InvalidOperationException("フォルダーを自身の中へは貼り付けできません。");

        var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var destination = _workspace.ResolvePath(Path.Combine(targetDir, name));
        if (move && PathsEqual(source, destination)) return destination;
        destination = EnsureUniqueDestination(destination, isDirectory);

        try
        {
            if (isDirectory)
            {
                if (move) Directory.Move(source, destination);
                else CopyDirectory(source, destination);
            }
            else if (move) File.Move(source, destination);
            else File.Copy(source, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"貼り付けに失敗しました: {ex.Message}", ex);
        }
        return destination;
    }

    public bool AddToGitignore(string workspaceRoot, string fullPath, bool isDirectory)
    {
        var relativePath = Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/');
        if (isDirectory) relativePath += "/";
        var gitignorePath = Path.Combine(workspaceRoot, ".gitignore");
        var existingText = File.Exists(gitignorePath) ? File.ReadAllText(gitignorePath) : "";
        if (existingText.Split('\n').Any(line => line.Trim() == relativePath)) return false;

        try
        {
            var prefix = existingText.Length > 0 && existingText[^1] is not ('\n' or '\r') ? "\n" : "";
            File.AppendAllText(gitignorePath, prefix + relativePath + "\n", Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($".gitignore への追加に失敗しました: {ex.Message}", ex);
        }
        return true;
    }

    private static string EnsureUniqueDestination(string destination, bool isDirectory)
    {
        if (!File.Exists(destination) && !Directory.Exists(destination)) return destination;
        var dir = Path.GetDirectoryName(destination)!;
        var name = Path.GetFileName(destination);
        var ext = isDirectory ? "" : Path.GetExtension(name);
        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        for (var i = 1; ; i++)
        {
            var suffix = i == 1 ? " - コピー" : $" - コピー ({i})";
            var candidate = Path.Combine(dir, stem + suffix + ext);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'),
            Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool IsPathUnder(string path, string directory)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\', '/');
        var parent = Path.GetFullPath(directory).TrimEnd('\\', '/');
        return full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(parent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("名前を入力してください。");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("名前に使用できない文字が含まれています。");
        if (name is "." or "..") throw new InvalidOperationException("その名前は使用できません。");
    }
}
