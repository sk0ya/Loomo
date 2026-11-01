using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using sk0ya.Loomo.Core.Files;
using sk0ya.Loomo.Core.Processes;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>リポジトリが途中で止まっている操作（§40.6）。</summary>
public enum GitRepositoryOperation
{
    None,
    Rebase,
    Merge,
    CherryPick,
    Revert,
    Bisect,
}

/// <summary>
/// リポジトリ<b>1つ</b>の「いまの様子」（§40.6）：ブランチ・upstream・ahead/behind・変更の数・止まっている操作と、
/// ファイルごとの変更種別。<c>git status --porcelain -z -b --untracked-files=all</c> の<b>1回</b>から作る。
///
/// <para>もとは <see cref="GitTreeState"/>（エクスプローラーの表示ルート1つぶん）の中身だったものを、
/// リポジトリ単位に切り出した。表示ルートとリポジトリはずれる（親フォルダーの下に複数のリポジトリ・
/// リポジトリの中の一部だけを表示）ので、状態はリポジトリで持ち、表示ルートの側で束ねる
/// （<see cref="GitTreeState.Compose"/>）。同じリポジトリを複数の面（ツリー・ファイル一覧）が見ても
/// git は1回で済む（<see cref="GitWorkspaceStatus"/> が共有する）。</para>
///
/// <para><c>--no-optional-locks</c> は必須：付けないと status が index の stat 情報を書き戻し、
/// <see cref="GitWorkspaceStatus"/> の <c>.git</c> 監視が発火して読み直し→書き戻し…のループになる
/// （メモリ ui-jank-git-watcher と同じ型の事故）。</para>
/// </summary>
public sealed class GitRepositoryFileState
{
    private readonly object _ignoredGate = new();
    private readonly HashSet<string> _ignoredPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _gitDirectory;

    /// <summary>作業ツリーの根（正規化済み）。</summary>
    public string Root { get; }

    /// <summary>status が読めたか。false は「今は分からない」（git が無い・一時的な失敗）。</summary>
    public bool Loaded { get; }

    /// <summary>ブランチ名。detached なら <c>HEAD</c>。読めなければ空。</summary>
    public string Branch { get; }
    public bool IsDetached { get; }
    public string? Upstream { get; }
    /// <summary>upstream が消えている（<c>[gone]</c>）。</summary>
    public bool UpstreamGone { get; }
    public int Ahead { get; }
    public int Behind { get; }

    public int StagedCount { get; }
    public int UnstagedCount { get; }
    public int UntrackedCount { get; }
    public int ConflictCount { get; }
    public GitRepositoryOperation Operation { get; }

    /// <summary>変更のあるファイルの数（ステージ済み・未ステージ・未追跡・競合のどれか）。</summary>
    public int ChangedCount => FileStatuses.Count;

    /// <summary>変更ファイルのフルパス → 変更種別。</summary>
    public IReadOnlyDictionary<string, GitChangeKind> FileStatuses { get; }

    /// <summary>配下に変更を含むフォルダー（作業ツリーの根<b>を含む</b>——根の行にも「配下に変更あり」を出せるように）。</summary>
    public IReadOnlySet<string> ChangedDirectories { get; }

    private GitRepositoryFileState(
        string root,
        string gitDirectory,
        bool loaded,
        IReadOnlyDictionary<string, GitChangeKind> fileStatuses,
        BranchHeader header,
        (int Staged, int Unstaged, int Untracked, int Conflicts) counts,
        GitRepositoryOperation operation)
    {
        Root = root;
        _gitDirectory = gitDirectory;
        Loaded = loaded;
        FileStatuses = fileStatuses;
        Branch = header.Branch;
        IsDetached = header.Detached;
        Upstream = header.Upstream;
        UpstreamGone = header.Gone;
        Ahead = header.Ahead;
        Behind = header.Behind;
        StagedCount = counts.Staged;
        UnstagedCount = counts.Unstaged;
        UntrackedCount = counts.Untracked;
        ConflictCount = counts.Conflicts;
        Operation = operation;
        ChangedDirectories = CollectChangedDirectories(fileStatuses.Keys, root);
    }

