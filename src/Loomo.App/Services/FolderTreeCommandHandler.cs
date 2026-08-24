using System.Text;
using System.IO.Compression;
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
/// エージェント側の防御は変わらない。</para>
///
/// <para>成功した操作は <see cref="FileOperationHistory"/> へ 1 件ずつ記録し、Undo／Redo（逆操作）は
/// そちらが持つ。履歴はツリーとファイル一覧ペインで共有する 1 本（DI シングルトン）なので、
/// 限定つき・限定なしのどちらのインスタンスから行った操作も同じ履歴に積まれる。</para></summary>
public sealed class FolderTreeCommandHandler
{
    private readonly IWorkspaceService _workspace;
    private readonly FileOperationHistory _history;
    private readonly bool _confineToWorkspace;

    public FolderTreeCommandHandler(IWorkspaceService workspace, FileOperationHistory history)
        : this(workspace, history, confineToWorkspace: true) { }

    private FolderTreeCommandHandler(IWorkspaceService workspace, FileOperationHistory history, bool confineToWorkspace)
    {
        _workspace = workspace;
        _history = history;
        _confineToWorkspace = confineToWorkspace;
    }

    /// <summary>ワークスペース外でも操作できる版（ファイル一覧ペイン用）。</summary>
    public static FolderTreeCommandHandler Unconfined(IWorkspaceService workspace, FileOperationHistory history)
        => new(workspace, history, false);

