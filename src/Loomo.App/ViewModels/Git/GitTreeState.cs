using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using sk0ya.Loomo.Core.Files;
using sk0ya.Loomo.Services;

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
    Staged,
    Ignored,
    DirectoryChanged,
}

/// <summary>
/// エクスプローラーの<b>表示ルート1つぶん</b>の Git の状態。中身はその表示ルートにかかるリポジトリの状態
/// （<see cref="GitRepositoryFileState"/>）の束で、ここは「表示ルートから見た」集約だけを持つ（§40.6）。
///
/// <para>表示ルートとリポジトリはずれる：表示ルートがリポジトリの一部（先祖が根）のことも、表示ルートの下に
/// 複数のリポジトリがある（<c>C:\work\{api,web}</c>）こともある。以前はここで <c>rev-parse --show-toplevel</c> を
/// 起こしてリポジトリを1つに決めていたので、後者は丸ごと「Git 管理外」になっていた。</para>
///
/// <para>ファイルの状態・無視対象は<b>いちばん深い</b>リポジトリに問う。配下に変更を含むフォルダーの集約は
/// 表示ルートまで（入れ子のリポジトリの変更も外側のフォルダーへ伝わる——境目で止めるのは §40.8.1・P4）。</para>
/// </summary>
internal sealed class GitTreeState
{
    private readonly string _rootPath;
    /// <summary>この表示ルートにかかるリポジトリ（深い順）。</summary>
    private readonly IReadOnlyList<GitRepositoryFileState> _repositories;

    public static GitTreeState Empty { get; } = new("", Array.Empty<GitRepositoryFileState>());

    public bool IsGitRepository => _repositories.Count > 0;

    /// <summary>この表示ルートにかかるリポジトリの状態（深い順）。</summary>
    public IReadOnlyList<GitRepositoryFileState> Repositories => _repositories;

    /// <summary>変更ファイルのフルパス → 変更種別（全リポジトリぶん）。</summary>
    public Dictionary<string, GitChangeKind> FileStatuses { get; }
    public HashSet<string> ChangedFiles { get; }
    /// <summary>配下に変更を含むフォルダー（表示ルートより下だけ・表示ルート自身は含まない）。</summary>
    public HashSet<string> ChangedDirectories { get; }

    private GitTreeState(string rootPath, IReadOnlyList<GitRepositoryFileState> repositories)
    {
        _rootPath = rootPath;
        _repositories = repositories
            .OrderByDescending(r => r.Root.Length)
            .ToArray();
        FileStatuses = new Dictionary<string, GitChangeKind>(StringComparer.OrdinalIgnoreCase);
        // 浅い順に入れて深い方で上書きする（同じパスを両方が持つことは普通は無いが、入れ子の境目で
        // 外側が "api/" を未追跡として持つ等に備えて、所属＝深い方を正とする）。
        foreach (var repository in _repositories.Reverse())
            foreach (var pair in repository.FileStatuses)
                FileStatuses[pair.Key] = pair.Value;
        ChangedFiles = new HashSet<string>(FileStatuses.Keys, StringComparer.OrdinalIgnoreCase);
        ChangedDirectories = rootPath.Length == 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : CollectChangedDirectories(ChangedFiles, rootPath);
    }

    /// <summary>表示ルート <paramref name="rootPath"/> の状態を、読み込み済みのリポジトリの状態から組む。</summary>
    public static GitTreeState Compose(string rootPath, IEnumerable<GitRepositoryFileState> repositories)
        => new(WorkspacePaths.Normalize(rootPath), repositories.ToArray());

    /// <summary>
    /// 表示ルート <paramref name="rootPath"/> にかかるリポジトリを探して読む（共有のキャッシュを使わない単独の読み込み）。
    /// アプリは <see cref="GitWorkspaceStatus.TreeStateFor"/> を通す。
    /// </summary>
    public static GitTreeState Load(string rootPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = WorkspacePaths.Normalize(rootPath);
        var repositories = new List<GitRepositoryFileState>();
        foreach (var repository in GitRepositoryCatalog.Discover(new[] { root }))
        {
            token.ThrowIfCancellationRequested();
            repositories.Add(GitRepositoryFileState.Load(repository.Root, token));
        }
        return new GitTreeState(root, repositories);
    }

    /// <summary>指定ファイルの変更種別（変更なしは None）。</summary>
    public GitChangeKind GetFileStatus(string fullPath)
        => FileStatuses.TryGetValue(WorkspacePaths.Normalize(fullPath), out var kind) ? kind : GitChangeKind.None;

    /// <summary>与えたパスのうち無視対象のもの。所属リポジトリごとに <c>check-ignore</c> を1回ずつ起こす（同期）。</summary>
    public HashSet<string> GetIgnoredPaths(IEnumerable<string> fullPaths)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var grouped = new Dictionary<GitRepositoryFileState, List<string>>();
        foreach (var raw in fullPaths)
        {
            var path = WorkspacePaths.Normalize(raw);
            if (path.Length == 0) continue;
            if (RepositoryFor(path) is not { } repository)
            {
                // どのリポジトリにも属さない：表示ルートの .git の中だけは無視対象として扱う（従来どおり）。
                if (_rootPath.Length > 0 && WorkspacePaths.IsWithin(Path.Combine(_rootPath, ".git"), path))
                    ignored.Add(path);
                continue;
            }
            if (!grouped.TryGetValue(repository, out var list))
                grouped[repository] = list = new List<string>();
            list.Add(path);
        }
        foreach (var pair in grouped)
            ignored.UnionWith(pair.Key.GetIgnoredPaths(pair.Value));
        return ignored;
    }

    /// <summary>ファイル／フォルダーに表示する状態。フォルダーは配下の変更を集約し、無視対象は
    /// 読み込み済み（<see cref="GetIgnoredPaths"/>）のものだけ。</summary>
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
        return RepositoryFor(path)?.IsKnownIgnored(path) == true ? GitChangeKind.Ignored : GitChangeKind.None;
    }

    private GitRepositoryFileState? RepositoryFor(string normalizedPath)
    {
        foreach (var repository in _repositories) // 深い順
        {
            if (WorkspacePaths.IsWithin(repository.Root, normalizedPath))
                return repository;
        }
        return null;
    }

    private static HashSet<string> CollectChangedDirectories(HashSet<string> changedFiles, string visibleRoot)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in changedFiles)
        {
            var directory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(file));
            while (!string.IsNullOrEmpty(directory))
            {
                if (directory.Equals(visibleRoot, StringComparison.OrdinalIgnoreCase)
                    || !WorkspacePaths.IsWithin(visibleRoot, directory))
                    break;
                if (!directories.Add(directory))
                    break;
                directory = Path.GetDirectoryName(directory);
            }
        }
        return directories;
    }
}