    /// <summary>リポジトリの状態を読む（git を1回起こす・同期）。UI スレッドから呼ばない。</summary>
    public static GitRepositoryFileState Load(string root, CancellationToken token)
    {
        var normalized = WorkspacePaths.Normalize(root);
        var gitDirectory = GitRepositoryCatalog.ResolveGitDirectory(normalized) ?? Path.Combine(normalized, ".git");
        var operation = DetectOperation(gitDirectory);

        token.ThrowIfCancellationRequested();
        var result = GitProcess.Run(normalized, null, token,
            ["--no-optional-locks", "status", "--porcelain", "-z", "-b", "--untracked-files=all"]);
        if (result.ExitCode != 0)
            return new GitRepositoryFileState(normalized, gitDirectory, loaded: false,
                new Dictionary<string, GitChangeKind>(), BranchHeader.Empty, default, operation);

        var parsed = ParseStatus(normalized, result.Output);
        return new GitRepositoryFileState(normalized, gitDirectory, loaded: true,
            parsed.Statuses, parsed.Header, parsed.Counts, operation);
    }

    /// <summary>ファイル／フォルダーに出す状態（無視対象の判定は <see cref="GetIgnoredPaths"/> で読んだぶんだけ）。</summary>
    public GitChangeKind GetStatus(string fullPath, bool isDirectory)
    {
        var path = WorkspacePaths.Normalize(fullPath);
        if (isDirectory)
        {
            if (ChangedDirectories.Contains(path))
                return GitChangeKind.DirectoryChanged;
        }
        else if (FileStatuses.TryGetValue(path, out var status))
        {
            return status;
        }
        lock (_ignoredGate)
            return _ignoredPaths.Contains(path) ? GitChangeKind.Ignored : GitChangeKind.None;
    }

    /// <summary>読み込み済みの無視対象か（check-ignore は起こさない）。</summary>
    public bool IsKnownIgnored(string fullPath)
    {
        lock (_ignoredGate)
            return _ignoredPaths.Contains(WorkspacePaths.Normalize(fullPath));
    }

    /// <summary>
    /// 与えたパスのうち無視対象のもの（<c>check-ignore</c> を1回だけ起こす・同期）。結果は覚えて
    /// <see cref="GetStatus"/> に反映する。git ディレクトリの中は常に無視対象。
    /// </summary>
    public HashSet<string> GetIgnoredPaths(IEnumerable<string> fullPaths)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = fullPaths.Select(WorkspacePaths.Normalize).Where(p => p.Length > 0).ToArray();
        var dotGit = Path.Combine(Root, ".git");
        foreach (var path in normalized)
        {
            if (WorkspacePaths.IsWithin(dotGit, path) || WorkspacePaths.IsWithin(_gitDirectory, path))
                ignored.Add(path);
        }

