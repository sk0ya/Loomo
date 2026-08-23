using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services.Search;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>ファイル名検索・grep バックエンドの検証。純ロジック（FuzzyMatcher / RgOutputParser）と、
/// 一時ワークスペースに対する WorkspaceSearchService の結合（rg があれば rg、無ければインプロセス）。</summary>
public class WorkspaceSearchTests
{
    // ===== FuzzyMatcher =====

    [Theory]
    [InlineData("Program.cs", "prog", true)]   // 前方一致
    [InlineData("Program.cs", "gram", true)]   // 部分一致
    [InlineData("Program.cs", "pgcs", true)]   // 飛び石
    [InlineData("Program.cs", "xyz", false)]   // 不一致
    public void FuzzyMatcher_matches_expected(string text, string query, bool expected)
        => Assert.Equal(expected, FuzzyMatcher.Score(text, query) is not null);

    [Fact]
    public void FuzzyMatcher_prefix_beats_contains_beats_subsequence()
    {
        var prefix = FuzzyMatcher.Score("readme.md", "read");
        var contains = FuzzyMatcher.Score("the-readme.md", "read");
        var subseq = FuzzyMatcher.Score("r-e-a-d.md", "read");
        Assert.NotNull(prefix);
        Assert.NotNull(contains);
        Assert.NotNull(subseq);
        Assert.True(prefix < contains);
        Assert.True(contains < subseq);
    }

    [Fact]
    public void FuzzyMatcher_empty_query_always_matches()
        => Assert.Equal(0, FuzzyMatcher.Score("anything", "  "));

    [Fact]
    public void FuzzyMatcher_all_tokens_must_match()
    {
        Assert.NotNull(FuzzyMatcher.Score("foo bar", "foo bar"));
        Assert.Null(FuzzyMatcher.Score("foo", "foo zzz"));
    }

    // ===== RgOutputParser =====

    [Fact]
    public void Parser_splits_path_line_col_text()
    {
        var p = RgOutputParser.ParseVimgrep("src/app.cs:12:5:var x = 1;");
        Assert.NotNull(p);
        Assert.Equal("src/app.cs", p!.Value.Path);
        Assert.Equal(12, p.Value.Line);
        Assert.Equal(5, p.Value.Column);
        Assert.Equal("var x = 1;", p.Value.Text);
    }

