using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace sk0ya.Loomo.App.Services;

// ツリー上の差分マーク（FileNodeViewModel.GitStatus）の種別。表示文字・色は XAML 側の
// DataTrigger で割り当てる。DirectoryChanged は「配下に変更を含むフォルダ」を表す集約マーク。
public enum GitChangeKind
{
    None,
    Modified,
    Added,
    Deleted,
    Renamed,
    Untracked,
    Conflicted,
    DirectoryChanged,
}

internal sealed class GitTreeState
{
    private readonly string _rootPath;
    private readonly string? _gitRootPath;

    public static GitTreeState Empty { get; } = new("", null, new(), new(), new());

    public bool IsGitRepository => _gitRootPath is not null;

    /// <summary>変更ファイルのフルパス → 変更種別。</summary>
    public Dictionary<string, GitChangeKind> FileStatuses { get; }
    public HashSet<string> ChangedFiles { get; }
    public HashSet<string> ChangedDirectories { get; }

    private GitTreeState(
        string rootPath,
        string? gitRootPath,
        Dictionary<string, GitChangeKind> fileStatuses,
        HashSet<string> changedFiles,
        HashSet<string> changedDirectories)
    {
        _rootPath = rootPath;
        _gitRootPath = gitRootPath;
        FileStatuses = fileStatuses;
        ChangedFiles = changedFiles;
        ChangedDirectories = changedDirectories;
    }

    /// <summary>指定ファイルの変更種別（変更なしは None）。</summary>
    public GitChangeKind GetFileStatus(string fullPath)
        => FileStatuses.TryGetValue(Path.GetFullPath(fullPath), out var kind) ? kind : GitChangeKind.None;

    public static GitTreeState Load(string rootPath, CancellationToken token)
    {
        var fullRoot = Path.GetFullPath(rootPath);
        var gitRoot = FindGitRoot(fullRoot, token);
        if (gitRoot is null)
            return new GitTreeState(fullRoot, null, new(), new(), new());

        token.ThrowIfCancellationRequested();
        var fileStatuses = LoadFileStatuses(gitRoot, token);
        token.ThrowIfCancellationRequested();
        var changedFiles = new HashSet<string>(fileStatuses.Keys, StringComparer.OrdinalIgnoreCase);
        var changedDirectories = LoadChangedDirectories(changedFiles, fullRoot);
        return new GitTreeState(fullRoot, gitRoot, fileStatuses, changedFiles, changedDirectories);
    }