        var candidates = normalized
            .Where(path => !ignored.Contains(path) && WorkspacePaths.IsWithin(Root, path)
                           && !string.Equals(path, Root, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(Root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(relative => relative.Length > 0 && relative != ".")
            .ToArray();

        if (candidates.Length > 0)
        {
            var input = string.Join('\0', candidates) + '\0';
            var result = GitProcess.Run(Root, input, CancellationToken.None, ["check-ignore", "-z", "--stdin"]);
            if (result.ExitCode is 0 or 1)
            {
                foreach (var relative in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                    ignored.Add(WorkspacePaths.Normalize(Path.Combine(Root, relative)));
            }
        }

        lock (_ignoredGate)
            _ignoredPaths.UnionWith(ignored);
        return ignored;
    }

    // ===== 解析 =====

    internal readonly record struct BranchHeader(
        string Branch, bool Detached, string? Upstream, bool Gone, int Ahead, int Behind)
    {
        public static BranchHeader Empty { get; } = new("", false, null, false, 0, 0);
    }

    internal static (Dictionary<string, GitChangeKind> Statuses, BranchHeader Header,
        (int Staged, int Unstaged, int Untracked, int Conflicts) Counts) ParseStatus(string root, string output)
    {
        var statuses = new Dictionary<string, GitChangeKind>(StringComparer.OrdinalIgnoreCase);
        var header = BranchHeader.Empty;
        int staged = 0, unstaged = 0, untracked = 0, conflicts = 0;
        var entries = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.StartsWith("## ", StringComparison.Ordinal))
            {
                header = ParseBranchHeader(entry[3..]);
                continue;
            }
            if (entry.Length < 4)
                continue;

            var xy = entry[..2];
            var relativePath = entry[3..];

            // リネーム/コピーは -z 形式では「新パス\0旧パス」の順。旧パスは読み飛ばす
            // （旧パスはディスク上に無く、表示するのは新パス）。
            if ((xy[0] is 'R' or 'C' || xy[1] is 'R' or 'C') && i + 1 < entries.Length)
                i++;
            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            var kind = MapStatus(xy);
            switch (kind)
            {
                case GitChangeKind.Untracked: untracked++; break;
                case GitChangeKind.Conflicted: conflicts++; break;
                default:
                    if (!IsClean(xy[0])) staged++;
                    if (!IsClean(xy[1])) unstaged++;
                    break;
            }
            statuses[WorkspacePaths.Normalize(Path.Combine(root, relativePath))] = kind;
        }
        return (statuses, header, (staged, unstaged, untracked, conflicts));
    }

    /// <summary><c>## </c> の後ろ。<c>main...origin/main [ahead 1, behind 2]</c>・<c>main</c>・
    /// <c>No commits yet on main</c>・<c>HEAD (no branch)</c>・<c>main...origin/main [gone]</c>。</summary>
    internal static BranchHeader ParseBranchHeader(string text)
    {
        foreach (var prefix in new[] { "No commits yet on ", "Initial commit on " })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                return BranchHeader.Empty with { Branch = text[prefix.Length..].Trim() };
        }
        if (text.StartsWith("HEAD (no branch)", StringComparison.Ordinal))
            return BranchHeader.Empty with { Branch = "HEAD", Detached = true };

        var tracking = "";
        var bracket = text.IndexOf(" [", StringComparison.Ordinal);
        if (bracket >= 0 && text.EndsWith(']'))
        {
            tracking = text[(bracket + 2)..^1];
            text = text[..bracket];
        }

        string branch = text, upstream = "";
        var dots = text.IndexOf("...", StringComparison.Ordinal);
        if (dots >= 0)
        {
            branch = text[..dots];
            upstream = text[(dots + 3)..];
        }

        int ahead = 0, behind = 0;
        var gone = false;
        foreach (var part in tracking.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part == "gone") gone = true;
            else if (part.StartsWith("ahead ", StringComparison.Ordinal)) int.TryParse(part[6..], out ahead);
            else if (part.StartsWith("behind ", StringComparison.Ordinal)) int.TryParse(part[7..], out behind);
        }
        return new BranchHeader(branch.Trim(), false, upstream.Length == 0 ? null : upstream.Trim(), gone, ahead, behind);
    }

    // git status --porcelain の XY 2 文字コードを表示用の種別へ落とす。
    // 競合（U を含む、AA/DD）を最優先で判定する。作業ツリー側（Y）に差分が
    // 残っている場合はその状態を表示し、作業ツリーがクリーンで index 側だけに
    // 差分がある場合だけ Staged とする。これにより AM/MM の編集を Added や
    // Staged に潰さず、現在の作業ツリー状態を優先できる。
    internal static GitChangeKind MapStatus(string xy)
    {
        if (xy == "??")
            return GitChangeKind.Untracked;

        var x = xy[0];
        var y = xy[1];

        if (x is 'U' || y is 'U' || xy is "AA" or "DD")
            return GitChangeKind.Conflicted;
        if (!IsClean(y))
        {
            if (y is 'R')
                return GitChangeKind.Renamed;
            if (y is 'A')
                return GitChangeKind.Added;
            if (y is 'D')
                return GitChangeKind.Deleted;
            return GitChangeKind.Modified;
        }

        // X のみが立っている項目は index にだけ差分がある。
        if (!IsClean(x))
            return GitChangeKind.Staged;
        if (x is 'R' || y is 'R')
            return GitChangeKind.Renamed;
        if (x is 'A' || y is 'A')
            return GitChangeKind.Added;
        if (x is 'D' || y is 'D')
            return GitChangeKind.Deleted;
        return GitChangeKind.Modified;
    }

    // porcelain=v1 は未設定側を '.' ではなく空白で出す（-z でも同じ）。
    private static bool IsClean(char status) => status is '.' or ' ';

    private static HashSet<string> CollectChangedDirectories(IEnumerable<string> changedFiles, string root)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in changedFiles)
        {
            // 未追跡のフォルダー（入れ子のリポジトリなど）は "api/" のように区切りで終わる。
            var directory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(file));
            while (!string.IsNullOrEmpty(directory) && WorkspacePaths.IsWithin(root, directory))
            {
                if (!directories.Add(directory))
                    break; // ここから上は既に入っている
                if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
                    break;
                directory = Path.GetDirectoryName(directory);
            }
        }
        return directories;
    }

    internal static GitRepositoryOperation DetectOperation(string gitDirectory)
    {
        try
        {
            if (Directory.Exists(Path.Combine(gitDirectory, "rebase-merge"))
                || Directory.Exists(Path.Combine(gitDirectory, "rebase-apply")))
                return GitRepositoryOperation.Rebase;
            if (File.Exists(Path.Combine(gitDirectory, "MERGE_HEAD"))) return GitRepositoryOperation.Merge;
            if (File.Exists(Path.Combine(gitDirectory, "CHERRY_PICK_HEAD"))) return GitRepositoryOperation.CherryPick;
            if (File.Exists(Path.Combine(gitDirectory, "REVERT_HEAD"))) return GitRepositoryOperation.Revert;
            if (File.Exists(Path.Combine(gitDirectory, "BISECT_LOG"))) return GitRepositoryOperation.Bisect;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return GitRepositoryOperation.None;
    }
}

