using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// FolderTree の検索フィルタの実体。名前が一致するノード（とそこへ至るフォルダ）だけを残した
/// ツリーを <see cref="Entry"/> の形で組み立てる。UI 型に依存しないので単体テストできる。
/// 表示条件（ignore 非表示・変更のみ）と git 問い合わせはデリゲートで受け取る。
/// </summary>
public static class FolderTreeFilter
{
    // シンボリックリンク/ジャンクションの循環で無限再帰しないための保険。実在のソースツリーは
    // この深さに達しないので、超えたら打ち切る。
    public const int MaxDepth = 64;

    /// <summary>フィルタ結果の1ノード。<see cref="IsReparseLeaf"/> は名前一致した
    /// reparse point（循環の恐れがあるため展開しない葉として見せる）。</summary>
    public sealed record Entry(string FullPath, bool IsDirectory, bool IsReparseLeaf, IReadOnlyList<Entry> Children)
    {
        public static Entry File(string path) => new(path, false, false, Array.Empty<Entry>());
        public static Entry ReparseLeaf(string path) => new(path, true, true, Array.Empty<Entry>());
        public static Entry Directory(string path, IReadOnlyList<Entry> children) => new(path, true, false, children);
    }

    /// <summary>
    /// フィルタ済みツリーを構築する。ignore 判定は階層単位でまとめて問い合わせる
    /// （フォルダ単位の多重起動を避ける）。
    /// </summary>
    /// <param name="matchesName">ファイル/フォルダ名がフィルタに一致するか。</param>
    /// <param name="shouldShow">表示条件（ignore 非表示・変更のみ等）。第3引数は ignore 済みフルパス集合。</param>
    /// <param name="getIgnoredPaths">パス群のうち git ignore されているものを返す（フルパス）。</param>
    /// <param name="computeIgnored">ignore 集合を事前計算するか（=「ignore 非表示」かつ「変更のみ」でない）。</param>
    public static List<Entry> BuildFilteredTree(
        string root,
        Func<string, bool> matchesName,
        Func<string, bool, HashSet<string>, bool> shouldShow,
        Func<IEnumerable<string>, IEnumerable<string>> getIgnoredPaths,
        bool computeIgnored,
        CancellationToken token)
    {
        var ignoredPaths = ComputeIgnoredPaths(root, getIgnoredPaths, computeIgnored, token);
        return BuildFilteredChildren(root, depth: 0, matchesName, shouldShow, ignoredPaths, token);
    }

    // ツリーを階層ごと（BFS）に走査し、git check-ignore を「階層につき 1 回」だけ呼んで
    // ignore 集合をまとめて得る。ignore されたフォルダ・reparse point はその場で打ち切るため、
    // node_modules などの巨大ツリーへ潜らない。git 呼び出し回数は概ね「ツリーの深さ」に収まる。
    public static HashSet<string> ComputeIgnoredPaths(
        string root,
        Func<IEnumerable<string>, IEnumerable<string>> getIgnoredPaths,
        bool computeIgnored,
        CancellationToken token)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!computeIgnored)
            return ignored;

        var frontier = new List<string> { root };
        for (var depth = 0; depth <= MaxDepth && frontier.Count > 0; depth++)
        {
            token.ThrowIfCancellationRequested();

            var levelDirs = new List<string>();
            var levelEntries = new List<string>();
            foreach (var dir in frontier)
            {
                if (TryEnumerate(dir, out var subdirs, out var files))
                {
                    levelDirs.AddRange(subdirs);
                    levelEntries.AddRange(subdirs);
                    levelEntries.AddRange(files);
                }
            }

            if (levelEntries.Count == 0)
                break;

            foreach (var path in getIgnoredPaths(levelEntries))
                ignored.Add(path);

            // 次の階層は「ignore されておらず、reparse でない」サブフォルダのみ。
            frontier = levelDirs
                .Where(d => !ignored.Contains(Path.GetFullPath(d)) && !IsReparsePoint(d))
                .ToList();
        }

        return ignored;
    }

    // 名前が一致するノード（とそこへ至るフォルダ）だけを残す。一致フォルダは呼び出し側が
    // 自動展開して埋もれたヒットを見せる。ignore 集合は事前計算済みなので、ここでは git を呼ばない。
    private static List<Entry> BuildFilteredChildren(
        string path,
        int depth,
        Func<string, bool> matchesName,
        Func<string, bool, HashSet<string>, bool> shouldShow,
        HashSet<string> ignoredPaths,
        CancellationToken token)
    {
        var result = new List<Entry>();
        if (depth > MaxDepth || !TryEnumerate(path, out var directories, out var files))
            return result;

        token.ThrowIfCancellationRequested();

        foreach (var directory in directories.OrderBy(d => Path.GetFileName(d)))
        {
            token.ThrowIfCancellationRequested();

            if (!shouldShow(directory, true, ignoredPaths))
                continue;

            // reparse point（シンボリックリンク/ジャンクション）は循環の恐れがあるので辿らない。
            // 名前が一致する場合だけ、展開しない葉として見せる。
            if (IsReparsePoint(directory))
            {
                if (matchesName(Path.GetFileName(directory)))
                    result.Add(Entry.ReparseLeaf(directory));
                continue;
            }

            var matchingChildren = BuildFilteredChildren(directory, depth + 1, matchesName, shouldShow, ignoredPaths, token);
            if (!matchesName(Path.GetFileName(directory)) && matchingChildren.Count == 0)
                continue;

            result.Add(Entry.Directory(directory, matchingChildren));
        }

        foreach (var file in files.OrderBy(f => Path.GetFileName(f)))
        {
            if (!shouldShow(file, false, ignoredPaths))
                continue;
            if (matchesName(Path.GetFileName(file)))
                result.Add(Entry.File(file));
        }

        return result;
    }

    // 全階層を再帰的に列挙するため、アクセス不可なフォルダがあってもそこだけ飛ばして続行する。
    public static bool TryEnumerate(string path, out string[] directories, out string[] files)
    {
        directories = Array.Empty<string>();
        files = Array.Empty<string>();
        if (!Directory.Exists(path))
            return false;

        try
        {
            directories = Directory.EnumerateDirectories(path).ToArray();
            files = Directory.EnumerateFiles(path).ToArray();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static bool IsReparsePoint(string directory)
    {
        try
        {
            return (new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
