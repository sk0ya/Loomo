using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.Services;

/// <summary>リポジトリの種類（§40.3）。</summary>
public enum GitRepositoryKind
{
    /// <summary><c>.git</c> がディレクトリ（ふつうのクローン・<c>git init</c>）。</summary>
    Main,
    /// <summary><c>git worktree add</c> で作ったワークツリー（<c>.git</c> がファイルで、本体の <c>worktrees/</c> を指す）。</summary>
    LinkedWorktree,
    /// <summary>サブモジュール（<c>.git</c> がファイルで、親の <c>modules/</c> を指す）。</summary>
    Submodule,
}

/// <summary>
/// ワークスペースの中で見つけたリポジトリ1つ（§40.3）。<see cref="Root"/>（作業ツリーの根の正規化した
/// 絶対パス）が識別子。
/// </summary>
/// <param name="Root">作業ツリーの根。<see cref="WorkspacePaths.Normalize"/> 済み。</param>
/// <param name="Kind">種類。</param>
/// <param name="WorkspaceFolder">このリポジトリを見つけたワークスペースフォルダー。根がフォルダーより上
/// （フォルダーがリポジトリの一部）のときもそのフォルダー。</param>
/// <param name="CommonGitDir">本体の git ディレクトリ。同じ本体のワークツリー同士はこれが一致する。
/// 読めなかったら null。</param>
public sealed record GitRepositoryInfo(
    string Root,
    GitRepositoryKind Kind,
    string WorkspaceFolder,
    string? CommonGitDir)
{
    /// <summary>見出し名（作業ツリーの根のフォルダー名）。</summary>
    public string Name => WorkspacePaths.FolderName(Root);
}

/// <summary>
/// ワークスペースの全リポジトリを知る<b>唯一の場所</b>（§40.4）。「このパスはどのリポジトリか」は
/// <see cref="RepositoryFor"/> だけが答える——<see cref="WorkspacePaths"/> と同じく、問いの方を1か所に集める。
///
/// <para><b>見つけ方は <c>.git</c> の有無だけ</b>で、git は起こさない。<c>.git</c>（ディレクトリでもファイルでも）を
/// 持つディレクトリがそのまま作業ツリーの根なので、<c>rev-parse --show-toplevel</c> と同じ答えになる
/// （<c>GIT_DIR</c>／<c>core.worktree</c> で根をよそへ向けた特殊な構成は扱わない）。フォルダーを開くたびに
/// 候補の数だけ git を起こさずに済む。</para>
///
/// <para>一覧は遅延で作り、<see cref="IWorkspaceService.FoldersChanged"/> と <see cref="Refresh"/> で捨てる。
/// 探索はディレクトリの列挙なので UI スレッドから呼ばない方がよい（最初の問い合わせで走る）。</para>
/// </summary>
public sealed class GitRepositoryCatalog
{
    /// <summary>ワークスペースフォルダーの下を探す深さ。<c>C:\work\{api,web}</c>（1 段）と
    /// <c>C:\src\org\{a,b}</c>（2 段）を拾い、それより奥（<c>node_modules</c> の奥など）へは潜らない。</summary>
    public const int SearchDepth = 2;