    [Fact]
    public void Parser_keeps_colons_in_match_text()
    {
        var p = RgOutputParser.ParseVimgrep("a.txt:1:1:http://example.com:8080");
        Assert.NotNull(p);
        Assert.Equal("http://example.com:8080", p!.Value.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no colons here")]
    [InlineData("path:notanumber:1:text")]
    [InlineData(":1:1:text")]      // 空パス
    public void Parser_rejects_malformed(string line)
        => Assert.Null(RgOutputParser.ParseVimgrep(line));

    // ===== WorkspaceSearchService（一時ワークスペース結合） =====

    private static (WorkspaceSearchService svc, string root) NewWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "LoomoSearchTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "README.md"), "# Title\nhello world\n");
        File.WriteAllText(Path.Combine(root, "src", "app.cs"), "class App { }\n// needle here\n");
        File.WriteAllText(Path.Combine(root, "src", "util.cs"), "static int Add() => 0;\n");

        var ws = new FakeWorkspaceService();
        ws.OpenFolder(root);
        return (new WorkspaceSearchService(ws), root);
    }

    [Fact]
    public async Task FindFiles_ranks_name_match_first()
    {
        var (svc, root) = NewWorkspace();
        try
        {
            var hits = await svc.FindFilesAsync("app", 20, CancellationToken.None);
            Assert.Contains(hits, h => h.RelativePath.EndsWith("app.cs", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("src/app.cs", hits[0].RelativePath); // 名前一致が最上位
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Grep_finds_literal_text_with_location()
    {
        var (svc, root) = NewWorkspace();
        try
        {
            var hits = await svc.GrepAsync("needle", new GrepOptions(), CancellationToken.None);
            var hit = Assert.Single(hits);
            Assert.EndsWith("app.cs", hit.RelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, hit.Line);
            Assert.Contains("needle", hit.LineText);
            Assert.True(File.Exists(hit.FullPath));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Grep_case_insensitive_by_default_sensitive_when_requested()
    {
        var (svc, root) = NewWorkspace();
        try
        {
            Assert.NotEmpty(await svc.GrepAsync("NEEDLE", new GrepOptions(), CancellationToken.None));
            Assert.Empty(await svc.GrepAsync("NEEDLE",
                new GrepOptions(CaseSensitive: true), CancellationToken.None));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Grep_literal_query_is_not_treated_as_regex()
    {
        var (svc, root) = NewWorkspace();
        try
        {
            // "needle here" の "." は正規表現なら任意1文字に化けるが、既定(固定文字列)では一致しない。
            Assert.Empty(await svc.GrepAsync("needle.here", new GrepOptions(), CancellationToken.None));
            Assert.NotEmpty(await svc.GrepAsync("needle.here",
                new GrepOptions(UseRegex: true), CancellationToken.None));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Grep_searchRoot_limits_to_subfolder()
    {
        var (svc, root) = NewWorkspace();
        try
        {
            // src 配下に絞れば needle は見つかる。
            Assert.NotEmpty(await svc.GrepAsync(
                "needle", new GrepOptions(), CancellationToken.None, Path.Combine(root, "src")));

            // needle を含まない別フォルダに絞れば 0 件。
            var docs = Path.Combine(root, "docs");
            Directory.CreateDirectory(docs);
            File.WriteAllText(Path.Combine(docs, "guide.md"), "no match here\n");
            Assert.Empty(await svc.GrepAsync(
                "needle", new GrepOptions(), CancellationToken.None, docs));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Grep_searchRoot_accepts_relative_path()
    {
        var (svc, root) = NewWorkspace();
        try
        {
            // 相対パス（ワークスペースルート基準）でも src 配下に絞れる。
            Assert.NotEmpty(await svc.GrepAsync(
                "needle", new GrepOptions(), CancellationToken.None, "src"));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Grep_searchRoot_outside_workspace_falls_back_to_root()
    {
        var (svc, root) = NewWorkspace();
        try
        {
            // ワークスペース外のフォルダを渡してもルート全体へ退避するので needle は見つかる。
            var hits = await svc.GrepAsync(
                "needle", new GrepOptions(), CancellationToken.None, Path.GetTempPath());
            Assert.NotEmpty(hits);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task FindFiles_searchRoot_limits_to_subfolder()
    {
        var (svc, root) = NewWorkspace();
        try
        {
            // src 配下に絞ると README.md（ルート直下）は出ない。
            var hits = await svc.FindFilesAsync(
                "", 50, CancellationToken.None, Path.Combine(root, "src"));
            Assert.All(hits, h => Assert.DoesNotContain("README", h.RelativePath, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(hits, h => h.RelativePath.EndsWith("app.cs", StringComparison.OrdinalIgnoreCase));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task AdvancedSearch_combines_name_content_kind_size_and_date_filters()
    {
        var (svc, root) = NewWorkspace();
        try
        {
            var app = Path.Combine(root, "src", "app.cs");
            File.SetLastWriteTimeUtc(app, new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc));
            var hits = await svc.SearchFilesAsync(new AdvancedSearchOptions(
                FileNameQuery: "app", ContentQuery: "needle", ExtensionGlob: "*.cs",
                Kind: SearchFileKind.Code, MinimumSize: 1, MaximumSize: 10_000,
                ModifiedFrom: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ModifiedTo: new DateTime(2025, 12, 31, 23, 59, 59, DateTimeKind.Utc)),
                CancellationToken.None);

            var hit = Assert.Single(hits);
            Assert.EndsWith("src/app.cs", hit.RelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.Single(hit.ContentMatches);
            Assert.Equal(2, hit.ContentMatches[0].Line);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task AdvancedSearch_searchRoot_is_recursive_and_does_not_follow_reparse_points()
    {
        var (svc, root) = NewWorkspace();
        try
        {
            var deep = Path.Combine(root, "src", "deep", "one", "two");
            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(deep, "needle.txt"), "inside\n");
            var hits = await svc.SearchFilesAsync(new AdvancedSearchOptions(ContentQuery: "inside"),
                CancellationToken.None, Path.Combine(root, "src"));
            Assert.Contains(hits, h => h.RelativePath.EndsWith("deep/one/two/needle.txt", StringComparison.OrdinalIgnoreCase));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task AdvancedSearch_cancellation_is_observed()
    {
        var (svc, root) = NewWorkspace();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                svc.SearchFilesAsync(new AdvancedSearchOptions(ContentQuery: "needle"), cts.Token));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task AdvancedSearch_invalid_regex_returns_no_results_without_throwing()
    {
        var (svc, root) = NewWorkspace();
        try
        {
            var hits = await svc.SearchFilesAsync(new AdvancedSearchOptions(ContentQuery: "[", UseRegex: true),
                CancellationToken.None);
            Assert.Empty(hits);
        }
        finally { TryDelete(root); }
    }

    // ===== マルチルート（複数ワークスペースフォルダー） =====

    [Fact]
    public async Task GrepAsync_default_scope_searches_all_workspace_folders()
    {
        var (svc, root) = NewWorkspace();
        var second = Path.Combine(Path.GetTempPath(), "LoomoSearchTest2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "other.cs"), "// needle in second folder\n");

        try
        {
            var ws = new FakeWorkspaceService();
            ws.OpenFolder(root);
            ws.AddFolder(second);
            var multiSvc = new WorkspaceSearchService(ws);

            var hits = await multiSvc.GrepAsync("needle", new GrepOptions(), CancellationToken.None);

            Assert.Contains(hits, h => h.FullPath.Equals(
                Path.GetFullPath(Path.Combine(root, "src", "app.cs")), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(hits, h => h.FullPath.Equals(
                Path.GetFullPath(Path.Combine(second, "other.cs")), StringComparison.OrdinalIgnoreCase));

            // 複数フォルダー横断時は表示パスの先頭にフォルダー名を付けて区別する。
            var secondName = Path.GetFileName(second);
            Assert.Contains(hits, h => h.RelativePath.StartsWith(secondName + "/", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
            TryDelete(second);
        }
    }

    [Fact]
    public async Task AdvancedSearch_reads_utf16_bom_and_shift_jis_text()
    {
        var (svc, root) = NewWorkspace();
        try
        {
            var utf16 = Path.Combine(root, "src", "utf16.txt");
            File.WriteAllText(utf16, "先頭\nUTF16 needle\n", Encoding.Unicode);

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var shiftJis = Path.Combine(root, "src", "legacy.txt");
            File.WriteAllText(shiftJis, "legacy 日本語 needle\n",
                Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback));

            var hits = await svc.SearchFilesAsync(new AdvancedSearchOptions(ContentQuery: "needle"),
                CancellationToken.None);

            Assert.Contains(hits, h => h.RelativePath.EndsWith("utf16.txt", StringComparison.OrdinalIgnoreCase)
                && h.ContentMatches[0].Line == 2);
            Assert.Contains(hits, h => h.RelativePath.EndsWith("legacy.txt", StringComparison.OrdinalIgnoreCase));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task AdvancedSearch_skips_oversized_content_but_keeps_metadata_search()
    {
        var (svc, root) = NewWorkspace();
        try
        {
            var large = Path.Combine(root, "src", "large.txt");
            File.WriteAllText(large, "needle\n");
            using (var stream = new FileStream(large, FileMode.Open, FileAccess.Write, FileShare.Read))
                stream.SetLength(8_000_001);

            var contentHits = await svc.SearchFilesAsync(
                new AdvancedSearchOptions(ContentQuery: "needle"), CancellationToken.None);
            Assert.DoesNotContain(contentHits, h => h.FullPath.Equals(large, StringComparison.OrdinalIgnoreCase));

            var metadataHits = await svc.SearchFilesAsync(
                new AdvancedSearchOptions(FileNameQuery: "large"), CancellationToken.None);
            var metadataHit = Assert.Single(metadataHits);
            Assert.Equal(8_000_001, metadataHit.Size);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task AdvancedSearch_does_not_follow_symbolic_link_directory()
    {
        var (svc, root) = NewWorkspace();
        var outside = Path.Combine(Path.GetTempPath(), "LoomoSearchOutside_" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(root, "src", "linked");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside-secret\n");
        try
        {
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                _ = ex;
                // CI や開発者モード無効の Windows では作成権限がないため、環境依存の検証は行わない。
                return;
            }

            var hits = await svc.SearchFilesAsync(new AdvancedSearchOptions(ContentQuery: "outside-secret"),
                CancellationToken.None);
            Assert.DoesNotContain(hits, h => h.FullPath.Contains("LoomoSearchOutside_", StringComparison.OrdinalIgnoreCase));

            var explicitLinkHits = await svc.SearchFilesAsync(
                new AdvancedSearchOptions(ContentQuery: "outside-secret"), CancellationToken.None, link);
            Assert.DoesNotContain(explicitLinkHits,
                h => h.FullPath.Contains("LoomoSearchOutside_", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Fact]
    public async Task FindFilesAsync_default_scope_searches_all_workspace_folders()
    {
        var (svc, root) = NewWorkspace();
        var second = Path.Combine(Path.GetTempPath(), "LoomoSearchTest2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "other.cs"), "// content\n");

        try
        {
            var ws = new FakeWorkspaceService();
            ws.OpenFolder(root);
            ws.AddFolder(second);
            var multiSvc = new WorkspaceSearchService(ws);

            var hits = await multiSvc.FindFilesAsync("", 50, CancellationToken.None);

            Assert.Contains(hits, h => h.RelativePath.EndsWith("app.cs", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(hits, h => h.RelativePath.EndsWith("other.cs", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
            TryDelete(second);
        }
    }

    [Fact]
    public async Task GrepAsync_explicit_searchRoot_in_secondary_folder_is_not_ignored()
    {
        var (svc, root) = NewWorkspace();
        var second = Path.Combine(Path.GetTempPath(), "LoomoSearchTest2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "other.cs"), "// needle in second folder\n");

        try
        {
            var ws = new FakeWorkspaceService();
            ws.OpenFolder(root);
            ws.AddFolder(second);
            var multiSvc = new WorkspaceSearchService(ws);

            // 明示的にセカンダリフォルダー自身を searchRoot に指定 → プライマリへ迷子扱いされず絞れる。
            var hits = await multiSvc.GrepAsync("needle", new GrepOptions(), CancellationToken.None, second);

            var hit = Assert.Single(hits);
            Assert.Equal("other.cs", hit.RelativePath);
        }
        finally
        {
            TryDelete(root);
            TryDelete(second);
        }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* ベストエフォート */ }
    }
}