/// <summary>
/// ツリー・状態の読み込み用に git を同期で起こす（5 秒で刈る）。出力はプールではなく専用スレッドで
/// 読む（<see cref="ChildProcessIo"/>）。もとは <see cref="GitTreeState"/> の中にあったもの。
/// </summary>
internal static class GitProcess
{
    internal readonly record struct Result(int ExitCode, string Output);

    // 注意: stdin 版の args は params にしない。params にすると
    // Run(wd, "status", "--porcelain", ...) のような呼び出しで "status" が
    // standardInput に解決され（git のサブコマンドが渡らず exit 129 になる）。
    public static Result Run(string workingDirectory, string? standardInput, CancellationToken token, string[] args)
    {
        try
        {
            // 打ち切りは「読み取りに渡したトークン」任せにしない——読み取りが専用スレッドの
            // ブロッキング読みになった以上、打ち切りはここで明示的に見る（起こす前と、待ったあと）。
            token.ThrowIfCancellationRequested();
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

            // stderr も読み捨てる——誰も読まないとパイプが詰まって git 側が止まる。
            // **読み手を立ててから stdin を書く**——check-ignore は stdin を食いながら一致した
            // パスを吐くので、先に何千行も流し込むと git の stdout が詰まり、こちらは Write で
            // 止まる（無視対象が空になり、無視ファイルが普通のファイルとして並ぶ）。
            var outputTask = ChildProcessIo.ReadToEndAsync(process.StandardOutput, "gitツリー:stdout");
            _ = ChildProcessIo.ReadToEndAsync(process.StandardError, "gitツリー:stderr");

            if (standardInput is not null)
            {
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            }

            try
            {
                process.WaitForExitAsync(waitToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return new Result(-1, "");
            }

            token.ThrowIfCancellationRequested();
            return new Result(process.ExitCode, outputTask.GetAwaiter().GetResult());
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new Result(-1, "");
        }
    }
}