    public HashSet<string> GetIgnoredPaths(IEnumerable<string> fullPaths)
    {
        var ignoredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedPaths = fullPaths.Select(Path.GetFullPath).ToArray();

        foreach (var path in normalizedPaths.Where(IsInsideGitDirectory))
            ignoredPaths.Add(path);

        if (_gitRootPath is null)
            return ignoredPaths;

        var candidates = normalizedPaths
            .Where(path => !ignoredPaths.Contains(path))
            .Select(path => new
            {
                FullPath = path,
                RelativePath = ToGitRelativePath(_gitRootPath, path)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.RelativePath))
            .ToArray();

        if (candidates.Length == 0)
            return ignoredPaths;

        var input = string.Join('\0', candidates.Select(x => x.RelativePath)) + '\0';
        var result = RunGit(_gitRootPath, input, new[] { "check-ignore", "-z", "--stdin" });
        if (result.ExitCode is not 0 and not 1)
            return ignoredPaths;

        foreach (var relativePath in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            ignoredPaths.Add(Path.GetFullPath(Path.Combine(_gitRootPath, relativePath)));

        return ignoredPaths;
    }

    private bool IsInsideGitDirectory(string fullPath)
    {
        var gitDir = Path.Combine(_gitRootPath ?? _rootPath, ".git");
        var normalizedGitDir = Path.GetFullPath(gitDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fullPath.Equals(normalizedGitDir, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(normalizedGitDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindGitRoot(string rootPath, CancellationToken token)
    {
        var result = RunGit(rootPath, null, token, ["rev-parse", "--show-toplevel"]);
        if (result.ExitCode != 0)
            return FindGitRootByDirectory(rootPath);

        var path = result.Output.Trim();
        return string.IsNullOrWhiteSpace(path)
            ? FindGitRootByDirectory(rootPath)
            : Path.GetFullPath(path);
    }

    private static string? FindGitRootByDirectory(string rootPath)
    {
        var directory = new DirectoryInfo(rootPath);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    private static Dictionary<string, GitChangeKind> LoadFileStatuses(string gitRoot, CancellationToken token)
    {
        var statuses = new Dictionary<string, GitChangeKind>(StringComparer.OrdinalIgnoreCase);

        var result = RunGit(gitRoot, null, token,
            ["status", "--porcelain", "-z", "--untracked-files=all"]);
        if (result.ExitCode != 0)
            return statuses;

        var entries = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4)
                continue;

            var status = entry[..2];
            var relativePath = entry[3..];

            // リネーム/コピーは -z 形式では「新パス\0旧パス」の順で出力される。
            // 新パスは entry 側に含まれているので、続く旧パスのエントリを読み飛ばすだけにする
            // （旧パスはディスク上に存在せず、新パス＝追加扱いのファイルが表示対象）。
            if ((status[0] is 'R' or 'C' || status[1] is 'R' or 'C') && i + 1 < entries.Length)
                i++;

            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            statuses[Path.GetFullPath(Path.Combine(gitRoot, relativePath))] = MapStatus(status);
        }

        return statuses;
    }

    // git status --porcelain の XY 2 文字コードを表示用の種別へ落とす。
    // 競合（U を含む、AA/DD）を最優先で判定し、以降は X か Y のどちらかに現れた文字で分類する。
    private static GitChangeKind MapStatus(string xy)
    {
        if (xy == "??")
            return GitChangeKind.Untracked;

        var x = xy[0];
        var y = xy[1];

        if (x is 'U' || y is 'U' || xy is "AA" or "DD")
            return GitChangeKind.Conflicted;
        if (x is 'R' || y is 'R')
            return GitChangeKind.Renamed;
        if (x is 'A' || y is 'A')
            return GitChangeKind.Added;
        if (x is 'D' || y is 'D')
            return GitChangeKind.Deleted;
        return GitChangeKind.Modified;
    }

    private static HashSet<string> LoadChangedDirectories(HashSet<string> changedFiles, string visibleRoot)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedRoot = Path.GetFullPath(visibleRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var file in changedFiles)
        {
            var directory = Path.GetDirectoryName(file);
            while (!string.IsNullOrEmpty(directory))
            {
                var normalizedDirectory = Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (normalizedDirectory.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    break;

                if (!normalizedDirectory.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    break;

                directories.Add(normalizedDirectory);
                directory = Path.GetDirectoryName(normalizedDirectory);
            }
        }

        return directories;
    }

    private static string ToGitRelativePath(string gitRoot, string fullPath)
    {
        var relativePath = Path.GetRelativePath(gitRoot, fullPath);
        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static GitCommandResult RunGit(string workingDirectory, params string[] args)
        => RunGit(workingDirectory, standardInput: null, args);

    // 注意: stdin 版の args は params にしない。params にすると
    // RunGit(wd, "status", "--porcelain", ...) のような呼び出しで "status" が
    // standardInput に解決され（git のサブコマンドが渡らず exit 129 になる）。
    // 明示的な string[] にすることで、可変長引数の呼び出しは必ず上の params 版へ振り分けられる。
    private static GitCommandResult RunGit(string workingDirectory, string? standardInput, string[] args)
        => RunGit(workingDirectory, standardInput, CancellationToken.None, args);

    private static GitCommandResult RunGit(
        string workingDirectory, string? standardInput, CancellationToken token, string[] args)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (var arg in args)
                process.StartInfo.ArgumentList.Add(arg);

            process.Start();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            var waitToken = linkedCts.Token;
            using var registration = waitToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { /* 終了との競合 */ }
            });

            if (standardInput is not null)
            {
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(waitToken);

            try
            {
                process.WaitForExitAsync(waitToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return new GitCommandResult(-1, "");
            }
            catch (OperationCanceledException) { throw; }

            return new GitCommandResult(process.ExitCode, outputTask.GetAwaiter().GetResult());
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new GitCommandResult(-1, "");
        }
    }

    private readonly record struct GitCommandResult(int ExitCode, string Output);
}
