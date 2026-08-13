using System.Text;
using Microsoft.VisualBasic.FileIO;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.Services;

/// <summary>フォルダーツリー／ファイル一覧ペインから要求されるファイル操作を実行する。
///
/// <para><b>ワークスペース配下への限定は「エージェントの手綱」であって人間の足枷ではない</b>
/// （設計書 §10 はツールのパストラバーサル防止の話）。ツリーはワークスペースしか映さないので
/// 既定は限定つき（<see cref="IWorkspaceService.ResolvePath"/> 経由）だが、ファイル一覧ペインは
/// 外のフォルダーも開けるファイラなので <see cref="Unconfined"/> を使い、エクスプローラーと同じに
/// 振る舞う。AI の <c>write_file</c>／<c>edit_file</c> は従来どおり ResolvePath を通るので、
/// エージェント側の防御は変わらない。</para></summary>
public sealed class FolderTreeCommandHandler
{
    private readonly IWorkspaceService _workspace;
    private readonly bool _confineToWorkspace;

    public FolderTreeCommandHandler(IWorkspaceService workspace) : this(workspace, confineToWorkspace: true) { }

    private FolderTreeCommandHandler(IWorkspaceService workspace, bool confineToWorkspace)
    {
        _workspace = workspace;
        _confineToWorkspace = confineToWorkspace;
    }

    /// <summary>ワークスペース外でも操作できる版（ファイル一覧ペイン用）。</summary>
    public static FolderTreeCommandHandler Unconfined(IWorkspaceService workspace) => new(workspace, false);

    /// <summary>パスの正規化。限定つきなら <see cref="IWorkspaceService.ResolvePath"/>（ワークスペース外は拒否）、
    /// 限定なしなら素の絶対パス化。</summary>
    private string Resolve(string path)
        => _confineToWorkspace ? _workspace.ResolvePath(path) : Path.GetFullPath(path);

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool EntryExists(string path, bool isDirectory) =>
        isDirectory ? Directory.Exists(path) : File.Exists(path);

    public string Create(string parentDirectory, string name, bool isDirectory)
    {
        ValidateName(name);
        var fullPath = Resolve(Path.Combine(parentDirectory, name));
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
        var oldPath = Resolve(path);
        var parent = Path.GetDirectoryName(oldPath)
            ?? throw new InvalidOperationException("親ディレクトリを特定できません。");
        var newPath = Resolve(Path.Combine(parent, newName));
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
        path = Resolve(path);
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
        var targetDir = Resolve(targetDirectory);
        if (isDirectory && (PathsEqual(source, targetDir) || IsPathUnder(targetDir, source)))
            throw new InvalidOperationException("フォルダーを自身の中へは貼り付けできません。");

        var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var destination = Resolve(Path.Combine(targetDir, name));
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