    /// <summary>Undo／Redo の履歴（ツリーとファイル一覧ペインで共有）。</summary>
    public FileOperationHistory History => _history;

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
        _history.Record(FileOperation.Created(fullPath, isDirectory));
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
        _history.Record(FileOperation.Renamed(oldPath, newPath, isDirectory));
        return newPath;
    }

    public void Delete(string path, bool isDirectory)
    {
        path = Resolve(path);
        if (!(isDirectory ? Directory.Exists(path) : File.Exists(path)))
            return;   // 既に無いものは履歴にも残さない（戻す先が無い）。
        SendToRecycleBin(path, isDirectory);
        _history.Record(FileOperation.Deleted(path, isDirectory));
    }

    /// <summary>選択項目を同じ親フォルダーの ZIP に圧縮する。生成物は履歴へ記録し、
    /// Redo では元の選択項目からアーカイブを再生成する。</summary>
    public string CompressToZip(IEnumerable<string> sourcePaths)
        => CompressToZipAsync(sourcePaths).GetAwaiter().GetResult();

    /// <summary>選択項目をバックグラウンドから呼び出せる形で ZIP に圧縮する。
    /// 最終パスへ直接書かず、同じ親の隠し一時ファイルを完成後に移動するので、失敗／キャンセルで
    /// 壊れた ZIP が残らない。</summary>
    public async Task<string> CompressToZipAsync(
        IEnumerable<string> sourcePaths,
        CancellationToken cancellationToken = default)
    {
        if (sourcePaths is null) throw new ArgumentNullException(nameof(sourcePaths));

        var candidates = sourcePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Resolve)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .ToArray();
        if (candidates.Length == 0)
            throw new InvalidOperationException("ZIP にする項目がありません。");

        // 親フォルダーとその子を同時に選んだ場合は親だけを入れる。重複したエントリを作らず、
        // 「ファイル→そのファイルを含むフォルダー」の選択で出力 ZIP 自身を読み込むことも防ぐ。
        var sourceDirectories = candidates.Where(Directory.Exists).ToArray();
        var sources = candidates
            .Where(path => !sourceDirectories.Any(parent =>
                !PathsEqual(path, parent) && IsPathWithin(path, parent)))
            .ToArray();

        var parent = Path.GetDirectoryName(sources[0])
            ?? throw new InvalidOperationException("ZIP の作成先を特定できません。");
        var stem = sources.Length == 1
            ? Path.GetFileNameWithoutExtension(sources[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : "archive";
        if (string.IsNullOrWhiteSpace(stem)) stem = "archive";
        var destination = EnsureUniqueDestination(Path.Combine(parent, stem + ".zip"), false);

        await CreateZipFileAsync(sources, destination, cancellationToken).ConfigureAwait(false);
        _history.Record(FileOperation.Compressed(sources, destination));
        return destination;
    }

    /// <summary>ZIP の共通作成処理。Redo からも呼ばれるため、成功前に履歴を変更しない。
    /// 再解析ポイントは辿らない。</summary>
    internal static void CreateZipFile(IReadOnlyList<string> sources, string destination)
        => CreateZipFileAsync(sources, destination).GetAwaiter().GetResult();

    internal static async Task CreateZipFileAsync(
        IReadOnlyList<string> sources,
        string destination,
        CancellationToken cancellationToken = default)
    {
        var temporary = destination + ".loomo-tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                try { File.SetAttributes(temporary, FileAttributes.Hidden); } catch { /* 表示属性は補助的 */ }

                foreach (var source in sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Directory.Exists(source))
                        await AddDirectoryAsync(
                            archive,
                            source,
                            Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                            temporary,
                            destination,
                            cancellationToken).ConfigureAwait(false);
                    else if (File.Exists(source))
                        await AddFileAsync(archive, source, Path.GetFileName(source), cancellationToken)
                            .ConfigureAwait(false);
                }
            }

            File.Move(temporary, destination);
            // 一時ファイルに付けた Hidden は Move で引き継がれる。落とさないと、できあがった
            // ZIP が隠しファイルのまま＝一覧にもツリーにも出ず、直後の「作った ZIP を選ぶ」も
            // 空振りして、圧縮が失敗したように見える。
            try { File.SetAttributes(destination, FileAttributes.Normal); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
            {
                // 属性を戻せなくても ZIP 自体は作れている。
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                   or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            TryDelete(temporary);
            throw new InvalidOperationException($"ZIP の作成に失敗しました: {ex.Message}", ex);
        }
        finally { TryDelete(temporary); }
    }

    private static async Task AddDirectoryAsync(
        ZipArchive archive,
        string directory,
        string entryRoot,
        string temporary,
        string destination,
        CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PathsEqual(file, temporary) || PathsEqual(file, destination))
                continue;
            var relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
            await AddFileAsync(archive, file, entryRoot + "/" + relative, cancellationToken)
                .ConfigureAwait(false);
        }
        foreach (var child in Directory.EnumerateDirectories(directory, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(child);
            await AddDirectoryAsync(
                archive, child, entryRoot + "/" + name, temporary, destination, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task AddFileAsync(
        ZipArchive archive,
        string source,
        string entryName,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        await using var output = entry.Open();
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsPathWithin(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd('\\', '/');
        var fullDirectory = Path.GetFullPath(directory).TrimEnd('\\', '/');
        return string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch { /* 元の ZIP／キャンセル結果を優先する */ }
    }

    /// <summary>ゴミ箱へ送る（完全削除ではない）。Undo の逆操作（作成・コピーの取り消し）も
    /// ここを通す——「消した」ものは必ずゴミ箱に居る、という一点を守るため。</summary>
    internal static void SendToRecycleBin(string path, bool isDirectory)
    {
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
        => PasteWithConflict(targetDirectory, sourcePath, move, resolver: null).DestinationPath
            ?? throw new InvalidOperationException("貼り付けはキャンセルされました。");

    /// <summary>競合時の選択を呼び出し側へ委譲して貼り付ける。resolver が null の場合は従来どおり
    /// 「 - コピー」で一意化する。スキップ／キャンセルはファイルを変更せず履歴にも記録しない。</summary>
    public FilePasteResult PasteWithConflict(
        string targetDirectory,
        string sourcePath,
        bool move,
        Func<FileConflictContext, FileConflictDecision>? resolver)
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
        if (move && PathsEqual(source, destination)) return new FilePasteResult(destination);

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            if (resolver is null)
                destination = EnsureUniqueDestination(destination, isDirectory);
            else
            {
                var decision = resolver(new FileConflictContext(source, destination, isDirectory, move));
                switch (decision.Action)
                {
                    case FileConflictAction.Skip:
                        return FilePasteResult.Skip();
                    case FileConflictAction.Cancel:
                        return FilePasteResult.Cancel();
                    case FileConflictAction.Rename:
                        var renamed = ResolveRenamedDestination(targetDir, decision.NewName);
                        if (File.Exists(renamed) || Directory.Exists(renamed))
                        {
                            // 名前変更先も埋まっている場合は、同じダイアログをもう一度開けるよう
                            // 呼び出し側の resolver に再提示する。
                            return PasteWithConflictAtDestination(source, targetDir, renamed, move, isDirectory, resolver);
                        }
                        destination = renamed;
                        break;
                    case FileConflictAction.Overwrite:
                        break;
                    default:
                        throw new InvalidOperationException("不明な競合解決です。");
                }
            }
        }

        var replaced = (File.Exists(destination) || Directory.Exists(destination))
            ? BackupExistingDestination(destination)
            : null;
        try { ExecutePaste(source, destination, move, isDirectory); }
        catch
        {
            if (replaced is not null)
            {
                RemovePartialDestination(destination);
                RestoreBackup(replaced, destination);
            }
            throw;
        }
        _history.Record(move
            ? FileOperation.Moved(source, destination, isDirectory, replaced)
            : FileOperation.Copied(source, destination, isDirectory, replaced));
        return new FilePasteResult(destination);
    }

    private FilePasteResult PasteWithConflictAtDestination(
        string source, string targetDirectory, string destination, bool move, bool isDirectory,
        Func<FileConflictContext, FileConflictDecision> resolver)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            var decision = resolver(new FileConflictContext(source, destination, isDirectory, move));
            if (decision.Action == FileConflictAction.Skip) return FilePasteResult.Skip();
            if (decision.Action == FileConflictAction.Cancel) return FilePasteResult.Cancel();
            if (decision.Action == FileConflictAction.Rename)
            {
                destination = ResolveRenamedDestination(targetDirectory, decision.NewName);
                return PasteWithConflictAtDestination(source, targetDirectory, destination, move, isDirectory, resolver);
            }
            if (decision.Action != FileConflictAction.Overwrite)
                throw new InvalidOperationException("不明な競合解決です。");
        }

        var replaced = (File.Exists(destination) || Directory.Exists(destination))
            ? BackupExistingDestination(destination)
            : null;
        try { ExecutePaste(source, destination, move, isDirectory); }
        catch
        {
            if (replaced is not null)
            {
                RemovePartialDestination(destination);
                RestoreBackup(replaced, destination);
            }
            throw;
        }
        _history.Record(move
            ? FileOperation.Moved(source, destination, isDirectory, replaced)
            : FileOperation.Copied(source, destination, isDirectory, replaced));
        return new FilePasteResult(destination);
    }

    private static void ExecutePaste(string source, string destination, bool move, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                if (move) Directory.Move(source, destination);
                else CopyDirectoryTree(source, destination);
            }
            else if (move) File.Move(source, destination);
            else File.Copy(source, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"貼り付けに失敗しました: {ex.Message}", ex);
        }
    }

    private static string ResolveRenamedDestination(string targetDirectory, string? newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new InvalidOperationException("新しい名前を入力してください。");
        ValidateName(newName.Trim());
        var target = Path.GetFullPath(Path.Combine(targetDirectory, newName.Trim()));
        return target;
    }

    /// <summary>上書き対象を同じ親へ一時退避する。Undo で元の項目を復元するため、履歴に残す。</summary>
    private static string BackupExistingDestination(string destination)
    {
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("貼り付け先の親フォルダーを特定できません。");
        var backup = Path.Combine(parent, $".loomo-conflict-{Guid.NewGuid():N}-{Path.GetFileName(destination)}");
        try
        {
            if (Directory.Exists(destination)) Directory.Move(destination, backup);
            else File.Move(destination, backup);
            try { File.SetAttributes(backup, FileAttributes.Hidden); } catch { /* 非表示化は補助 */ }
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"上書き対象を退避できませんでした: {ex.Message}", ex);
        }
    }

    private static void RemovePartialDestination(string destination)
    {
        try
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            else if (File.Exists(destination)) File.Delete(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"失敗した貼り付け結果を片付けられませんでした: {ex.Message}", ex);
        }
    }

    internal static void RestoreBackup(string backup, string destination)
    {
        try
        {
            if (Directory.Exists(backup)) Directory.Move(backup, destination);
            else if (File.Exists(backup)) File.Move(backup, destination);
            else throw new InvalidOperationException("上書き前の項目が見つかりません。");
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"上書き前の項目を復元できませんでした: {ex.Message}", ex);
        }
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

    /// <summary>フォルダーを中身ごとコピーする。Redo（コピーのやり直し）からも使う。</summary>
    internal static void CopyDirectoryTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectoryTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
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