    /// <summary>潜らないディレクトリ名。<c>DebouncedFolderWatcher</c> の除外と揃える
    /// （ビルド出力・依存の置き場に入れ子のリポジトリを探しに行かない）。</summary>
    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea",
    };

    private readonly Func<IReadOnlyList<string>> _folders;
    private readonly object _gate = new();
    private IReadOnlyList<GitRepositoryInfo>? _repositories;
    private int _generation;

    public GitRepositoryCatalog(IWorkspaceService workspace)
    {
        _folders = () => workspace.Folders;
        workspace.FoldersChanged += (_, _) => Refresh();
    }

    /// <summary>テスト用：フォルダーの集合を直接与える。</summary>
    internal GitRepositoryCatalog(Func<IReadOnlyList<string>> folders) => _folders = folders;

    /// <summary>一覧が作り直されたとき（フォルダーの追加・削除、<see cref="Refresh"/>）。</summary>
    public event EventHandler? Changed;

    /// <summary>全リポジトリ。ワークスペースフォルダーの順、その中は浅い順・名前順。</summary>
    public IReadOnlyList<GitRepositoryInfo> Repositories
    {
        get
        {
            int generation;
            lock (_gate)
            {
                if (_repositories is not null) return _repositories;
                generation = _generation;
            }
            // 探索はロックの外で（ディレクトリ列挙は遅い場所では遅い）。途中で Refresh が来たら、
            // 古い世代の答えは置かずにそのまま返す（次の問い合わせで作り直す）。
            var found = Discover(_folders());
            lock (_gate)
            {
                if (_generation == generation)
                    _repositories = found;
            }
            return found;
        }
    }

    /// <summary>作り済みの一覧（作っていなければ null・探索は走らせない）。UI スレッドで「分かっている範囲で」答えたい
    /// 場所（パンくず・場所パネル）のため。一覧はツリーの読み込みなど背景の問い合わせで作られる。</summary>
    public IReadOnlyList<GitRepositoryInfo>? KnownRepositories
    {
        get { lock (_gate) return _repositories; }
    }

    /// <summary>作り済みの一覧だけで答える <see cref="RepositoryFor(string?)"/>（探索は走らせない）。</summary>
    public GitRepositoryInfo? KnownRepositoryFor(string? path)
        => KnownRepositories is { } known ? RepositoryFor(known, path) : null;

    /// <summary>一覧を捨てる。<c>.git</c> が現れた・消えた（クローン・<c>git init</c>・削除）ことを
    /// 呼び出し側が知ったときにも呼ぶ。</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            _repositories = null;
            _generation++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary><paramref name="path"/> が属するリポジトリ。含むリポジトリのうち<b>いちばん深いもの</b>
    /// （入れ子・サブモジュールでは中の方）。どれにも属さなければ null。</summary>
    public GitRepositoryInfo? RepositoryFor(string? path) => RepositoryFor(Repositories, path);

    /// <summary><paramref name="folder"/>（ワークスペースフォルダー）で見つけたリポジトリ。</summary>
    public IReadOnlyList<GitRepositoryInfo> RepositoriesIn(string folder)
    {
        var normalized = WorkspacePaths.Normalize(folder);
        return Repositories
            .Where(r => string.Equals(r.WorkspaceFolder, normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    internal static GitRepositoryInfo? RepositoryFor(IReadOnlyList<GitRepositoryInfo> repositories, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        GitRepositoryInfo? best = null;
        foreach (var repository in repositories)
        {
            if (!WorkspacePaths.IsWithin(repository.Root, path)) continue;
            if (best is null || repository.Root.Length > best.Root.Length)
                best = repository;
        }
        return best;
    }

    // ===== 探索 =====

    /// <summary>
    /// フォルダーの集合からリポジトリを探す（純粋に .git の有無だけ・<see cref="Repositories"/> の中身）。一覧を持たない
    /// 場所（エクスプローラーの表示ルートをその場で読む等）から直接呼ぶ。
    /// </summary>
    public static IReadOnlyList<GitRepositoryInfo> Discover(IReadOnlyList<string> folders)
    {
        var result = new List<GitRepositoryInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawFolder in folders)
        {
            var folder = WorkspacePaths.Normalize(rawFolder);
            if (folder.Length == 0 || !Directory.Exists(folder)) continue;

            var found = new List<GitRepositoryInfo>();
            // ① フォルダー自身か先祖が根。フォルダーはそのリポジトリの一部なので、下は探さない
            //   （リポジトリの中で入れ子を探すのはサブモジュールだけ・③）。
            if (FindEnclosingRoot(folder) is { } enclosing)
                found.Add(Describe(enclosing, folder));
            else
                SearchBelow(folder, folder, depth: 1, found);

            // ③ 見つけたリポジトリのサブモジュール（.gitmodules に書かれ、チェックアウト済みのもの）。
            foreach (var repository in found.ToArray())
                AddSubmodules(repository, folder, found);

            foreach (var repository in found
                         .OrderBy(r => r.Root.Count(c => c == Path.DirectorySeparatorChar))
                         .ThenBy(r => r.Root, StringComparer.OrdinalIgnoreCase))
            {
                if (seen.Add(repository.Root))
                    result.Add(repository);
            }
        }
        return result;
    }

    private static string? FindEnclosingRoot(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
        {
            if (HasGitEntry(dir.FullName))
                return WorkspacePaths.Normalize(dir.FullName);
        }
        return null;
    }

    // ② フォルダーの下、SearchDepth 段まで。見つけたリポジトリの中へは潜らない。
    private static void SearchBelow(string workspaceFolder, string directory, int depth, List<GitRepositoryInfo> found)
    {
        if (depth > SearchDepth) return;
        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return; }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (SkippedDirectoryNames.Contains(name)) continue;
            if (IsReparsePoint(child)) continue; // ジャンクションの循環・別ドライブへの飛び出しを避ける
            if (HasGitEntry(child))
            {
                found.Add(Describe(WorkspacePaths.Normalize(child), workspaceFolder));
                continue;
            }
            SearchBelow(workspaceFolder, child, depth + 1, found);
        }
    }

    private static void AddSubmodules(GitRepositoryInfo repository, string workspaceFolder, List<GitRepositoryInfo> found)
    {
        foreach (var relative in ReadSubmodulePaths(Path.Combine(repository.Root, ".gitmodules")))
        {
            string full;
            try { full = WorkspacePaths.Normalize(Path.Combine(repository.Root, relative)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { continue; }
            // ワークスペースの中にある部分だけ扱う（§40.4）。根がフォルダーより上のリポジトリでは、
            // フォルダーの外のサブモジュールはツリーにも出ないので数えない。
            if (!WorkspacePaths.IsWithin(workspaceFolder, full)) continue;
            if (!HasGitEntry(full)) continue; // 未チェックアウト
            if (found.Any(r => string.Equals(r.Root, full, StringComparison.OrdinalIgnoreCase))) continue;
            var submodule = Describe(full, workspaceFolder) with { Kind = GitRepositoryKind.Submodule };
            found.Add(submodule);
            AddSubmodules(submodule, workspaceFolder, found);
        }
    }

    /// <summary><c>.gitmodules</c> の <c>path = …</c> を読む。git config の書式のうち、ここで要るのは
    /// <c>path</c> の値だけ（引用符で囲まれていれば外す）。</summary>
    internal static IReadOnlyList<string> ReadSubmodulePaths(string gitmodulesPath)
    {
        string[] lines;
        try
        {
            if (!File.Exists(gitmodulesPath)) return Array.Empty<string>();
            lines = File.ReadAllLines(gitmodulesPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Array.Empty<string>(); }

        var paths = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';' or '[') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (!line[..eq].Trim().Equals("path", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            if (value.Length > 0)
                paths.Add(value.Replace('/', Path.DirectorySeparatorChar));
        }
        return paths;
    }

    private static GitRepositoryInfo Describe(string root, string workspaceFolder)
    {
        var dotGit = Path.Combine(root, ".git");
        if (Directory.Exists(dotGit))
            return new GitRepositoryInfo(root, GitRepositoryKind.Main, workspaceFolder, WorkspacePaths.Normalize(dotGit));

        // .git がファイル：「gitdir: <パス>」。本体の worktrees/<名前> ならワークツリー、
        // それ以外（modules/<名前>）はサブモジュール。
        var gitDir = ReadGitDirPointer(dotGit, root);
        if (gitDir is null)
            return new GitRepositoryInfo(root, GitRepositoryKind.Main, workspaceFolder, null);
        var parent = Path.GetDirectoryName(gitDir);
        if (parent is not null && Path.GetFileName(parent).Equals("worktrees", StringComparison.OrdinalIgnoreCase))
        {
            var common = Path.GetDirectoryName(parent);
            return new GitRepositoryInfo(root, GitRepositoryKind.LinkedWorktree, workspaceFolder,
                common is null ? null : WorkspacePaths.Normalize(common));
        }
        return new GitRepositoryInfo(root, GitRepositoryKind.Submodule, workspaceFolder, gitDir);
    }

    /// <summary>作業ツリーの根 <paramref name="root"/> の git ディレクトリ（<c>.git</c> がディレクトリならそれ、
    /// ファイルなら <c>gitdir:</c> の指す先）。どちらでもなければ null。HEAD・index・操作中の印はここにある。</summary>
    public static string? ResolveGitDirectory(string root)
    {
        var dotGit = Path.Combine(root, ".git");
        if (Directory.Exists(dotGit)) return WorkspacePaths.Normalize(dotGit);
        return File.Exists(dotGit) ? ReadGitDirPointer(dotGit, root) : null;
    }

    internal static string? ReadGitDirPointer(string dotGitFile, string root)
    {
        try
        {
            var text = File.ReadAllText(dotGitFile).Trim();
            const string prefix = "gitdir:";
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var value = text[prefix.Length..].Trim();
            if (value.Length == 0) return null;
            var full = Path.IsPathRooted(value) ? value : Path.Combine(root, value);
            return WorkspacePaths.Normalize(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool HasGitEntry(string directory)
    {
        var dotGit = Path.Combine(directory, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    private static bool IsReparsePoint(string directory)
    {
        try { return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }
}
