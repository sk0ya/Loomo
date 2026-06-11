using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using sk0ya.Loomo.App.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// FolderTree 検索フィルタ（<see cref="FolderTreeFilter"/>）の検証。
/// 一時フォルダに実ツリーを作って、名前一致・経路保持・ignore 打ち切りの振る舞いを固定する。
/// </summary>
public sealed class FolderTreeFilterTests : IDisposable
{
    private readonly string _root;

    public FolderTreeFilterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "loomo-filter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* テスト後始末の失敗は無視 */ }
    }

    private string MakeDir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private string MakeFile(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private static List<FolderTreeFilter.Entry> Run(
        string root,
        string query,
        Func<IEnumerable<string>, IEnumerable<string>>? getIgnored = null,
        bool computeIgnored = false,
        Func<string, bool, HashSet<string>, bool>? shouldShow = null,
        CancellationToken token = default)
        => FolderTreeFilter.BuildFilteredTree(
            root,
            name => name.Contains(query, StringComparison.OrdinalIgnoreCase),
            shouldShow ?? ((path, _, ignored) => !ignored.Contains(Path.GetFullPath(path))),
            getIgnored ?? (_ => Array.Empty<string>()),
            computeIgnored,
            token);

    [Fact]
    public void Keeps_matching_file_and_folders_on_its_path()
    {
        MakeFile("src", "deep", "target.cs");
        MakeFile("src", "deep", "other.txt");
        MakeFile("unrelated", "note.txt");

        var result = Run(_root, "target");

        // ヒットへ至る src/deep だけが残り、各階層で無関係な項目は落ちる
        var src = Assert.Single(result);
        Assert.True(src.IsDirectory);
        Assert.Equal("src", Path.GetFileName(src.FullPath));
        var deep = Assert.Single(src.Children);
        var file = Assert.Single(deep.Children);
        Assert.Equal("target.cs", Path.GetFileName(file.FullPath));
        Assert.False(file.IsDirectory);
    }

    [Fact]
    public void Matching_folder_is_kept_even_without_matching_children()
    {
        MakeFile("widgets", "a.txt");
        MakeFile("widgets", "b.txt");

        var result = Run(_root, "widget");

        var dir = Assert.Single(result);
        Assert.Equal("widgets", Path.GetFileName(dir.FullPath));
        // フォルダ名が一致した場合、その配下のうち「名前が一致する子」だけが残る
        // （現行仕様：一致フォルダでも子は個別にフィルタされる）
        Assert.Empty(dir.Children);
    }

    [Fact]
    public void Result_is_sorted_by_name_directories_first()
    {
        MakeFile("b-dir", "match.txt");
        MakeFile("a-dir", "match.txt");
        MakeFile("match-root.txt");

        var result = Run(_root, "match");

        Assert.Equal(
            new[] { "a-dir", "b-dir", "match-root.txt" },
            result.Select(e => Path.GetFileName(e.FullPath)));
    }

    [Fact]
    public void Empty_when_nothing_matches()
    {
        MakeFile("src", "main.cs");
        Assert.Empty(Run(_root, "zzz-no-hit"));
    }

    [Fact]
    public void ShouldShow_filters_out_entries_and_their_subtrees()
    {
        MakeFile("bin", "match.dll");
        MakeFile("src", "match.cs");

        var binPath = Path.Combine(_root, "bin");
        var result = Run(_root, "match",
            shouldShow: (path, _, _) => !string.Equals(path, binPath, StringComparison.OrdinalIgnoreCase));

        var src = Assert.Single(result);
        Assert.Equal("src", Path.GetFileName(src.FullPath));
    }

    [Fact]
    public void ComputeIgnoredPaths_queries_per_level_and_stops_descending_into_ignored_dirs()
    {
        MakeFile("node_modules", "pkg", "index.js");
        MakeFile("src", "app.js");

        var queried = new List<string[]>();
        var nodeModules = Path.Combine(_root, "node_modules");

        var ignored = FolderTreeFilter.ComputeIgnoredPaths(
            _root,
            paths =>
            {
                var snapshot = paths.ToArray();
                queried.Add(snapshot);
                return snapshot.Where(p => p.StartsWith(nodeModules, StringComparison.OrdinalIgnoreCase));
            },
            computeIgnored: true,
            CancellationToken.None);

        Assert.Contains(nodeModules, ignored);
        // ignore されたフォルダの中身は次階層の問い合わせに含まれない（巨大ツリーへ潜らない）
        Assert.DoesNotContain(queried.SelectMany(q => q),
            p => p.Contains("pkg", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComputeIgnoredPaths_returns_empty_when_disabled()
    {
        MakeFile("src", "a.txt");
        var called = false;

        var ignored = FolderTreeFilter.ComputeIgnoredPaths(
            _root, paths => { called = true; return paths; }, computeIgnored: false, CancellationToken.None);

        Assert.Empty(ignored);
        Assert.False(called); // 無効時は git に問い合わせない
    }

    [Fact]
    public void Cancellation_throws_OperationCanceled()
    {
        MakeFile("src", "match.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Run(_root, "match", token: cts.Token));
    }

    [Fact]
    public void TryEnumerate_returns_false_for_missing_directory()
    {
        Assert.False(FolderTreeFilter.TryEnumerate(Path.Combine(_root, "no-such-dir"), out var dirs, out var files));
        Assert.Empty(dirs);
        Assert.Empty(files);
    }
}
